using System.Text;
using Xunit;

namespace MdViewer.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void Utf8_without_bom_is_read_as_utf8()
    {
        var text = "# Привет, мир\n\nUmlauts: äöü";
        Assert.Equal(text, MarkdownRenderer.Decode(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Utf8_bom_is_stripped()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("# Title")).ToArray();
        Assert.Equal("# Title", MarkdownRenderer.Decode(bytes));
    }

    [Fact]
    public void Utf16_with_bom_is_decoded()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("# Заголовок")).ToArray();
        Assert.Equal("# Заголовок", MarkdownRenderer.Decode(bytes));
    }

    [Fact]
    public void Invalid_utf8_falls_back_to_a_legacy_code_page()
    {
        // "# Привет" in Windows-1251: not valid UTF-8, so it must come back as text
        // from the system code page rather than as replacement characters.
        var bytes = new byte[] { (byte)'#', (byte)' ', 0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2 };
        var decoded = MarkdownRenderer.Decode(bytes);
        Assert.DoesNotContain('�', decoded);
        Assert.StartsWith("# ", decoded);
        Assert.Equal(8, decoded.Length);
    }

    [Fact]
    public void Front_matter_is_hidden_and_tables_render()
    {
        var html = MarkdownRenderer.ToHtml("---\ntitle: x\n---\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
        Assert.DoesNotContain("title: x", html);
        Assert.Contains("<table>", html);
    }
}
