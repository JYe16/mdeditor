using MDEditor.Core;
using Xunit;
using C = MDEditor.Core.MarkdownCommands;

namespace MDEditor.Core.Tests;

/// <summary>1:1 port of Tests/EditingTests/main.swift (HANDOFF Appendix C). | = caret, «…» = selection.</summary>
public class EditingTests
{
    private static EditState Parse(string marked)
    {
        int open = marked.IndexOf('«');
        int close = marked.IndexOf('»');
        if (open >= 0 && close > open)
        {
            string inner = marked[(open + 1)..close];
            return new EditState(marked[..open] + inner + marked[(close + 1)..], open, inner.Length);
        }
        int bar = marked.IndexOf('|');
        return bar >= 0 ? new EditState(marked.Remove(bar, 1), bar, 0) : new EditState(marked, 0, 0);
    }

    private static string Show(EditState s) => s.SelLength == 0
        ? s.Text.Insert(s.SelStart, "|")
        : s.Text[..s.SelStart] + "«" + s.Text.Substring(s.SelStart, s.SelLength) + "»" + s.Text[s.SelEnd..];

    /// <summary>Applies a plain newline when a command reports "not handled", as a text box would.</summary>
    private static EditState EnterOrDefault(EditState s) => C.Enter(s)
        ?? new EditState(string.Concat(s.Text.AsSpan(0, s.SelStart), "\n", s.Text.AsSpan(s.SelEnd)), s.SelStart + 1, 0);

    [Theory]
    [InlineData("- item|", "- item\n- |")]
    [InlineData("* item|", "* item\n* |")]
    [InlineData("1. one|", "1. one\n2. |")]
    [InlineData("9) nine|", "9) nine\n10) |")]
    [InlineData("  - sub|", "  - sub\n  - |")]
    [InlineData("- [x] done|", "- [x] done\n- [ ] |")]
    [InlineData("- a\n- |", "- a\n|")]
    [InlineData("1. a\n2. |", "1. a\n|")]
    [InlineData("> quote|", "> quote\n> |")]
    [InlineData("> a\n> |", "> a\n|")]
    [InlineData("plain|", "plain\n|")]
    [InlineData("- ab|cd", "- ab\n|cd")]
    [InlineData("intro\n- a|", "intro\n- a\n- |")]
    // additional edge cases
    [InlineData("99. x|", "99. x\n100. |")]
    [InlineData("+ x|", "+ x\n+ |")]
    [InlineData("   > x|", "   > x\n   > |")]
    [InlineData("|", "\n|")]
    [InlineData("1.  wide|", "1.  wide\n2.  |")]
    [InlineData("- 😀|", "- 😀\n- |")]
    public void Enter(string input, string want) => Assert.Equal(want, Show(EnterOrDefault(Parse(input))));

    [Fact]
    public void EnterWithSelectionIsNotHandled() => Assert.Null(C.Enter(Parse("- «a»")));

    [Theory]
    [InlineData("a|b", "a    |b")]
    [InlineData("- item|", "    - item|")]
    [InlineData("«a\nb»", "«    a\n    b»")]
    [InlineData("|", "    |")]
    [InlineData("«a\n\nb»", "«    a\n\n    b»")]
    [InlineData("«a\n»b", "«    a»\nb")]
    public void Tab(string input, string want) => Assert.Equal(want, Show(C.Tab(Parse(input))));

    [Theory]
    [InlineData("    - item|", "- item|")]
    [InlineData("  - item|", "- item|")]
    [InlineData("- item|", "- item|")]
    [InlineData("«    a\n    b»", "«a\nb»")]
    [InlineData("\t- x|", "- x|")]
    [InlineData("|", "|")]
    public void BackTab(string input, string want) => Assert.Equal(want, Show(C.BackTab(Parse(input))));

    [Theory]
    [InlineData("a «word» b", "a **«word»** b")]
    [InlineData("a **«word»** b", "a «word» b")]
    [InlineData("a |b", "a **|**b")]
    [InlineData("«中文»", "**«中文»**")]
    [InlineData("«😀»", "**«😀»**")]
    [InlineData("**«x»", "****«x»**")] // markers on one side only: not "already wrapped"
    public void Bold(string input, string want) => Assert.Equal(want, Show(C.Bold(Parse(input))));

    [Fact] public void Italic() => Assert.Equal("*«x»*", Show(C.Italic(Parse("«x»"))));
    [Fact] public void Strike() => Assert.Equal("~~«x»~~", Show(C.Strike(Parse("«x»"))));
    [Fact] public void InlineCode() => Assert.Equal("`«x»`", Show(C.InlineCode(Parse("«x»"))));

    [Theory]
    [InlineData("title|", 1, "# title|")]
    [InlineData("# title|", 2, "## title|")]
    [InlineData("# title|", 1, "title|")]
    [InlineData("a\nb|", 2, "a\n## b|")]
    [InlineData("####### x|", 1, "# ####### x|")]
    [InlineData("a|\nb", 3, "### a|\nb")]
    public void Heading(string input, int level, string want) => Assert.Equal(want, Show(C.Heading(Parse(input), level)));

    [Theory]
    [InlineData("«a\nb»", "«> a\n> b»")]
    [InlineData("«> a\n> b»", "«a\nb»")]
    [InlineData("«a\n\nb»", "«> a\n\n> b»")]
    public void Quote(string input, string want) => Assert.Equal(want, Show(C.Quote(Parse(input))));

    [Fact] public void Bullet() => Assert.Equal("«- a\n- b»", Show(C.Bullet(Parse("«a\nb»"))));
    [Fact] public void Task() => Assert.Equal("«- [ ] a»", Show(C.Task(Parse("«a»"))));

    [Theory]
    [InlineData("«text»", "[text](«url»)")]
    [InlineData("|", "[](«url»)")]
    public void Link(string input, string want) => Assert.Equal(want, Show(C.Link(Parse(input))));

    [Theory]
    [InlineData("|", "```\n|\n```\n")]
    [InlineData("«a = 1»", "```\na = 1\n```\n|")]
    [InlineData("x|", "x\n```\n|\n```\n")]
    [InlineData("```\n|\n```", "```\n```\n|\n```\n\n```")]
    public void CodeBlock(string input, string want) => Assert.Equal(want, Show(C.CodeBlock(Parse(input))));

    [Fact]
    public void OneMegabyteDocument()
    {
        string big = string.Concat(Enumerable.Repeat("- some list item with text\n", 40_000));
        var state = new EditState(big + "- last", big.Length + 6, 0);
        Assert.EndsWith("- last\n- ", C.Enter(state)!.Text);
    }
}
