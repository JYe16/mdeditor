using System.Text;
using MDEditor.Core;
using Xunit;

namespace MDEditor.Core.Tests;

public class TextStatsTests
{
    [Theory]
    [InlineData("Hello world", 2, 10, 1)]
    [InlineData("中文测试", 4, 4, 1)]
    [InlineData("Hello 世界", 3, 7, 1)]
    [InlineData("", 0, 0, 0)]
    [InlineData("  \n\t", 0, 0, 0)]
    [InlineData("😀", 0, 1, 0)]
    public void Counts(string text, int words, int chars, int minutes) =>
        Assert.Equal(new TextStats(words, chars, minutes), TextStats.Of(text));
}

public class LineIndexTests
{
    [Fact]
    public void MapsOffsets()
    {
        var index = new LineIndex("ab\ncd\n");
        Assert.Equal(3, index.LineCount);
        Assert.Equal(0, index.LineOf(0));
        Assert.Equal(0, index.LineOf(2));
        Assert.Equal(1, index.LineOf(3));
        Assert.Equal(2, index.LineOf(6));
        Assert.Equal(3, index.StartOf(1));
    }

    [Fact]
    public void EmptyDocumentHasOneLine()
    {
        var index = new LineIndex("");
        Assert.Equal(1, index.LineCount);
        Assert.Equal(0, index.LineOf(0));
        Assert.Equal(0, index.StartOf(99));
    }
}

public class TextDiffTests
{
    [Theory]
    [InlineData("abc", "abXc", -1, 2, 0, 1)]
    [InlineData("abc", "ac", -1, 1, 1, 0)]
    [InlineData("abc", "abc", -1, 0, 0, 0)]
    [InlineData("aaa", "aaaa", 2, 1, 0, 1)]   // typed at index 1 of a run: the caret disambiguates
    [InlineData("aaa", "aa", 1, 1, 1, 0)]
    public void Minimal(string a, string b, int caret, int offset, int removed, int inserted) =>
        Assert.Equal((offset, removed, inserted), TextDiff.Minimal(a, b, caret));

    [Fact]
    public void NeverSplitsSurrogatePairs()
    {
        // 😀 (D83D DE00) replaced by 😁 (D83D DE01): the shared high surrogate must be part of the edit.
        var (offset, removed, inserted) = TextDiff.Minimal("x😀y", "x😁y");
        Assert.Equal((1, 2, 2), (offset, removed, inserted));
    }
}

public class UndoStackTests
{
    private static TextEdit Ins(int at, string s) => new(at, "", s, at, 0, at + s.Length, 0);
    private static TextEdit Del(int at, string s) => new(at, s, "", at + s.Length, 0, at, 0);

    [Fact]
    public void CoalescesTypingWithinTheWindow()
    {
        var u = new UndoStack();
        u.Push(Ins(0, "a"), typing: true, 0);
        u.Push(Ins(1, "b"), typing: true, 100);
        Assert.Equal("ab", u.Undo()!.Inserted);
        Assert.False(u.CanUndo);
    }

    [Fact]
    public void BreaksOnPauseWordAndNewline()
    {
        var u = new UndoStack();
        u.Push(Ins(0, "a"), true, 0);
        u.Push(Ins(1, "b"), true, 5000);
        Assert.Equal("b", u.Undo()!.Inserted);

        u = new UndoStack();
        u.Push(Ins(0, " "), true, 0);
        u.Push(Ins(1, "b"), true, 10);
        Assert.Equal("b", u.Undo()!.Inserted);

        u = new UndoStack();
        u.Push(Ins(0, "a"), true, 0);
        u.Push(Ins(1, "\n"), true, 10);
        Assert.Equal("\n", u.Undo()!.Inserted);
    }

    [Fact]
    public void CommandsAreSingleSteps()
    {
        var u = new UndoStack();
        u.Push(Ins(0, "a"), true, 0);
        u.Push(Ins(1, "**"), typing: false, 10);
        u.Push(Ins(3, "b"), true, 20);
        Assert.Equal("b", u.Undo()!.Inserted);
        Assert.Equal("**", u.Undo()!.Inserted);
        Assert.Equal("a", u.Undo()!.Inserted);
    }

    [Fact]
    public void CoalescesBackspaceAndClearsRedo()
    {
        var u = new UndoStack();
        u.Push(Del(2, "c"), true, 0);
        u.Push(Del(1, "b"), true, 10);
        var e = u.Undo()!;
        Assert.Equal((1, "bc"), (e.Offset, e.Removed));
        Assert.True(u.CanRedo);
        u.Push(Ins(0, "x"), true, 20);
        Assert.False(u.CanRedo);
    }
}

