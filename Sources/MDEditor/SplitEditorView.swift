import SwiftUI
import AppKit
import WebKit
import UniformTypeIdentifiers

enum ViewMode: String, CaseIterable, Identifiable {
    case editor, split, preview

    var id: String { rawValue }
    var showsEditor: Bool { self != .preview }
    var showsPreview: Bool { self != .editor }
}

/// Side-by-side container. A tiny NSSplitView subclass so we control the initial 50/50 split
/// and can answer File ▸ Print through the responder chain.
final class EditorSplitView: NSSplitView {
    var onPrint: (() -> Void)?
    var onMoveToWindow: ((NSWindow) -> Void)?
    private var needsCentering = true

    func recenter() {
        needsCentering = true
        needsLayout = true
    }

    override func layout() {
        super.layout()
        guard needsCentering, bounds.width > 200, arrangedSubviews.allSatisfy({ !$0.isHidden }) else { return }
        needsCentering = false
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.setPosition(self.bounds.width / 2, ofDividerAt: 0)
        }
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let window { onMoveToWindow?(window) }
    }

    @objc func printDocument(_ sender: Any?) { onPrint?() }
}

/// Forwards script messages without retaining the coordinator (WKUserContentController retains handlers).
private final class WeakScriptHandler: NSObject, WKScriptMessageHandler {
    weak var target: WKScriptMessageHandler?
    init(_ target: WKScriptMessageHandler) { self.target = target }
    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        target?.userContentController(controller, didReceive: message)
    }
}

struct SplitEditorView: NSViewRepresentable {
    @Binding var text: String
    var fileURL: URL?
    var mode: ViewMode
    var zoom: Double

    static let baseFontSize: CGFloat = 14

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> EditorSplitView {
        context.coordinator.makeView()
    }

    func updateNSView(_ nsView: EditorSplitView, context: Context) {
        context.coordinator.update(self)
    }

    static func dismantleNSView(_ nsView: EditorSplitView, coordinator: Coordinator) {
        coordinator.teardown()
    }

    // MARK: - Coordinator

    final class Coordinator: NSObject, NSTextViewDelegate, NSSplitViewDelegate, WKNavigationDelegate, WKScriptMessageHandler {
        var parent: SplitEditorView

        private let highlighter: MarkdownHighlighter
        private var split: EditorSplitView!
        private var scrollView: NSScrollView!
        private var textView: MarkdownTextView!
        private var webView: WKWebView!

        private var pageReady = false
        private var didInitialPlacement = false
        private var appliedMode: ViewMode?
        private var appliedZoom: Double
        private var appliedBase: String?
        private var lastRendered: String?
        private var renderWork: DispatchWorkItem?
        private var suppressEditorScrollUntil = Date.distantPast

        private var lineStarts: [Int] = [0]
        private var lineStartsValid = false

        init(_ parent: SplitEditorView) {
            self.parent = parent
            self.appliedZoom = parent.zoom
            self.highlighter = MarkdownHighlighter(fontSize: SplitEditorView.baseFontSize * parent.zoom)
            super.init()
        }

        // MARK: Construction

