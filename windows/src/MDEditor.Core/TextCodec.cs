using System.Text;

namespace MDEditor.Core;

public enum TextFileEncoding { Utf8, Utf8Bom, Utf16LE, Utf16BE, Legacy }

public enum LineEnding { Lf, CrLf }

/// <summary>File contents normalised to '\n' plus what is needed to write them back the same way.</summary>
public sealed record DecodedText(string Text, TextFileEncoding Encoding, LineEnding LineEnding);

/// <summary>Decoding rules from HANDOFF §6.1 (authoritative) and MarkdownDocument.swift.</summary>
public static class TextCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding PlainUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    static TextCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static bool LooksBinary(ReadOnlySpan<byte> data)
    {
        if (StartsWith(data, 0xFF, 0xFE) || StartsWith(data, 0xFE, 0xFF)) return false;
        return data[..Math.Min(data.Length, 65536)].Contains((byte)0);
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, params ReadOnlySpan<byte> prefix) => data.StartsWith(prefix);

    public static DecodedText Decode(byte[] data)
    {
        string raw;
        TextFileEncoding encoding;

        if (StartsWith(data, 0xFF, 0xFE))
        {
            raw = Encoding.Unicode.GetString(data, 2, data.Length - 2);
            encoding = TextFileEncoding.Utf16LE;
        }
        else if (StartsWith(data, 0xFE, 0xFF))
        {
            raw = Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            encoding = TextFileEncoding.Utf16BE;
        }
        else
        {
            bool bom = StartsWith(data, 0xEF, 0xBB, 0xBF);
            try
            {
                raw = bom ? StrictUtf8.GetString(data, 3, data.Length - 3) : StrictUtf8.GetString(data);
                encoding = bom ? TextFileEncoding.Utf8Bom : TextFileEncoding.Utf8;
            }
            catch (DecoderFallbackException)
            {
                raw = Encoding.GetEncoding(1252).GetString(data);
                encoding = TextFileEncoding.Legacy;
            }
        }

        var (text, lineEnding) = NormalizeLineEndings(raw);
        return new DecodedText(text, encoding, lineEnding);
    }

    /// <summary>Converts CRLF and bare CR to LF; reports the majority style (ties and no newlines are LF).</summary>
    public static (string Text, LineEnding Ending) NormalizeLineEndings(string raw)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\n') continue;
            if (i > 0 && raw[i - 1] == '\r') crlf++; else lf++;
        }
        string text = raw.Contains('\r') ? raw.Replace("\r\n", "\n").Replace('\r', '\n') : raw;
        return (text, crlf > lf ? LineEnding.CrLf : LineEnding.Lf);
    }

    /// <summary>Legacy single-byte files are written back as UTF-8; everything else keeps its encoding.</summary>
    public static byte[] Encode(string text, TextFileEncoding encoding, LineEnding lineEnding)
    {
        string body = lineEnding == LineEnding.CrLf ? text.Replace("\n", "\r\n") : text;
        return encoding switch
        {
            TextFileEncoding.Utf8Bom => [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. PlainUtf8.GetBytes(body)],
            TextFileEncoding.Utf16LE => [.. new byte[] { 0xFF, 0xFE }, .. Encoding.Unicode.GetBytes(body)],
            TextFileEncoding.Utf16BE => [.. new byte[] { 0xFE, 0xFF }, .. Encoding.BigEndianUnicode.GetBytes(body)],
            _ => PlainUtf8.GetBytes(body),
        };
    }
}

public static class SafeFile
{
    /// <summary>
    /// Writes to a temp file in the same folder and swaps it in, so a sync client (Dropbox, OneDrive…)
    /// never sees a half-written document.
    /// </summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { /* best effort: the original error matters more */ }
            catch (UnauthorizedAccessException) { /* same */ }
        }
    }
}