public class TextSearchTests
{
    [Fact] public void CaseInsensitiveByDefault() => Assert.Equal([0, 4], TextSearch.FindAll("Foo foo", "foo", false, false));
    [Fact] public void MatchCase() => Assert.Equal([4], TextSearch.FindAll("Foo foo", "foo", true, false));
    [Fact] public void WholeWord() => Assert.Equal([0], TextSearch.FindAll("foo food", "foo", false, true));
    [Fact] public void CjkNeighboursAreWordBoundaries() => Assert.Equal([1], TextSearch.FindAll("中foo文", "foo", false, true));
    [Fact] public void EmptyQuery() => Assert.Empty(TextSearch.FindAll("x", "", false, false));
    [Fact] public void NonOverlapping() => Assert.Equal([0, 2], TextSearch.FindAll("aaaa", "aa", false, false));

    [Fact]
    public void ReplaceAll() => Assert.Equal(("b b", 2), TextSearch.ReplaceAll("a a", "a", "b", false, false));
}

public class TokenizerTests
{
    private static ColorRole Role(byte b) => (ColorRole)(b & StyleBits.RoleMask);

    private static byte At(string text, string needle, int skip = 0)
    {
        var styles = MarkdownTokenizer.Tokenize(text);
        return styles[text.IndexOf(needle, StringComparison.Ordinal) + skip];
    }

    [Fact]
    public void Heading()
    {
        Assert.Equal(ColorRole.Secondary, Role(At("## Title", "#")));
        Assert.Equal(ColorRole.Blue, Role(At("## Title", "T")));
        Assert.NotEqual(0, At("## Title", "T") & StyleBits.Bold);
    }

    [Fact] public void SevenHashesIsNotAHeading() => Assert.Equal(ColorRole.Default, Role(At("####### x", "x")));
    [Fact] public void ListMarker() => Assert.Equal(ColorRole.Orange, Role(At("- [ ] task", "[")));
    [Fact] public void Quote() => Assert.Equal(ColorRole.Secondary, Role(At("> said", "s")));
    [Fact] public void InlineCodeHasBackground() => Assert.NotEqual(0, At("a `b` c", "b") & StyleBits.CodeBackground);
    [Fact] public void Link() => Assert.Equal(ColorRole.Secondary, Role(At("[x](y)", "y")));
    [Fact] public void InlineMath() => Assert.Equal(ColorRole.Indigo, Role(At("$x^2$", "x")));
    [Fact] public void PricesAreNotMath() => Assert.Equal(ColorRole.Default, Role(At("$5 and $10", "a")));

    [Fact]
    public void FencedBlockSuppressesInline()
    {
        const string t = "```\n**x** `y`\n```\nz";
        var b = At(t, "x");
        Assert.Equal(ColorRole.Teal, Role(b));
        Assert.Equal(0, b & StyleBits.Bold);
        Assert.Equal(ColorRole.Secondary, Role(At(t, "`")));
    }

    [Fact]
    public void UnterminatedFenceRunsToEnd() => Assert.Equal(ColorRole.Teal, Role(At("~~~\n**x**", "x")));

    [Fact]
    public void FrontMatterSuppressesInline()
    {
        var b = At("---\ntitle: **x**\n---\nbody", "x");
        Assert.Equal(ColorRole.Secondary, Role(b));
        Assert.Equal(0, b & StyleBits.Bold);
        Assert.Equal(ColorRole.Default, Role(At("---\ntitle: **x**\n---\nbody", "body")));
    }

    [Fact]
    public void OverLimitIsPlain()
    {
        var styles = MarkdownTokenizer.Tokenize("# " + new string('x', MarkdownTokenizer.MaxLength));
        Assert.All(styles.Take(10), b => Assert.Equal(0, b));
    }

    [Fact] public void EmptyText() => Assert.Empty(MarkdownTokenizer.Tokenize(""));