        func makeView() -> EditorSplitView {
            // --- editor (TextKit 1: exact line geometry for scroll syncing)
            let storage = NSTextStorage()
            let layoutManager = NSLayoutManager()
            layoutManager.allowsNonContiguousLayout = true
            storage.addLayoutManager(layoutManager)
            // Start with a sane width: a zero-width container makes TextKit hang (see MarkdownTextView).
            let initialSize = NSSize(width: 480, height: 400)
            let container = NSTextContainer(size: NSSize(width: initialSize.width, height: CGFloat.greatestFiniteMagnitude))
            container.widthTracksTextView = false   // MarkdownTextView keeps the width in sync, clamped
            layoutManager.addTextContainer(container)

            let tv = MarkdownTextView(frame: NSRect(origin: .zero, size: initialSize), textContainer: container)
            tv.minSize = .zero
            tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
            tv.isVerticallyResizable = true
            tv.isHorizontallyResizable = false
            tv.autoresizingMask = [.width]
            tv.isRichText = false
            tv.importsGraphics = false
            tv.allowsUndo = true
            tv.usesFindBar = true
            tv.isIncrementalSearchingEnabled = true
            tv.isAutomaticQuoteSubstitutionEnabled = false
            tv.isAutomaticDashSubstitutionEnabled = false
            tv.isAutomaticTextReplacementEnabled = false
            tv.isAutomaticSpellingCorrectionEnabled = false
            tv.isAutomaticLinkDetectionEnabled = false
            tv.isAutomaticDataDetectionEnabled = false
            tv.smartInsertDeleteEnabled = false
            tv.isContinuousSpellCheckingEnabled = false
            tv.textContainerInset = NSSize(width: 18, height: 16)
            tv.backgroundColor = .textBackgroundColor
            tv.insertionPointColor = .controlAccentColor
            tv.font = highlighter.baseFont
            tv.typingAttributes = highlighter.baseAttributes
            tv.delegate = self
            tv.string = parent.text
            textView = tv

            let sv = NSScrollView(frame: NSRect(origin: .zero, size: initialSize))
            sv.hasVerticalScroller = true
            sv.autohidesScrollers = true
            sv.borderType = .noBorder
            sv.drawsBackground = true
            sv.backgroundColor = .textBackgroundColor
            sv.documentView = tv
            sv.contentView.postsBoundsChangedNotifications = true
            scrollView = sv
            NotificationCenter.default.addObserver(
                self, selector: #selector(editorScrolled(_:)),
                name: NSView.boundsDidChangeNotification, object: sv.contentView)

            // --- preview
            let config = WKWebViewConfiguration()
            for name in ["ready", "scroll", "link"] {
                config.userContentController.add(WeakScriptHandler(self), name: name)
            }
            let web = WKWebView(frame: .zero, configuration: config)
            web.navigationDelegate = self
            web.underPageBackgroundColor = .textBackgroundColor
            web.pageZoom = parent.zoom
            webView = web
            loadPreviewPage()

            // --- container
            let container2 = EditorSplitView()
            container2.isVertical = true
            container2.dividerStyle = .thin
            container2.delegate = self
            container2.addArrangedSubview(sv)
            container2.addArrangedSubview(web)
            container2.onPrint = { [weak self] in self?.printPreview() }
            container2.onMoveToWindow = { [weak self] window in
                DispatchQueue.main.async {
                    guard let self else { return }
                    if !self.didInitialPlacement {
                        // A freshly opened document starts at the top with the caret at the start.
                        self.didInitialPlacement = true
                        self.textView.setSelectedRange(NSRange(location: 0, length: 0))
                        self.scrollView.contentView.scroll(to: .zero)
                        self.scrollView.reflectScrolledClipView(self.scrollView.contentView)
                    }
                    if self.appliedMode?.showsEditor ?? true { window.makeFirstResponder(self.textView) }
                }
            }
            split = container2

            rehighlight()
            return container2
        }

        func teardown() {
            NotificationCenter.default.removeObserver(self)
            renderWork?.cancel()
            for name in ["ready", "scroll", "link"] {
                webView?.configuration.userContentController.removeScriptMessageHandler(forName: name)
            }
            webView?.navigationDelegate = nil
            webView?.stopLoading()
        }

        private func loadPreviewPage() {
            var index = Bundle.main.url(forResource: "index", withExtension: "html", subdirectory: "preview")
            if index == nil {
                // `swift run` from the repository root
                let dev = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
                    .appendingPathComponent("Resources/preview/index.html")
                if FileManager.default.fileExists(atPath: dev.path) { index = dev }
            }
            guard let index else {
                webView.loadHTMLString("<body style='font:14px -apple-system;padding:2em'>Preview resources are missing.</body>", baseURL: nil)
                return
            }
            webView.loadFileURL(index, allowingReadAccessTo: URL(fileURLWithPath: "/"))
        }

        // MARK: SwiftUI → AppKit

        func update(_ parent: SplitEditorView) {
            self.parent = parent

            if appliedZoom != parent.zoom {
                appliedZoom = parent.zoom
                highlighter.setFontSize(SplitEditorView.baseFontSize * parent.zoom)
                textView.font = highlighter.baseFont
                webView.pageZoom = parent.zoom
                rehighlight()
            }

            // Text changed from outside (revert, reopen, undo of a file-level change…)
            if !textView.hasMarkedText(), textView.string != parent.text {
                setEditorText(parent.text)
            }

            if appliedMode != parent.mode { applyMode(parent.mode) }
            applyBase()
            scheduleRender()
        }

        private func setEditorText(_ text: String) {
            let selection = textView.selectedRange()
            textView.string = text
            let length = (text as NSString).length
            textView.setSelectedRange(NSRange(location: min(selection.location, length), length: 0))
            lineStartsValid = false
            rehighlight()
        }

        private func applyMode(_ mode: ViewMode) {
            let previous = appliedMode
            appliedMode = mode
            scrollView.isHidden = !mode.showsEditor
            webView.isHidden = !mode.showsPreview
            split.adjustSubviews()
            if mode == .split { split.recenter() }
            if mode.showsPreview, previous?.showsPreview != true { lastRendered = nil }
            if mode.showsEditor, previous?.showsEditor != true {
                DispatchQueue.main.async { [weak self] in
                    guard let self else { return }
                    self.textView.window?.makeFirstResponder(self.textView)
                }
            }
        }

