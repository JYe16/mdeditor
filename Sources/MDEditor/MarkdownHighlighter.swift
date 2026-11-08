import AppKit

/// Lightweight Markdown syntax colouring for the editor pane.
///
/// Only colour, weight and slant change (never size), so layout is stable and the
/// preview's line mapping stays exact. Each regex runs once over the whole text;
/// matches that fall inside fenced code blocks are skipped.
final class MarkdownHighlighter {
    /// Above this size highlighting is skipped to keep typing instant.
    static let maxLength = 300_000

    private(set) var baseFont: NSFont
    private var boldFont: NSFont
    private var paragraph: NSMutableParagraphStyle

    init(fontSize: CGFloat) {
        baseFont = NSFont.monospacedSystemFont(ofSize: fontSize, weight: .regular)
        boldFont = NSFont.monospacedSystemFont(ofSize: fontSize, weight: .bold)
        paragraph = Self.makeParagraph(fontSize: fontSize)
    }

    func setFontSize(_ size: CGFloat) {
        baseFont = NSFont.monospacedSystemFont(ofSize: size, weight: .regular)
        boldFont = NSFont.monospacedSystemFont(ofSize: size, weight: .bold)
        paragraph = Self.makeParagraph(fontSize: size)
    }

    private static func makeParagraph(fontSize: CGFloat) -> NSMutableParagraphStyle {
        let p = NSMutableParagraphStyle()
        p.lineSpacing = fontSize * 0.32
        return p
    }

    var baseAttributes: [NSAttributedString.Key: Any] {
        [.font: baseFont, .foregroundColor: NSColor.labelColor, .paragraphStyle: paragraph]
    }

    // MARK: - Patterns

    private static func re(_ pattern: String, _ options: NSRegularExpression.Options = []) -> NSRegularExpression {
        // Patterns are literals in this file; a failure is a programming error.
        // swiftlint:disable:next force_try
        try! NSRegularExpression(pattern: pattern, options: options)
    }

    private static let inlineCode = re("(`+)(?!`)[^\\n]*?[^`\\n](\\1)(?!`)|`[^`\\n]`")
    private static let bold       = re("(\\*\\*|__)(?=\\S)([^\\n]*?\\S)\\1")
    private static let italic     = re("(?<![*\\w\\\\])([*_])(?![*_\\s])([^\\n*_]*?[^\\s*_\\\\])\\1(?![*\\w])")
    private static let strike     = re("~~(?=\\S)[^\\n]*?\\S~~")
    private static let link       = re("!?\\[([^\\]\\n]*)\\]\\(([^)\\n]*)\\)")
    private static let autolink   = re("<(?:https?://|mailto:)[^>\\s]+>")
    private static let listMarker = re("^[ \\t]*((?:[-*+]|\\d{1,9}[.)])[ \\t]+(?:\\[[ xX]\\][ \\t]+)?)", .anchorsMatchLines)
    private static let hrule      = re("^ {0,3}([-*_])(?:[ \\t]*\\1){2,}[ \\t]*$", .anchorsMatchLines)
    private static let tableSep   = re("^[ \\t]*\\|?[ \\t]*:?-{2,}:?[ \\t]*(?:\\|[ \\t]*:?-{2,}:?[ \\t]*)*\\|?[ \\t]*$", .anchorsMatchLines)
    private static let math       = re("\\$\\$[^\\n]+?\\$\\$|(?<![\\\\$])\\$(?![\\s$])[^$\\n]*?(?<![\\s\\\\])\\$(?!\\d)")

    // MARK: - Apply

    func apply(to storage: NSTextStorage) {
        let full = NSRange(location: 0, length: storage.length)
        guard full.length > 0 else { return }

        storage.beginEditing()
        storage.setAttributes(baseAttributes, range: full)
        if full.length <= Self.maxLength {
            style(storage, full: full)
        }
        storage.endEditing()
    }