    [Fact]
    public void ThreeHundredKilobytesIsFastEnoughForABackgroundPass()
    {
        string doc = string.Concat(Enumerable.Repeat("# heading\n- **a** `b` [c](d) $e$\nplain text line\n\n", 5000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        MarkdownTokenizer.Tokenize(doc[..Math.Min(doc.Length, 300_000)]);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }
}

public class TextCodecTests
{
    private static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Utf8RoundTrip()
    {
        var d = TextCodec.Decode(U8("中文\n"));
        Assert.Equal(("中文\n", TextFileEncoding.Utf8, LineEnding.Lf), (d.Text, d.Encoding, d.LineEnding));
        Assert.Equal(U8("中文\n"), TextCodec.Encode(d.Text, d.Encoding, d.LineEnding));
    }

    [Fact]
    public void Utf8BomIsStrippedAndKept()
    {
        byte[] data = [0xEF, 0xBB, 0xBF, (byte)'a'];
        var d = TextCodec.Decode(data);
        Assert.Equal("a", d.Text);
        Assert.Equal(data, TextCodec.Encode(d.Text, d.Encoding, d.LineEnding));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Utf16RoundTrip(bool little)
    {
        byte[] data = little
            ? [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("a\r\nb")]
            : [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("a\r\nb")];
        var d = TextCodec.Decode(data);
        Assert.Equal(("a\nb", LineEnding.CrLf), (d.Text, d.LineEnding));
        Assert.Equal(data, TextCodec.Encode(d.Text, d.Encoding, d.LineEnding));
    }

    [Fact]
    public void LegacyBecomesUtf8OnSave()
    {
        var d = TextCodec.Decode([(byte)'c', 0xE9]); // "cé" in Windows-1252; invalid UTF-8
        Assert.Equal(("cé", TextFileEncoding.Legacy), (d.Text, d.Encoding));
        Assert.Equal(U8("cé"), TextCodec.Encode(d.Text, d.Encoding, d.LineEnding));
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\n", "a\nb\nc\n", LineEnding.CrLf)]
    [InlineData("a\nb\nc\r\n", "a\nb\nc\n", LineEnding.Lf)]
    [InlineData("a\rb", "a\nb", LineEnding.Lf)]
    [InlineData("none", "none", LineEnding.Lf)]
    public void LineEndings(string raw, string text, LineEnding ending) =>
        Assert.Equal((text, ending), TextCodec.NormalizeLineEndings(raw));

    [Fact] public void NulBytesLookBinary() => Assert.True(TextCodec.LooksBinary([1, 0, 2]));
    [Fact] public void Utf16IsNotBinary() => Assert.False(TextCodec.LooksBinary([0xFF, 0xFE, 0x61, 0]));

    [Fact]
    public void AtomicWriteReplacesAndLeavesNoTempFiles()
    {
        string dir = Directory.CreateTempSubdirectory("mdeditor-test").FullName;
        try
        {
            string path = Path.Combine(dir, "a.md");
            SafeFile.WriteAtomic(path, U8("one"));
            SafeFile.WriteAtomic(path, U8("two"));
            Assert.Equal("two", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class LinkResolverTests
{
    private static readonly string Folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "docs"));

    private static LinkAction R(string href, string? folder = "", bool exists = true) =>
        LinkResolver.Resolve(href, folder == "" ? Folder : folder, _ => exists);

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("HTTP://x")]
    [InlineData("mailto:a@b.c")]
    [InlineData("tel:123")]
    public void WebLinksOpen(string href) => Assert.Equal(LinkActionKind.OpenUri, R(href).Kind);

    [Theory]
    [InlineData("file:///c:/windows/system32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData(@"C:\x\y.md")]
    [InlineData("%43:/windows/notepad.exe")]
    [InlineData("//attacker/share/x.png")]
    [InlineData(@"\\attacker\share\x.png")]
    [InlineData("%5C%5Cattacker/share/x.png")]
    [InlineData("a.txt:hidden")]
    public void DangerousTargetsAreIgnored(string href) => Assert.Equal(LinkActionKind.Ignore, R(href).Kind);

    [Theory]
    [InlineData("other.md", LinkActionKind.OpenInEditor)]
    [InlineData("sub%20dir/other.md#part", LinkActionKind.OpenInEditor)]
    [InlineData("pic.PNG", LinkActionKind.OpenWithDefaultApp)]
    [InlineData("doc.pdf", LinkActionKind.OpenWithDefaultApp)]
    [InlineData("evil.exe", LinkActionKind.Reveal)]
    [InlineData("script.ps1", LinkActionKind.Reveal)]
    [InlineData("shortcut.lnk", LinkActionKind.Reveal)]
    [InlineData("noextension", LinkActionKind.Reveal)]
    public void RelativeFilesFollowTheAllowList(string href, LinkActionKind kind) => Assert.Equal(kind, R(href).Kind);

    [Fact] public void MissingFileBeeps() => Assert.Equal(LinkActionKind.Beep, R("nope.md", exists: false).Kind);
    [Fact] public void UnsavedDocumentBeeps() => Assert.Equal(LinkActionKind.Beep, R("a.md", folder: null).Kind);
    [Fact] public void EmptyPathIsIgnored() => Assert.Equal(LinkActionKind.Ignore, R("#frag-only").Kind);

    [Fact]
    public void BackslashesAndDotsAreCanonical() =>
        Assert.Equal(Path.GetFullPath(Path.Combine(Folder, "..", "x.md")), R(@"sub\..\..\x.md").Target);
}

public class SettingsTests
{
    [Fact]
    public void CorruptOrMissingYieldsDefaults()
    {
        string dir = Directory.CreateTempSubdirectory("mdeditor-test").FullName;
        try
        {
            string path = Path.Combine(dir, "settings.json");
            Assert.Equal("split", AppSettings.Load(path).ViewMode);
            File.WriteAllText(path, "{not json");
            Assert.Equal(1.0, AppSettings.Load(path).Zoom);
            File.WriteAllText(path, """{"viewMode":"bogus","zoom":99}""");
            var s = AppSettings.Load(path);
            Assert.Equal(("split", 3.0), (s.ViewMode, s.Zoom));

            s.ViewMode = "preview";
            s.Save(path);
            Assert.Equal("preview", AppSettings.Load(path).ViewMode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0.1, 0.6)]
    [InlineData(1.0000001, 1.0)]
    [InlineData(1.26, 1.3)]
    [InlineData(5, 3.0)]
    public void ZoomClamps(double input, double want) => Assert.Equal(want, AppSettings.ClampZoom(input));
}