        private func applyBase() {
            guard pageReady, let dir = parent.fileURL?.deletingLastPathComponent() else { return }
            var base = dir.absoluteString
            if !base.hasSuffix("/") { base += "/" }
            guard base != appliedBase else { return }
            appliedBase = base
            lastRendered = nil
            webView.callAsyncJavaScript("setBase(url)", arguments: ["url": base], in: nil, in: .page, completionHandler: nil)
        }

        private func rehighlight() {
            textView.typingAttributes = highlighter.baseAttributes
            if let storage = textView.textStorage { highlighter.apply(to: storage) }
        }

        // MARK: Rendering

        private func scheduleRender() {
            guard pageReady, appliedMode?.showsPreview == true, lastRendered != parent.text else { return }
            renderWork?.cancel()
            let work = DispatchWorkItem { [weak self] in self?.renderNow() }
            renderWork = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.04, execute: work)
        }

        private func renderNow() {
            guard pageReady else { return }
            let text = parent.text
            lastRendered = text
            let pos = editorPosition()
            webView.callAsyncJavaScript(
                "render(text, line, bottom)",
                arguments: ["text": text, "line": pos.line, "bottom": pos.atBottom],
                in: nil, in: .page
            ) { result in
                if case .failure(let error) = result { NSLog("MDEditor: render failed: %@", String(describing: error)) }
            }
        }

        // MARK: NSTextViewDelegate

        func textDidChange(_ notification: Notification) {
            lineStartsValid = false
            // While an input method is composing (pinyin, kana…) leave the text alone.
            guard !textView.hasMarkedText() else { return }
            rehighlight()
            let s = textView.string
            if parent.text != s { parent.text = s }
            scheduleRender()
        }

        // MARK: Scroll sync (by source line)

        private func ensureLineStarts() {
            guard !lineStartsValid else { return }
            let ns = textView.textStorage!.mutableString
            let n = ns.length
            var buffer = [unichar](repeating: 0, count: n)
            if n > 0 { ns.getCharacters(&buffer, range: NSRange(location: 0, length: n)) }
            var starts = [0]
            starts.reserveCapacity(n / 30 + 1)
            for (i, c) in buffer.enumerated() where c == 0x0A { starts.append(i + 1) }
            lineStarts = starts
            lineStartsValid = true
        }

        private func lineIndex(forChar index: Int) -> Int {
            var lo = 0, hi = lineStarts.count - 1
            while lo < hi {
                let mid = (lo + hi + 1) / 2
                if lineStarts[mid] <= index { lo = mid } else { hi = mid - 1 }
            }
            return lo
        }

        /// Vertical extent (in text-view coordinates) of one logical line, including soft-wrapped rows.
        private func lineRect(_ line: Int) -> NSRect {
            guard let lm = textView.layoutManager else { return .zero }
            let origin = textView.textContainerOrigin
            let length = textView.textStorage!.length
            let start = min(lineStarts[line], length)
            let end = min(line + 1 < lineStarts.count ? lineStarts[line + 1] : length, length)

            if start >= length || end <= start {
                let extra = lm.extraLineFragmentRect
                if extra.height > 0 { return NSRect(x: 0, y: extra.minY + origin.y, width: extra.width, height: extra.height) }
                let last = max(lm.numberOfGlyphs - 1, 0)
                let r = lm.lineFragmentRect(forGlyphAt: last, effectiveRange: nil)
                return NSRect(x: 0, y: r.maxY + origin.y, width: r.width, height: r.height)
            }
            let glyphs = lm.glyphRange(forCharacterRange: NSRange(location: start, length: end - start), actualCharacterRange: nil)
            let first = lm.lineFragmentRect(forGlyphAt: glyphs.location, effectiveRange: nil)
            let last = lm.lineFragmentRect(forGlyphAt: max(glyphs.location + glyphs.length - 1, glyphs.location), effectiveRange: nil)
            return NSRect(x: 0, y: first.minY + origin.y, width: first.width, height: max(last.maxY - first.minY, 1))
        }

        /// Fractional source line at the top of the editor's visible area.
        private func editorPosition() -> (line: Double, atBottom: Bool) {
            guard let lm = textView.layoutManager, let tc = textView.textContainer, lm.numberOfGlyphs > 0 else {
                return (0, false)
            }
            let visible = scrollView.contentView.bounds
            let maxY = max(textView.frame.height - visible.height, 0)
            let atBottom = maxY > 1 && visible.minY >= maxY - 1

            ensureLineStarts()
            let origin = textView.textContainerOrigin
            let point = NSPoint(x: tc.lineFragmentPadding + 1, y: max(visible.minY - origin.y, 0))
            let glyph = lm.glyphIndex(for: point, in: tc)
            let line = lineIndex(forChar: lm.characterIndexForGlyph(at: glyph))
            let rect = lineRect(line)
            let frac = rect.height > 0 ? min(max((visible.minY - rect.minY) / rect.height, 0), 1) : 0
            return (Double(line) + Double(frac), atBottom)
        }

