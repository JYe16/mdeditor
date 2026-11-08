// Headless regression tests for the editor's Markdown editing commands.
// Run with: ./scripts/test.sh
//
// Notation in the test strings:  |  = caret       «…»  = selected text
import AppKit

var failures = 0
var passed = 0

func check(_ name: String, _ got: String, _ want: String) {
    if got == want {
        passed += 1
        print("  ok    \(name)")
    } else {
        failures += 1
        print("  FAIL  \(name)\n        got:  \(got.debugDescription)\n        want: \(want.debugDescription)")
    }
}

func makeView(_ marked: String) -> MarkdownTextView {
    var text = marked
    var selection = NSRange(location: 0, length: 0)
    if let open = text.firstIndex(of: "«"), let close = text.firstIndex(of: "»"), open < close {
        let head = String(text[text.startIndex..<open])
        let inner = String(text[text.index(after: open)..<close])
        let tail = String(text[text.index(after: close)...])
        selection = NSRange(location: (head as NSString).length, length: (inner as NSString).length)
        text = head + inner + tail
    } else if let bar = text.firstIndex(of: "|") {
        selection = NSRange(location: (String(text[text.startIndex..<bar]) as NSString).length, length: 0)
        text.remove(at: bar)
    }
    let tv = MarkdownTextView(frame: NSRect(x: 0, y: 0, width: 400, height: 300))
    tv.isRichText = false
    tv.string = text
    tv.setSelectedRange(selection)
    return tv
}

func describe(_ tv: MarkdownTextView) -> String {
    let ns = tv.string as NSString
    let sel = tv.selectedRange()
    if sel.length == 0 {
        return ns.substring(to: sel.location) + "|" + ns.substring(from: sel.location)
    }
    return ns.substring(to: sel.location) + "«" + ns.substring(with: sel) + "»" + ns.substring(from: NSMaxRange(sel))
}

func run(_ name: String, _ input: String, _ want: String, _ action: (MarkdownTextView) -> Void) {
    let tv = makeView(input)
    action(tv)
    check(name, describe(tv), want)
}

print("Enter: list continuation")
run("bullet continues",             "- item|",           "- item\n- |")          { $0.insertNewline(nil) }
run("star bullet continues",        "* item|",           "* item\n* |")          { $0.insertNewline(nil) }
run("numbered increments",          "1. one|",           "1. one\n2. |")         { $0.insertNewline(nil) }
run("numbered paren increments",    "9) nine|",          "9) nine\n10) |")       { $0.insertNewline(nil) }
run("nested indent kept",           "  - sub|",          "  - sub\n  - |")       { $0.insertNewline(nil) }
run("task continues unchecked",     "- [x] done|",       "- [x] done\n- [ ] |")  { $0.insertNewline(nil) }
run("empty item ends list",         "- a\n- |",          "- a\n|")               { $0.insertNewline(nil) }
run("empty numbered ends list",     "1. a\n2. |",        "1. a\n|")              { $0.insertNewline(nil) }
run("quote continues",              "> quote|",          "> quote\n> |")         { $0.insertNewline(nil) }
run("empty quote ends",             "> a\n> |",          "> a\n|")               { $0.insertNewline(nil) }
run("plain line untouched",         "plain|",            "plain\n|")             { $0.insertNewline(nil) }
run("mid-line is a plain break",    "- ab|cd",           "- ab\n|cd")            { $0.insertNewline(nil) }
run("list continues on later line", "intro\n- a|",       "intro\n- a\n- |")      { $0.insertNewline(nil) }

print("Tab / Shift-Tab")
run("tab on plain line",            "a|b",               "a    |b")              { $0.insertTab(nil) }
run("tab on list line indents",     "- item|",           "    - item|")           { $0.insertTab(nil) }
run("shift-tab outdents",           "    - item|",       "- item|")              { $0.insertBacktab(nil) }
run("shift-tab partial indent",     "  - item|",         "- item|")              { $0.insertBacktab(nil) }
run("shift-tab at column 0",        "- item|",           "- item|")              { $0.insertBacktab(nil) }
run("tab indents all selected",     "«a\nb»",            "«    a\n    b»")       { $0.insertTab(nil) }
run("shift-tab all selected",       "«    a\n    b»",    "«a\nb»")               { $0.insertBacktab(nil) }

print("Inline formatting")
run("bold wraps selection",         "a «word» b",        "a **«word»** b")       { $0.mdBold(nil) }
run("bold toggles off",             "a **«word»** b",    "a «word» b")           { $0.mdBold(nil) }
run("bold with empty selection",    "a |b",              "a **|**b")             { $0.mdBold(nil) }
run("italic wraps",                 "«x»",               "*«x»*")                { $0.mdItalic(nil) }
run("strikethrough wraps",          "«x»",               "~~«x»~~")              { $0.mdStrike(nil) }
run("inline code wraps",            "«x»",               "`«x»`")                { $0.mdInlineCode(nil) }
run("CJK selection bold",           "«中文»",             "**«中文»**")            { $0.mdBold(nil) }

print("Block formatting")
run("heading 1 adds",               "title|",            "# title|")             { $0.mdHeading1(nil) }
run("heading 2 replaces level",     "# title|",          "## title|")            { $0.mdHeading2(nil) }
run("heading 1 toggles off",        "# title|",          "title|")               { $0.mdHeading1(nil) }
run("heading on 2nd line only",     "a\nb|",             "a\n## b|")             { $0.mdHeading2(nil) }
run("quote adds to all lines",      "«a\nb»",            "«> a\n> b»")           { $0.mdQuote(nil) }
run("quote toggles off",            "«> a\n> b»",        "«a\nb»")               { $0.mdQuote(nil) }
run("bullet list",                  "«a\nb»",            "«- a\n- b»")           { $0.mdBulletList(nil) }
run("task list",                    "«a»",               "«- [ ] a»")            { $0.mdTaskList(nil) }
run("link keeps text, selects url", "«text»",            "[text](«url»)")        { $0.mdLink(nil) }
run("link with no selection",       "|",                 "[](«url»)")            { $0.mdLink(nil) }
run("code block, no selection",     "|",                 "```\n|\n```\n")        { $0.mdCodeBlock(nil) }
run("code block wraps selection",   "«a = 1»",           "```\na = 1\n```\n|")   { $0.mdCodeBlock(nil) }

print("\n\(passed) passed, \(failures) failed")
exit(failures == 0 ? 0 : 1)
