import SwiftUI

@main
struct MDEditorApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        DocumentGroup(newDocument: MarkdownDocument()) { file in
            ContentView(document: file.$document, fileURL: file.fileURL)
        }
        .defaultSize(width: 1200, height: 780)
        .commands { AppCommands() }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    /// True while the launch-time cleanup below closes empty "Untitled" leftovers. That must never
    /// count as "the last window was closed" (the real document's window may not be on screen yet).
    private var isClosingLeftovers = false

    /// Closing the last window (red button or ⌘W) quits the app instead of leaving it in the Dock.
    /// Unsaved changes are still offered for saving first, and cancelling that keeps the app open.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        !isClosingLeftovers
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // SwiftUI opens an empty "Untitled" window at launch even when the app was started by
        // opening a file. Once the file is open, close the empty leftovers.
        for delay in [0.6, 1.5, 3.0] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                self?.closeEmptyUntitledDocuments()
            }
        }
    }

    private func closeEmptyUntitledDocuments() {
        let documents = NSDocumentController.shared.documents
        guard documents.contains(where: { $0.fileURL != nil }) else { return }
        isClosingLeftovers = true
        defer { isClosingLeftovers = false }
        for document in documents where document.fileURL == nil && !document.isDocumentEdited {
            document.close()
        }
    }
}

struct AppCommands: Commands {
    @AppStorage("viewMode") private var mode: ViewMode = .split
    @AppStorage("zoom") private var zoom: Double = 1.0

    private func send(_ action: Selector) {
        NSApp.sendAction(action, to: nil, from: nil)
    }

    private func setZoom(_ value: Double) {
        zoom = (min(max(value, 0.6), 3.0) * 10).rounded() / 10
    }

    /// Drives the text view's built-in find bar (it reads the action from the sender's tag).
    private func find(_ action: NSTextFinder.Action) {
        let item = NSMenuItem()
        item.tag = action.rawValue
        NSApp.sendAction(#selector(NSResponder.performTextFinderAction(_:)), to: nil, from: item)
    }

    var body: some Commands {
        CommandGroup(replacing: .printItem) {
            Button("Print…") { send(#selector(EditorSplitView.printDocument(_:))) }
                .keyboardShortcut("p", modifiers: .command)
        }

        CommandGroup(after: .textEditing) {
            Menu("Find") {
                Button("Find…") { find(.showFindInterface) }
                    .keyboardShortcut("f", modifiers: .command)
                Button("Find and Replace…") { find(.showReplaceInterface) }
                    .keyboardShortcut("f", modifiers: [.command, .option])
                Button("Find Next") { find(.nextMatch) }
                    .keyboardShortcut("g", modifiers: .command)
                Button("Find Previous") { find(.previousMatch) }
                    .keyboardShortcut("g", modifiers: [.command, .shift])
                Button("Use Selection for Find") { find(.setSearchString) }
                    .keyboardShortcut("e", modifiers: .command)
            }
        }

        CommandGroup(before: .toolbar) {
            Button("Editor Only") { mode = .editor }
                .keyboardShortcut("1", modifiers: .command)
            Button("Split View") { mode = .split }
                .keyboardShortcut("2", modifiers: .command)
            Button("Preview Only") { mode = .preview }
                .keyboardShortcut("3", modifiers: .command)
            Divider()
            Button("Zoom In") { setZoom(zoom + 0.1) }
                .keyboardShortcut("+", modifiers: .command)
            Button("Zoom Out") { setZoom(zoom - 0.1) }
                .keyboardShortcut("-", modifiers: .command)
            Button("Actual Size") { setZoom(1.0) }
                .keyboardShortcut("0", modifiers: .command)
            Divider()
        }

        CommandMenu("Format") {
            Button("Bold") { send(#selector(MarkdownTextView.mdBold(_:))) }
                .keyboardShortcut("b", modifiers: .command)
            Button("Italic") { send(#selector(MarkdownTextView.mdItalic(_:))) }
                .keyboardShortcut("i", modifiers: .command)
            Button("Strikethrough") { send(#selector(MarkdownTextView.mdStrike(_:))) }
                .keyboardShortcut("x", modifiers: [.command, .shift])
            Button("Inline Code") { send(#selector(MarkdownTextView.mdInlineCode(_:))) }
                .keyboardShortcut("c", modifiers: [.command, .option])
            Button("Link") { send(#selector(MarkdownTextView.mdLink(_:))) }
                .keyboardShortcut("k", modifiers: .command)
            Divider()
            Button("Heading 1") { send(#selector(MarkdownTextView.mdHeading1(_:))) }
                .keyboardShortcut("1", modifiers: [.command, .option])
            Button("Heading 2") { send(#selector(MarkdownTextView.mdHeading2(_:))) }
                .keyboardShortcut("2", modifiers: [.command, .option])
            Button("Heading 3") { send(#selector(MarkdownTextView.mdHeading3(_:))) }
                .keyboardShortcut("3", modifiers: [.command, .option])
            Divider()
            Button("Quote") { send(#selector(MarkdownTextView.mdQuote(_:))) }
                .keyboardShortcut("'", modifiers: .command)
            Button("Bulleted List") { send(#selector(MarkdownTextView.mdBulletList(_:))) }
                .keyboardShortcut("8", modifiers: [.command, .shift])
            Button("Task List") { send(#selector(MarkdownTextView.mdTaskList(_:))) }
                .keyboardShortcut("t", modifiers: [.command, .option])
            Button("Code Block") { send(#selector(MarkdownTextView.mdCodeBlock(_:))) }
                .keyboardShortcut("k", modifiers: [.command, .option])
        }
    }
}