    private func style(_ storage: NSTextStorage, full: NSRange) {
        let text = storage.string
        let ns = text as NSString

        // ---- block level, line by line -------------------------------------------------
        var fences: [NSRange] = []
        var inFence = false
        var fenceChar: unichar = 0
        var fenceLen = 0
        var fenceStart = 0
        var inFront = false
        var lineIndex = 0

        let secondary = NSColor.secondaryLabelColor
        let heading = NSColor.systemBlue
        let codeColor = NSColor.systemTeal

        ns.enumerateSubstrings(in: full, options: [.byLines, .substringNotRequired]) { _, line, enclosing, _ in
            defer { lineIndex += 1 }
            let end = line.location + line.length
            let first = line.length > 0 ? ns.character(at: line.location) : 0

            // YAML front matter at the very top
            if lineIndex == 0, line.length >= 3, ns.substring(with: line).trimmingCharacters(in: .whitespaces) == "---" {
                inFront = true
                storage.addAttribute(.foregroundColor, value: secondary, range: enclosing)
                return
            }
            if inFront {
                storage.addAttribute(.foregroundColor, value: secondary, range: enclosing)
                let t = ns.substring(with: line).trimmingCharacters(in: .whitespaces)
                if t == "---" || t == "..." { inFront = false }
                return
            }

            // fenced code
            if !inFence {
                var i = line.location
                var spaces = 0
                while i < end, ns.character(at: i) == 0x20, spaces < 3 { i += 1; spaces += 1 }
                if i < end {
                    let c = ns.character(at: i)
                    if c == 0x60 || c == 0x7E {   // ` or ~
                        var n = 0
                        var j = i
                        while j < end, ns.character(at: j) == c { n += 1; j += 1 }
                        if n >= 3 {
                            inFence = true
                            fenceChar = c
                            fenceLen = n
                            fenceStart = line.location
                            storage.addAttribute(.foregroundColor, value: secondary, range: enclosing)
                            return
                        }
                    }
                }
            } else {
                var i = line.location
                var spaces = 0
                while i < end, ns.character(at: i) == 0x20, spaces < 3 { i += 1; spaces += 1 }
                var n = 0
                var j = i
                while j < end, ns.character(at: j) == fenceChar { n += 1; j += 1 }
                let rest = ns.substring(with: NSRange(location: j, length: end - j)).trimmingCharacters(in: .whitespaces)
                if n >= fenceLen, rest.isEmpty {
                    inFence = false
                    fences.append(NSRange(location: fenceStart, length: enclosing.location + enclosing.length - fenceStart))
                    storage.addAttribute(.foregroundColor, value: secondary, range: enclosing)
                } else {
                    storage.addAttribute(.foregroundColor, value: codeColor, range: enclosing)
                }
                return
            }

            // ATX heading
            if first == 0x23 {   // #
                var n = 0
                while line.location + n < end, ns.character(at: line.location + n) == 0x23 { n += 1 }
                if n <= 6, line.location + n == end || ns.character(at: line.location + n) == 0x20 {
                    storage.addAttributes([.foregroundColor: heading, .font: self.boldFont], range: line)
                    storage.addAttribute(.foregroundColor, value: secondary, range: NSRange(location: line.location, length: n))
                    return
                }
            }

            // block quote
            var q = line.location
            var qs = 0
            while q < end, ns.character(at: q) == 0x20, qs < 3 { q += 1; qs += 1 }
            if q < end, ns.character(at: q) == 0x3E {   // >
                storage.addAttribute(.foregroundColor, value: secondary, range: line)
            }
        }
        if inFence {   // unterminated fence runs to the end of the document
            fences.append(NSRange(location: fenceStart, length: full.length - fenceStart))
        }

        func inFenceOrFront(_ r: NSRange) -> Bool {
            for f in fences where NSIntersectionRange(f, r).length > 0 { return true }
            return false
        }

        func forEach(_ regex: NSRegularExpression, _ body: (NSTextCheckingResult) -> Void) {
            regex.enumerateMatches(in: text, options: [], range: full) { m, _, _ in
                guard let m, !inFenceOrFront(m.range) else { return }
                body(m)
            }
        }

        // ---- list markers, rules, table separators ------------------------------------
        forEach(Self.listMarker) { m in
            storage.addAttribute(.foregroundColor, value: NSColor.systemOrange, range: m.range(at: 1))
        }
        forEach(Self.hrule) { m in
            storage.addAttribute(.foregroundColor, value: secondary, range: m.range)
        }
        forEach(Self.tableSep) { m in
            storage.addAttribute(.foregroundColor, value: secondary, range: m.range)
        }

        // ---- inline --------------------------------------------------------------------
        forEach(Self.italic) { m in
            storage.addAttributes([.obliqueness: 0.2, .foregroundColor: NSColor.systemPurple], range: m.range)
        }
        forEach(Self.bold) { m in
            storage.addAttributes([.font: self.boldFont], range: m.range)
        }
        forEach(Self.strike) { m in
            storage.addAttributes([.strikethroughStyle: NSUnderlineStyle.single.rawValue,
                                   .foregroundColor: secondary], range: m.range)
        }
        forEach(Self.link) { m in
            storage.addAttribute(.foregroundColor, value: NSColor.systemBlue, range: m.range(at: 1))
            storage.addAttribute(.foregroundColor, value: secondary, range: m.range(at: 2))
        }
        forEach(Self.autolink) { m in
            storage.addAttribute(.foregroundColor, value: NSColor.systemBlue, range: m.range)
        }
        forEach(Self.math) { m in
            storage.addAttribute(.foregroundColor, value: NSColor.systemIndigo, range: m.range)
        }
        forEach(Self.inlineCode) { m in
            storage.addAttributes([.foregroundColor: NSColor.systemPink,
                                   .backgroundColor: NSColor.systemPink.withAlphaComponent(0.10),
                                   .obliqueness: 0], range: m.range)
        }
    }
}
