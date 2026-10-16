using Xunit;

namespace MdViewer.Tests;

public class LinkPolicyTests
{
    private const string Folder = @"C:\docs\guide";

    // ---------- Links the user clicks ----------

    [Theory]
    [InlineData("intro.md", @"C:\docs\guide\intro.md")]
    [InlineData("sub/page.md", @"C:\docs\guide\sub\page.md")]
    [InlineData("../other.md", @"C:\docs\other.md")]
    [InlineData("../../far.md", @"C:\far.md")]
    [InlineData("with%20space.md", @"C:\docs\guide\with space.md")]
    [InlineData("page.md#section", @"C:\docs\guide\page.md")]
    [InlineData("../../../../../../root.md", @"C:\root.md")]
    public void Link_inside_the_volume_resolves(string href, string expected)
    {
        Assert.Equal(expected, LinkPolicy.ResolveLinkTarget(href, Folder));
    }

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("/absolute/path.md")]
    [InlineData(@"\\server\share\doc.md")]
    [InlineData("//server/share/doc.md")]
    [InlineData(@"D:\other.md")]
    [InlineData("")]
    [InlineData("#only-an-anchor")]
    public void Rooted_or_empty_link_is_refused(string href)
    {
        Assert.Null(LinkPolicy.ResolveLinkTarget(href, Folder));
    }

    [Fact]
    public void Link_on_a_share_stays_on_that_share()
    {
        // Climbing never gets above the share itself, and a drive letter in the middle
        // of a relative link is just an odd file name there, not a jump to C:.
        Assert.Equal(@"\\server\share\x.md",
            LinkPolicy.ResolveLinkTarget(@"..\..\..\..\x.md", @"\\server\share\docs"));
        Assert.Equal(@"\\server\share\C:\x.md",
            LinkPolicy.ResolveLinkTarget(@"..\..\C:\x.md", @"\\server\share\docs"));
    }

    // ---------- External links ----------

    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("http://example.com")]
    [InlineData("mailto:someone@example.com")]
    public void Web_schemes_are_allowed(string url)
    {
        Assert.NotNull(LinkPolicy.SafeExternalUri(url));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:display")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("file://server/share/x.lnk")]
    [InlineData("not a url")]
    public void Other_schemes_are_refused(string url)
    {
        Assert.Null(LinkPolicy.SafeExternalUri(url));
    }

    // ---------- Files the page embeds ----------

    [Fact]
    public void Base_url_spells_out_the_document_folder()
    {
        Assert.Equal("https://doc.mdviewer.local/C%3A/docs/guide/", LinkPolicy.DocumentBaseUrl(Folder));
    }

    [Fact]
    public void Base_url_marks_a_network_share()
    {
        Assert.Equal("https://doc.mdviewer.local/unc/server/share/docs/",
            LinkPolicy.DocumentBaseUrl(@"\\server\share\docs"));
    }

    [Fact]
    public void Base_url_escapes_awkward_characters()
    {
        var url = LinkPolicy.DocumentBaseUrl(@"C:\my docs\a#b");
        Assert.Equal("https://doc.mdviewer.local/C%3A/my%20docs/a%23b/", url);
    }

    [Theory]
    [InlineData("https://doc.mdviewer.local/C%3A/docs/guide/img/logo.png", @"C:\docs\guide\img\logo.png")]
    [InlineData("https://doc.mdviewer.local/C:/docs/guide/img/logo.png", @"C:\docs\guide\img\logo.png")]
    [InlineData("https://doc.mdviewer.local/C%3A/docs/images/shared.png", @"C:\docs\images\shared.png")]
    [InlineData("https://doc.mdviewer.local/C%3A/docs/guide/my%20pic.png", @"C:\docs\guide\my pic.png")]
    public void Embedded_file_on_the_same_volume_is_served(string uri, string expected)
    {
        Assert.Equal(expected, LinkPolicy.ResolveDocumentFile(uri, Folder));
    }

    [Fact]
    public void Browser_resolves_parent_paths_against_the_base_url()
    {
        // What the page does with "../images/shared.png" and the base URL above.
        var resolved = new Uri(new Uri(LinkPolicy.DocumentBaseUrl(Folder)), "../images/shared.png");
        Assert.Equal(@"C:\docs\images\shared.png", LinkPolicy.ResolveDocumentFile(resolved.ToString(), Folder));
    }

    [Fact]
    public void Embedded_file_on_a_share_is_served_to_a_share_document()
    {
        Assert.Equal(@"\\server\share\docs\a.png",
            LinkPolicy.ResolveDocumentFile("https://doc.mdviewer.local/unc/server/share/docs/a.png", @"\\server\share\docs"));
    }

    [Theory]
    [InlineData("https://doc.mdviewer.local/D%3A/secret.png")]
    [InlineData("https://doc.mdviewer.local/unc/server/share/a.png")]
    [InlineData("https://doc.mdviewer.local/")]
    [InlineData("https://doc.mdviewer.local/logo.png")]
    [InlineData("https://doc.mdviewer.local/C%3A/docs/guide/..%5C..%5Cx.png")]
    [InlineData("https://doc.mdviewer.local/C%3A/docs/guide/..%2F..%2Fx.png")]
    [InlineData("https://assets.mdviewer.local/C%3A/docs/guide/a.png")]
    [InlineData("https://doc.mdviewer.local/unc/server")]
    [InlineData("https://doc.mdviewer.local/unc/server/share")]
    [InlineData("https://doc.mdviewer.local/C%3A")]
    [InlineData("https://doc.mdviewer.local/CC%3A/x.png")]
    [InlineData("https://doc.mdviewer.local/docs/guide/x.png")]
    public void Embedded_file_elsewhere_is_refused(string uri)
    {
        Assert.Null(LinkPolicy.ResolveDocumentFile(uri, Folder));
    }

    [Fact]
    public void Share_document_cannot_embed_from_a_local_drive()
    {
        Assert.Null(LinkPolicy.ResolveDocumentFile("https://doc.mdviewer.local/C%3A/x.png", @"\\server\share\docs"));
    }

    // ---------- Kinds of file ----------

    [Theory]
    [InlineData(@"C:\a\readme.md", true)]
    [InlineData(@"C:\a\README.MD", true)]
    [InlineData(@"C:\a\notes.txt", true)]
    [InlineData(@"C:\a\image.png", false)]
    public void Markdown_files_are_recognised(string path, bool expected)
    {
        Assert.Equal(expected, LinkPolicy.IsMarkdown(path));
    }

    [Theory]
    [InlineData(@"C:\a\setup.exe", true)]
    [InlineData(@"C:\a\Run.BAT", true)]
    [InlineData(@"C:\a\shortcut.lnk", true)]
    [InlineData(@"C:\a\script.ps1", true)]
    [InlineData(@"C:\a\report.pdf", false)]
    [InlineData(@"C:\a\photo.jpg", false)]
    public void Executable_files_are_recognised(string path, bool expected)
    {
        Assert.Equal(expected, LinkPolicy.IsExecutable(path));
    }
}