        private func scrollEditor(toLine x: Double, bottom: Bool) {
            let clip = scrollView.contentView
            let maxY = max(textView.frame.height - clip.bounds.height, 0)
            var y: CGFloat
            if bottom {
                y = maxY
            } else {
                ensureLineStarts()
                let line = min(max(Int(floor(x)), 0), lineStarts.count - 1)
                let rect = lineRect(line)
                y = rect.minY + CGFloat(x - floor(x)) * rect.height
            }
            y = min(max(y, 0), maxY)
            guard abs(clip.bounds.origin.y - y) >= 0.5 else { return }
            suppressEditorScrollUntil = Date().addingTimeInterval(0.15)
            clip.scroll(to: NSPoint(x: 0, y: y))
            scrollView.reflectScrolledClipView(clip)
        }

        @objc private func editorScrolled(_ note: Notification) {
            guard pageReady, appliedMode?.showsPreview == true, Date() >= suppressEditorScrollUntil else { return }
            let pos = editorPosition()
            guard pos.line.isFinite else { return }
            webView.evaluateJavaScript("syncToLine(\(pos.line), \(pos.atBottom))", completionHandler: nil)
        }

        // MARK: WKScriptMessageHandler

        func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
            switch message.name {
            case "ready":
                pageReady = true
                appliedBase = nil
                lastRendered = nil
                applyBase()
                scheduleRender()
            case "scroll":
                guard appliedMode?.showsEditor == true,
                      let body = message.body as? [String: Any],
                      let line = (body["line"] as? NSNumber)?.doubleValue, line.isFinite else { return }
                scrollEditor(toLine: line, bottom: (body["bottom"] as? Bool) ?? false)
            case "link":
                if let href = message.body as? String { openLink(href) }
            default:
                break
            }
        }

        // MARK: WKNavigationDelegate

        func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                     decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            // Only the initial local page load is allowed; links are handled by app.js → openLink.
            decisionHandler(navigationAction.navigationType == .other ? .allow : .cancel)
        }

        func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
            pageReady = false
            lastRendered = nil
            loadPreviewPage()
        }

        // MARK: Links

        private static let openableTypes: [UTType] = [.image, .pdf, .text, .audiovisualContent, .html]

        private func openLink(_ href: String) {
            if let url = URL(string: href), let scheme = url.scheme?.lowercased() {
                if ["http", "https", "mailto", "tel"].contains(scheme) { NSWorkspace.shared.open(url) }
                return
            }
            guard let dir = parent.fileURL?.deletingLastPathComponent() else { NSSound.beep(); return }
            let pathPart = href.split(separator: "#", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? href
            let decoded = pathPart.removingPercentEncoding ?? pathPart
            guard !decoded.isEmpty else { return }
            let target = URL(fileURLWithPath: decoded, relativeTo: dir).standardizedFileURL
            guard FileManager.default.fileExists(atPath: target.path) else { NSSound.beep(); return }

            let ext = target.pathExtension.lowercased()
            if ["md", "markdown", "mdown", "mkd", "txt"].contains(ext) {
                NSDocumentController.shared.openDocument(withContentsOf: target, display: true) { _, _, _ in }
            } else if let type = UTType(filenameExtension: ext), Self.openableTypes.contains(where: { type.conforms(to: $0) }) {
                NSWorkspace.shared.open(target)
            } else {
                // Never launch scripts/apps from a link inside a document: just show them in Finder.
                NSWorkspace.shared.activateFileViewerSelecting([target])
            }
        }

        // MARK: Print

        func printPreview() {
            guard let window = split.window else { return }
            let info = (NSPrintInfo.shared.copy() as? NSPrintInfo) ?? NSPrintInfo.shared
            info.horizontalPagination = .fit
            info.isVerticallyCentered = false
            info.topMargin = 40; info.bottomMargin = 40; info.leftMargin = 40; info.rightMargin = 40
            let operation = webView.printOperation(with: info)
            operation.view?.frame = NSRect(x: 0, y: 0, width: info.paperSize.width, height: info.paperSize.height)
            operation.jobTitle = window.title
            operation.showsPrintPanel = true
            operation.runModal(for: window, delegate: nil, didRun: nil, contextInfo: nil)
        }

        // MARK: NSSplitViewDelegate

        func splitView(_ splitView: NSSplitView, constrainMinCoordinate proposedMinimumPosition: CGFloat,
                       ofSubviewAt dividerIndex: Int) -> CGFloat {
            proposedMinimumPosition + 240
        }

        func splitView(_ splitView: NSSplitView, constrainMaxCoordinate proposedMaximumPosition: CGFloat,
                       ofSubviewAt dividerIndex: Int) -> CGFloat {
            proposedMaximumPosition - 240
        }
    }
}
