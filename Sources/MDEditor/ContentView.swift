import SwiftUI

struct ContentView: View {
    @Binding var document: MarkdownDocument
    var fileURL: URL?

    @AppStorage("viewMode") private var mode: ViewMode = .split
    @AppStorage("zoom") private var zoom: Double = 1.0

    var body: some View {
        VStack(spacing: 0) {
            SplitEditorView(text: $document.text, fileURL: fileURL, mode: mode, zoom: zoom)
            Divider()
            StatusBar(text: document.text)
        }
        .frame(minWidth: 640, idealWidth: 1200, minHeight: 360, idealHeight: 780)
        .toolbar {
            ToolbarItem(placement: .principal) {
                Picker("View", selection: $mode) {
                    Label("Editor Only", systemImage: "doc.plaintext").tag(ViewMode.editor)
                    Label("Split View", systemImage: "rectangle.split.2x1").tag(ViewMode.split)
                    Label("Preview Only", systemImage: "doc.richtext").tag(ViewMode.preview)
                }
                .pickerStyle(.segmented)
                .labelStyle(.iconOnly)
                .help("Switch between editor, split view and preview")
            }
        }
    }
}

struct StatusBar: View {
    let text: String

    var body: some View {
        let stats = TextStats(text)
        HStack(spacing: 16) {
            Text("\(stats.words) words")
            Text("\(stats.characters) characters")
            if stats.words > 0 { Text("\(stats.minutes) min read") }
            Spacer()
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .monospacedDigit()
        .padding(.horizontal, 12)
        .frame(height: 22)
        .background(Color(nsColor: .windowBackgroundColor))
    }
}

/// Word count that treats every CJK character as one word, like most Chinese editors.
struct TextStats {
    let words: Int
    let characters: Int
    let minutes: Int

    init(_ text: String) {
        var cjk = 0
        var latin = 0
        var chars = 0
        var inWord = false
        for u in text.unicodeScalars {
            if !u.properties.isWhitespace { chars += 1 }
            if Self.isCJK(u.value) {
                cjk += 1
                inWord = false
            } else if u.properties.isAlphabetic || u.properties.numericType != nil {
                if !inWord { latin += 1; inWord = true }
            } else {
                inWord = false
            }
        }
        words = cjk + latin
        characters = chars
        let minutes = Double(cjk) / 400 + Double(latin) / 230
        self.minutes = words == 0 ? 0 : max(1, Int(minutes.rounded(.up)))
    }

    private static func isCJK(_ v: UInt32) -> Bool {
        switch v {
        case 0x3040...0x30FF, 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xAC00...0xD7AF, 0xF900...0xFAFF, 0x20000...0x2FA1F:
            return true
        default:
            return false
        }
    }
}
