using System.Text.RegularExpressions;

namespace MDEditor.Core;

public enum LinkActionKind { Ignore, Beep, OpenUri, OpenInEditor, OpenWithDefaultApp, Reveal }

public sealed record LinkAction(LinkActionKind Kind, string? Target = null);

/// <summary>
/// Decides what a clicked link may do. Port of openLink in SplitEditorView.swift with the Windows
/// allow-list of HANDOFF §6.7. Documents are untrusted: nothing executable is ever launched.
/// </summary>
public static partial class LinkResolver
{
    private static readonly HashSet<string> WebSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "tel" };

    private static readonly HashSet<string> EditorExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "md", "markdown", "mdown", "mkd", "mdwn", "txt",
    };

    private static readonly HashSet<string> DefaultAppExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "webp", "svg", "bmp", "ico", "tif", "tiff", "heic", "avif",
        "pdf",
        "mp3", "wav", "m4a", "flac", "mp4", "mov", "webm", "mkv",
        "html", "htm",
        "csv", "json", "xml", "log", "yml", "yaml", "toml",
    };

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9+.\-]*):", RegexOptions.CultureInvariant)]
    private static partial Regex Scheme();

    /// <param name="href">Raw href from the preview.</param>
    /// <param name="documentFolder">Folder of the saved document, or null when it has never been saved.</param>
    /// <param name="exists">File-or-folder existence check (injected for tests).</param>
    public static LinkAction Resolve(string href, string? documentFolder, Func<string, bool> exists)
    {
        var scheme = Scheme().Match(href);
        if (scheme.Success)
        {
            return WebSchemes.Contains(scheme.Groups[1].Value)
                ? new LinkAction(LinkActionKind.OpenUri, href)
                : new LinkAction(LinkActionKind.Ignore);
        }

        string path = href.Replace('\\', '/');
        int fragment = path.IndexOf('#');
        if (fragment >= 0) path = path[..fragment];
        path = Uri.UnescapeDataString(path);
        if (path.Length == 0) return new LinkAction(LinkActionKind.Ignore);

        // After decoding: no UNC paths (clicking one would leak credentials to a remote host),
        // no drive letters and no NTFS alternate data streams.
        string slashed = path.Replace('\\', '/');
        if (slashed.StartsWith("//", StringComparison.Ordinal) || slashed.Contains(':'))
            return new LinkAction(LinkActionKind.Ignore);

        if (documentFolder is null) return new LinkAction(LinkActionKind.Beep);
        string target = Path.GetFullPath(Path.Combine(documentFolder, slashed));
        if (!exists(target)) return new LinkAction(LinkActionKind.Beep);

        string extension = Path.GetExtension(target).TrimStart('.');
        if (EditorExtensions.Contains(extension)) return new LinkAction(LinkActionKind.OpenInEditor, target);
        if (DefaultAppExtensions.Contains(extension)) return new LinkAction(LinkActionKind.OpenWithDefaultApp, target);
        return new LinkAction(LinkActionKind.Reveal, target);
    }
}
