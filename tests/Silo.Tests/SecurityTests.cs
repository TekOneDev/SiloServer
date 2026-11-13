using Silo.Server.Api;
using Xunit;

namespace Silo.Tests;

/// <summary>0.1.4: an indexed file is never served as a page of the server's own site.</summary>
public class SecurityTests
{
    [Theory]
    [InlineData("text/html", ".html", "application/octet-stream", false)]
    [InlineData("text/html", ".HTM", "application/octet-stream", false)]
    [InlineData("image/svg+xml", ".svg", "application/octet-stream", false)]
    [InlineData("text/xml", ".xml", "application/octet-stream", false)]
    [InlineData("application/xhtml+xml", ".xhtml", "application/octet-stream", false)]
    [InlineData("application/javascript", ".js", "application/octet-stream", false)]
    [InlineData("application/octet-stream", ".dm", "application/octet-stream", false)]
    [InlineData("application/pdf", ".pdf", "application/pdf", true)]
    [InlineData("image/png", ".png", "image/png", true)]
    [InlineData("image/jpeg", ".JPG", "image/jpeg", true)]
    [InlineData("text/plain", ".las", "text/plain; charset=utf-8", true)]
    [InlineData("text/csv", ".csv", "text/plain; charset=utf-8", true)]
    public void Only_pdfs_pictures_and_plain_text_show_in_the_browser(string detected, string ext, string served, bool inline)
    {
        var (type, isInline) = ApiRoutes.Delivery(detected, ext);
        Assert.Equal(served, type);
        Assert.Equal(inline, isInline);
    }

    [Theory]
    [InlineData("http://localhost:47820", true)]
    [InlineData("http://127.0.0.1:47820", true)]
    [InlineData("http://*:47820", false)]
    [InlineData("http://+:47820", false)]
    [InlineData("http://0.0.0.0:47820", false)]
    [InlineData("http://silo.corp.example:47820", false)]
    public void Knows_which_listen_addresses_are_this_machine_only(string url, bool local) =>
        Assert.Equal(local, ApiRoutes.IsLoopbackUrl(url));
}
