using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Silo.Plugins;
using Silo.Server.Api;
using Silo.Server.Core;
using Xunit;

namespace Silo.Tests;

/// <summary>0.1.5: HTTPS, the service's own account and the plugin sandbox.</summary>
public class LockdownTests
{
    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "silo-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static string MakePfx(string dir, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=silo-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(notBefore, notAfter);
        var path = Path.Combine(dir, "test.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        return path;
    }

    [Fact]
    public void Https_is_off_when_no_address_asks_for_it()
    {
        var dir = TempDir();
        var plan = Https.Prepare(new ServerSettings { Listen = ["http://localhost:47820"] }, new Log(dir));
        Assert.Null(plan.Certificate);
        Assert.Null(plan.HttpsPort);
        Assert.Equal("off", plan.Status);
    }

    [Fact]
    public void Https_uses_a_valid_certificate()
    {
        var dir = TempDir();
        var pfx = MakePfx(dir, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        var plan = Https.Prepare(new ServerSettings { Listen = ["https://*:47821", "http://localhost:47820"], Certificate = pfx }, new Log(dir));
        Assert.NotNull(plan.Certificate);
        Assert.Equal(47821, plan.HttpsPort);
        Assert.StartsWith("on, port 47821", plan.Status);
        Assert.Equal(2, plan.Listen.Count);
    }

    [Fact]
    public void Without_a_usable_certificate_the_server_stays_on_this_computer()
    {
        var dir = TempDir();
        var pfx = MakePfx(dir, DateTimeOffset.Now.AddYears(-2), DateTimeOffset.Now.AddDays(-1));   // expired
        var plan = Https.Prepare(new ServerSettings { Listen = ["https://*:47821", "http://*:47820"], Certificate = pfx }, new Log(dir));
        Assert.Null(plan.Certificate);
        Assert.Equal(["http://localhost:47820"], plan.Listen);
        Assert.Contains("expired", plan.Status);

        var missing = Https.Prepare(new ServerSettings { Listen = ["https://*:443"], Certificate = Path.Combine(dir, "none.pfx") }, new Log(dir));
        Assert.Equal(["http://localhost:47820"], missing.Listen);
    }

    [Theory]
    [InlineData("https://*:47821", 47821)]
    [InlineData("https://+:443", 443)]
    [InlineData("https://silo.corp.local", 443)]
    [InlineData("http://0.0.0.0:8080", 8080)]
    public void Reads_the_port_of_a_listen_address(string url, int port) => Assert.Equal(port, Https.PortOf(url, 1));

    static HttpRequest Request(string host, string path, string query = "", bool https = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = https ? "https" : "http";
        ctx.Request.Host = new HostString(host);
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        return ctx.Request;
    }

    [Fact]
    public void Other_computers_are_sent_to_https_but_this_one_is_not()
    {
        Assert.Equal("https://silo:47821/api/v1/search?q=gold", Https.RedirectTarget(Request("silo:47820", "/api/v1/search", "?q=gold"), false, 47821));
        Assert.Equal("https://silo.corp.local/", Https.RedirectTarget(Request("silo.corp.local", "/"), false, 443));
        Assert.Null(Https.RedirectTarget(Request("localhost:47820", "/admin"), true, 47821));     // this computer
        Assert.Null(Https.RedirectTarget(Request("silo:47821", "/", https: true), false, 47821)); // already HTTPS
        Assert.Null(Https.RedirectTarget(Request("silo:47820", "/"), false, null));               // HTTPS off
    }

    [Fact]
    public void Adding_a_folder_the_service_cannot_see_names_the_account()
    {
        var why = AdminRoutes.CannotRead(Path.Combine(TempDir(), "missing"));
        Assert.NotNull(why);
        Assert.Contains(DataFolder.RunningAs(), why);
        Assert.Null(AdminRoutes.CannotRead(TempDir()));
    }

    [Fact]
    public void Plugins_are_sandboxed_by_default_and_may_opt_out()
    {
        Assert.True(new ServerSettings().SandboxPlugins);
        Assert.False(new ServerSettings().OcrLowIntegrity);
        Assert.True(new PluginManifest().Sandbox);
        var m = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>("""{"id":"x","sandbox":false}""", Contract.Json)!;
        Assert.False(m.Sandbox);
    }

    [Fact]
    public void A_plugin_not_asked_to_sandbox_does_nothing()
    {
        var before = Environment.GetEnvironmentVariable(Sandbox.EnvMode);
        try
        {
            Environment.SetEnvironmentVariable(Sandbox.EnvMode, null);
            Assert.Null(Sandbox.ApplyIfAsked());
            Environment.SetEnvironmentVariable(Sandbox.EnvMode, "0");
            Assert.Null(Sandbox.ApplyIfAsked());
        }
        finally { Environment.SetEnvironmentVariable(Sandbox.EnvMode, before); }
    }
}
