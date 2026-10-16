using System.Runtime.InteropServices;
using System.Text;
using Markdig;

namespace MdViewer;

/// <summary>Converts Markdown text to the HTML fragment shown in the viewer.</summary>
internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()   // tables, footnotes, task lists, auto ids, ...
        .UseEmojiAndSmiley()
        .UseYamlFrontMatter()      // hide front matter instead of printing it
        .Build();

    public static string ToHtml(string markdown) => Markdown.ToHtml(markdown, Pipeline);

    /// <summary>Reads a text file, honouring a BOM and falling back to UTF-8.</summary>
    public static string ReadFile(string path)
    {
        // Share the file so editors can keep it open while we read.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Decode(buffer.ToArray());
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Lazy<Encoding> SystemAnsi = new(() =>
    {
        // The legacy code pages are not registered by default on .NET Core.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            // The code page old editors wrote in is the one Windows uses for non-Unicode
            // programs (the "system locale"), which need not match the display language.
            return Encoding.GetEncoding((int)GetACP());
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    });

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    /// <summary>
    /// A byte order mark wins. Without one the file is read as UTF-8, which is what nearly
    /// every Markdown file is; only if that fails outright is it read in the system's
    /// legacy code page, so a document saved by an old editor in, say, Windows-1251 still
    /// shows its letters instead of replacement characters.
    /// </summary>
    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return SystemAnsi.Value.GetString(bytes);
        }
    }
}
