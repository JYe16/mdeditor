import AppKit

/// Plain-text editor with Markdown conveniences: list continuation, Tab indentation
/// and formatting commands (⌘B, ⌘I, ⌘K …). Every edit goes through `insertText`, so
/// undo/redo and the document's dirty state behave normally.
final class MarkdownTextView: NSTextView {

    // MARK: - Container width safety

    /// TextKit 1 spins at 100% CPU with runaway memory when the text container is narrower than
    /// its own line-fragment padding (which happens while a window is still being laid out, or
    /// when a split pane is collapsed). So the container width is managed here and never allowed
    /// to collapse, instead of relying on `widthTracksTextView`.
    static let minContainerWidth: CGFloat = 160

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        updateContainerWidth(for: newSize.width)
    }

    func updateContainerWidth(for viewWidth: CGFloat) {
        guard let container = textContainer else { return }
        let width = max(viewWidth - textContainerInset.width * 2, Self.minContainerWidth)
        if abs(container.containerSize.width - width) > 0.5 {
            container.containerSize = NSSize(width: width, height: CGFloat.greatestFiniteMagnitude)
        }
    }

    // MARK: - Enter: continue lists and quotes

    private static let listRegex = try! NSRegularExpression(
        pattern: "^([ \\t]*)((?:[-*+]|(\\d{1,9})([.)]))[ \\t]+)(\\[[ xX]\\][ \\t]+)?")
    private static let quoteRegex = try! NSRegularExpression(pattern: "^([ \\t]{0,3}(?:>[ \\t]?)+)")

    override func insertNewline(_ sender: Any?) {
        guard !hasMarkedText(), selectedRange().length == 0 else {
            super.insertNewline(sender)
            return
        }
        let ns = string as NSString
        let caret = selectedRange().location
        let paragraph = ns.paragraphRange(for: NSRange(location: caret, length: 0))
        var lineEnd = paragraph.location + paragraph.length
        while lineEnd > paragraph.location, [0x0A, 0x0D].contains(ns.character(at: lineEnd - 1)) { lineEnd -= 1 }
        let line = ns.substring(with: NSRange(location: paragraph.location, length: lineEnd - paragraph.location))

        // Only continue when the caret is at the end of the line.
        guard caret == lineEnd else {
            super.insertNewline(sender)
            return
        }
        let full = NSRange(location: 0, length: (line as NSString).length)

        if let m = Self.listRegex.firstMatch(in: line, range: full) {
            let ls = line as NSString
            let indent = ls.substring(with: m.range(at: 1))
            let marker = ls.substring(with: m.range(at: 2))
            let hasTask = m.range(at: 5).location != NSNotFound
            let rest = ls.substring(from: m.range.location + m.range.length)

            if rest.trimmingCharacters(in: .whitespaces).isEmpty {
                // Empty item: pressing Enter ends the list.
                insertText("", replacementRange: NSRange(location: paragraph.location, length: lineEnd - paragraph.location))
                return
            }
            var next = marker
            if m.range(at: 3).location != NSNotFound, let n = Int(ls.substring(with: m.range(at: 3))) {
                let delim = ls.substring(with: m.range(at: 4))
                let spacing = String(marker.drop { $0 != " " && $0 != "\t" })
                next = "\(n + 1)\(delim)\(spacing)"
            }
            insertText("\n" + indent + next + (hasTask ? "[ ] " : ""), replacementRange: selectedRange())
            return
        }

        if let m = Self.quoteRegex.firstMatch(in: line, range: full) {
            let ls = line as NSString
            let prefix = ls.substring(with: m.range(at: 1))
            let rest = ls.substring(from: m.range.location + m.range.length)
            if rest.trimmingCharacters(in: .whitespaces).isEmpty {
                insertText("", replacementRange: NSRange(location: paragraph.location, length: lineEnd - paragraph.location))
            } else {
                insertText("\n" + prefix, replacementRange: selectedRange())
            }
            return
        }

        super.insertNewline(sender)
    }

    // MARK: - Tab / Shift-Tab

    private static let indentUnit = "    "

    override func insertTab(_ sender: Any?) {
        guard !hasMarkedText() else { super.insertTab(sender); return }
        let sel = selectedRange()
        let ns = string as NSString
        let currentLine = ns.substring(with: ns.lineRange(for: NSRange(location: sel.location, length: 0)))
        let onListLine = Self.listRegex.firstMatch(
            in: currentLine, range: NSRange(location: 0, length: (currentLine as NSString).length)) != nil

        // With a selection (or inside a list item) Tab indents whole lines; otherwise it
        // inserts spaces at the caret.
        if sel.length > 0 || onListLine {
            shiftLines(by: .indent)
        } else {
            insertText(Self.indentUnit, replacementRange: sel)
        }
    }

    override func insertBacktab(_ sender: Any?) {
        guard !hasMarkedText() else { super.insertBacktab(sender); return }
        shiftLines(by: .outdent)
    }

    private enum Shift { case indent, outdent }

    private func shiftLines(by shift: Shift) {
        let ns = string as NSString
        let sel = selectedRange()
        let block = ns.lineRange(for: sel)
        let original = ns.substring(with: block)
        var out = ""
        var firstDelta = 0
        var isFirst = true
        original.enumerateSubstrings(in: original.startIndex..., options: [.byLines, .substringNotRequired]) { _, _, enclosing, _ in
            let line = String(original[enclosing])
            var delta = 0
            var newLine = line
            switch shift {
            case .indent:
                if !line.trimmingCharacters(in: .newlines).isEmpty {
                    newLine = Self.indentUnit + line
                    delta = Self.indentUnit.count
                }
            case .outdent:
                if line.hasPrefix("\t") {
                    newLine = String(line.dropFirst()); delta = -1
                } else {
                    var n = 0
                    for ch in line.prefix(Self.indentUnit.count) { if ch == " " { n += 1 } else { break } }
                    newLine = String(line.dropFirst(n)); delta = -n
                }
            }
            if isFirst { firstDelta = delta; isFirst = false }
            out += newLine
        }
        guard out != original else { return }
        insertText(out, replacementRange: block)
        if sel.length == 0 {
            setSelectedRange(NSRange(location: max(block.location, sel.location + firstDelta), length: 0))
        } else {
            // Keep every shifted line selected so Tab / Shift-Tab can be repeated.
            let len = (out as NSString).length - (out.hasSuffix("\n") ? 1 : 0)
            setSelectedRange(NSRange(location: block.location, length: len))
        }
    }

    // MARK: - Formatting commands (Format menu)

    @objc func mdBold(_ sender: Any?) { toggleInline("**") }
    @objc func mdItalic(_ sender: Any?) { toggleInline("*") }
    @objc func mdStrike(_ sender: Any?) { toggleInline("~~") }
    @objc func mdInlineCode(_ sender: Any?) { toggleInline("`") }
    @objc func mdHeading1(_ sender: Any?) { setHeading(1) }
    @objc func mdHeading2(_ sender: Any?) { setHeading(2) }
    @objc func mdHeading3(_ sender: Any?) { setHeading(3) }
    @objc func mdQuote(_ sender: Any?) { toggleLinePrefix("> ") }
    @objc func mdBulletList(_ sender: Any?) { toggleLinePrefix("- ") }
    @objc func mdTaskList(_ sender: Any?) { toggleLinePrefix("- [ ] ") }

    @objc func mdLink(_ sender: Any?) {
        let sel = selectedRange()
        let ns = string as NSString
        let text = sel.length > 0 ? ns.substring(with: sel) : ""
        let urlPlaceholder = "url"
        insertText("[\(text)](\(urlPlaceholder))", replacementRange: sel)
        let urlStart = sel.location + 1 + (text as NSString).length + 2
        setSelectedRange(NSRange(location: urlStart, length: (urlPlaceholder as NSString).length))
    }

    @objc func mdCodeBlock(_ sender: Any?) {
        let sel = selectedRange()
        let ns = string as NSString
        let text = sel.length > 0 ? ns.substring(with: sel) : ""
        let needsLeadingBreak = sel.location > 0 && ns.character(at: sel.location - 1) != 0x0A
        let lead = needsLeadingBreak ? "\n" : ""
        let inserted = "\(lead)```\n\(text)\n```\n"
        insertText(inserted, replacementRange: sel)
        // No selection: caret goes on the empty line inside the fence. Otherwise after the block.
        let caret = sel.length == 0
            ? sel.location + (lead as NSString).length + 4
            : sel.location + (inserted as NSString).length
        setSelectedRange(NSRange(location: caret, length: 0))
    }

    private func toggleInline(_ marker: String) {
        let ns = string as NSString
        let sel = selectedRange()
        let m = (marker as NSString).length

        let wrapped = sel.location >= m
            && sel.location + sel.length + m <= ns.length
            && ns.substring(with: NSRange(location: sel.location - m, length: m)) == marker
            && ns.substring(with: NSRange(location: sel.location + sel.length, length: m)) == marker

        if wrapped {
            let outer = NSRange(location: sel.location - m, length: sel.length + 2 * m)
            insertText(ns.substring(with: sel), replacementRange: outer)
            setSelectedRange(NSRange(location: outer.location, length: sel.length))
        } else if sel.length == 0 {
            insertText(marker + marker, replacementRange: sel)
            setSelectedRange(NSRange(location: sel.location + m, length: 0))
        } else {
            insertText(marker + ns.substring(with: sel) + marker, replacementRange: sel)
            setSelectedRange(NSRange(location: sel.location + m, length: sel.length))
        }
    }

    private func setHeading(_ level: Int) {
        let ns = string as NSString
        let sel = selectedRange()
        let lineRange = ns.lineRange(for: NSRange(location: sel.location, length: 0))
        var content = ns.substring(with: lineRange)
        let newline = content.hasSuffix("\n") ? "\n" : ""
        if !newline.isEmpty { content.removeLast() }

        var existing = 0
        for ch in content { if ch == "#" { existing += 1 } else { break } }
        var body = content
        if existing > 0, existing <= 6 {
            body = String(content.dropFirst(existing))
            if body.hasPrefix(" ") { body.removeFirst() }
        }
        let replacement = existing == level ? body : String(repeating: "#", count: level) + " " + body
        insertText(replacement + newline, replacementRange: lineRange)
        setSelectedRange(NSRange(location: lineRange.location + (replacement as NSString).length, length: 0))
    }

    private func toggleLinePrefix(_ prefix: String) {
        let ns = string as NSString
        let sel = selectedRange()
        let block = ns.lineRange(for: sel)
        let original = ns.substring(with: block)

        var lines: [String] = []
        original.enumerateSubstrings(in: original.startIndex..., options: [.byLines, .substringNotRequired]) { _, _, enclosing, _ in
            lines.append(String(original[enclosing]))
        }
        let nonEmpty = lines.filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        let allHave = !nonEmpty.isEmpty && nonEmpty.allSatisfy { $0.hasPrefix(prefix) }
        let out = lines.map { line -> String in
            if line.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return line }
            return allHave ? String(line.dropFirst(prefix.count)) : prefix + line
        }.joined()
        insertText(out, replacementRange: block)
        setSelectedRange(NSRange(location: block.location, length: (out as NSString).length - (out.hasSuffix("\n") ? 1 : 0)))
    }
}
