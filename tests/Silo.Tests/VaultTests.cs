using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Silo.Plugins;
using Silo.Plugins.Vault;
using Silo.Server.Api;
using Xunit;

namespace Silo.Tests;

/// <summary>0.1.7: the Autodesk Vault source, against a pretend Vault (see <see cref="FakeVault"/>).</summary>
public class VaultTests
{
    sealed class FakeSids : ISidResolver
    {
        public static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"CORP\alice"] = "S-1-5-21-9-1001", [@"CORP\mallory"] = "S-1-5-21-9-1003", [@"CORP\Drafting"] = "S-1-5-21-9-2002",
            [@"CORP\bob.smith"] = "S-1-5-21-9-1002", [@"CORP\Vault Users"] = "S-1-5-21-9-2100",
        };
        public string? Sid(string account) => account.StartsWith("S-1-") ? account : Known.GetValueOrDefault(account);
    }

    const string Alice = "S-1-5-21-9-1001", Bob = "S-1-5-21-9-1002", Mallory = "S-1-5-21-9-1003", Drafting = "S-1-5-21-9-2002";

    static JsonObject Config(Action<JsonObject>? tweak = null)
    {
        var c = new JsonObject
        {
            ["server"] = "http://vault.test", ["vault"] = "Vault", ["user"] = "silo-indexer", ["password"] = "pw", ["domain"] = "CORP",
            ["exclude"] = new JsonArray("$/Archive*", "*.bak"), ["version"] = "released",
        };
        tweak?.Invoke(c);
        return c;
    }

    static (VaultSource source, FakeVault fake) Source()
    {
        var fake = new FakeVault();
        return (new VaultSource(new FakeSids(), c => new VaultClient(c, fake)), fake);
    }

    static async Task<List<SourceItem>> List(JsonObject? cfg = null)
    {
        var (src, _) = Source();
        var items = new List<SourceItem>();
        await src.EnumerateAsync(new EnumerateArgs { SourceId = "v", Config = cfg ?? Config() }, items.Add, CancellationToken.None);
        return items;
    }

    static string? M(SourceItem i, string k) => i.Meta.TryGetValue(k, out var l) && l.Count > 0 ? l[0] : null;

    [Fact]
    public void Reads_a_wcf_service_description_and_builds_requests_in_its_namespace()
    {
        var svc = SoapService.FromWsdl("DocumentService", new Uri("http://vault.test/AutodeskDM/Services/v31/DocumentService.svc"), FakeVault.Wsdl("DocumentService"));
        Assert.False(svc.Soap12);
        Assert.Equal(["parentFolderId", "recurse"], svc.ParamsOf("GetFoldersByParentId"));
        var env = XDocument.Parse(svc.Envelope("GetDownloadTicketsByFileIds", new Dictionary<string, object?> { ["fileIds"] = new long[] { 5, 6 } }, null));
        XNamespace ns = "http://AutodeskDM/Services/Document/1/7/2020/";
        var call = env.Descendants(ns + "GetDownloadTicketsByFileIds").Single();
        Assert.Equal(["5", "6"], call.Element(ns + "fileIds")!.Elements(ns + "long").Select(e => e.Value));
        Assert.Throws<InvalidOperationException>(() => svc.Envelope("GetFoldersByParentId", new Dictionary<string, object?> { ["nope"] = 1 }, null));
    }

    [Fact]
    public void Reads_mtom_replies_and_vault_faults()
    {
        var body = "--b\r\nContent-ID: <root>\r\nContent-Type: application/xop+xml\r\n\r\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><R><RResult><xop:Include xmlns:xop=\"http://www.w3.org/2004/08/xop/include\" href=\"cid:p1\"/></RResult></R></s:Body></s:Envelope>\r\n"
            + "--b\r\nContent-ID: <p1>\r\n\r\nHELLO\r\n--b--\r\n";
        var r = SoapService.ReadReply("R", Encoding.ASCII.GetBytes(body), "multipart/related", "b", System.Net.HttpStatusCode.OK);
        Assert.Equal("HELLO", Encoding.ASCII.GetString(Convert.FromBase64String(r.Result!.Value)));

        var fault = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultstring>nope</faultstring><detail><sl:sldetail xmlns:sl=\"x\"><sl:errorcode>300</sl:errorcode></sl:sldetail></detail></s:Fault></s:Body></s:Envelope>";
        var e = Assert.Throws<VaultFault>(() => SoapService.ReadReply("Op", Encoding.UTF8.GetBytes(fault), "text/xml", null, System.Net.HttpStatusCode.InternalServerError));
        Assert.Equal(300, e.Code);
    }

    [Fact]
    public async Task Signs_in_read_only_and_lists_files_with_vault_paths_and_properties()
    {
        var (src, fake) = Source();
        var items = new List<SourceItem>();
        var res = await src.EnumerateAsync(new EnumerateArgs { SourceId = "v", Config = Config() }, items.Add, CancellationToken.None);
        Assert.Contains("SignInReadOnly", fake.Calls);
        Assert.DoesNotContain("SignIn", fake.Calls);
        Assert.True(res.Complete);
        // Archive is excluded, so old.dwg is not listed.
        Assert.Equal(["$/Designs/Bracket.ipt", "$/Designs/Haul road.dwg", "$/Designs/Pit/Pit shell.dxf", "$/readme.txt"], items.Select(i => i.Location!).OrderBy(x => x, StringComparer.Ordinal));
        var bracket = items.Single(i => i.Name == "Bracket.ipt");
        Assert.Equal("m:3000", bracket.Key);
        Assert.Null(bracket.Path);
        Assert.Equal(".ipt", bracket.Ext);
        Assert.Equal("PN-301", M(bracket, "vault_part"));
        Assert.Equal("Title of 301", M(bracket, "title"));
        Assert.Equal("J24011", M(bracket, "vault_project"));
        Assert.Equal("Released", M(bracket, "vault_state"));
        Assert.Equal("Engineering", M(bracket, "vault_category"));
        Assert.Equal("$/Designs", M(bracket, "vault_folder"));
        Assert.Null(M(bracket, "abstract"));   // empty Vault properties are left out
        Assert.StartsWith("http://vault.test/AutodeskDM/Services/EntityDataCommandRequest.aspx?Vault=Vault&ObjectId=%24%2FDesigns%2FBracket.ipt", bracket.Url);
        Assert.Equal("Open in Vault", M(bracket, "landing_label"));
    }

    [Fact]
    public async Task Indexes_the_latest_released_version_and_the_latest_of_never_released_files()
    {
        var items = await List();
        var road = items.Single(i => i.Name == "Haul road.dwg");
        Assert.StartsWith("201:", road.Version);       // v1 is the released one; v3 is work in progress
        Assert.Equal("A", M(road, "vault_rev"));
        Assert.Equal("Released", M(road, "vault_state"));
        var shell = items.Single(i => i.Name == "Pit shell.dxf");
        Assert.Equal("Work in Progress", M(shell, "vault_state"));

        var latest = await List(Config(c => c["version"] = "latest"));
        Assert.StartsWith("203:", latest.Single(i => i.Name == "Haul road.dwg").Version);
        var skip = await List(Config(c => c["unreleased"] = "skip"));
        Assert.DoesNotContain(skip, i => i.Name == "Pit shell.dxf");
        Assert.Contains(skip, i => i.Name == "readme.txt");   // no lifecycle at all: not a "never released" file
    }

    [Fact]
    public async Task Maps_vault_permissions_to_windows_sids_and_fails_closed()
    {
        var items = await List();
        // No ACL anywhere: every active Vault user with a Windows account (bob is Vault-only, so not him).
        var readme = items.Single(i => i.Name == "readme.txt");
        Assert.Equal([Alice, Mallory], readme.Allow.OrderBy(x => x));
        // Folder ACL: the Vault-only group Engineers is expanded to its members; Mallory's deny is kept (deny wins).
        var road = items.Single(i => i.Name == "Haul road.dwg");
        Assert.Equal([Alice, Mallory], road.Allow.OrderBy(x => x));
        Assert.Equal([Mallory], road.Deny);
        // Inherited by the subfolder.
        Assert.Equal(road.Allow.OrderBy(x => x), items.Single(i => i.Name == "Pit shell.dxf").Allow.OrderBy(x => x));
        // Own ACL (Drafting AD group + alice) combined with a state ACL (alice): only alice passes both.
        var bracket = items.Single(i => i.Name == "Bracket.ipt");
        Assert.Equal([Alice], bracket.Allow);

        // A user map brings Vault-only bob in.
        var mapped = await List(Config(c => c["userMap"] = new JsonObject { ["bob"] = @"CORP\bob.smith" }));
        Assert.Contains(Bob, mapped.Single(i => i.Name == "readme.txt").Allow);
        // "everyone" names one group for files with no ACL.
        var everyone = await List(Config(c => c["everyone"] = @"CORP\Vault Users"));
        Assert.Equal(["S-1-5-21-9-2100"], everyone.Single(i => i.Name == "readme.txt").Allow);
    }

    [Fact]
    public void Acl_mapper_rules()
    {
        var dir = new VaultDirectory(
            [new VUser(1, "alice", "ActiveDir", true, null, null, null, false), new VUser(2, "bob", "Vault", true, null, null, null, false), new VUser(3, "gone", "ActiveDir", false, null, null, null, false)],
            [new VGroup(10, "Drafting", "ActiveDir", true, false), new VGroup(11, "Local", "Vault", true, false), new VGroup(12, "Nested", "Vault", true, false)]);
        dir.Members[11] = ([2, 3], [12]);
        dir.Members[12] = ([1], [11]);   // a cycle
        var m = new AclMapper(dir, new AclOptions { Domain = "CORP" }, new FakeSids());
        static VAcl Acl(params (long who, bool read)[] e) => new(1, e.Select(x => new VAce(x.who, new Dictionary<long, bool> { [1] = x.read })).ToList());

        Assert.Equal([Drafting], m.Map(Acl((10, true)), null, null, false).Allow);              // AD group keeps its own SID
        Assert.Equal([Alice], m.Map(Acl((11, true)), null, null, false).Allow);                 // Vault group: members, through the cycle; bob unmapped, gone inactive
        var none = m.Map(Acl((2, true)), null, null, false);
        Assert.Empty(none.Allow);                                                                // only an unmapped user: nobody
        Assert.NotNull(none.Problem);
        Assert.Contains("user bob", m.Unmapped);
        Assert.Empty(m.Map(Acl((1, true)), "Combined", null, true).Allow);                      // state security unreadable: hidden
        Assert.Equal([Alice], new AclMapper(dir, new AclOptions { Domain = "CORP", StrictStateSecurity = false }, new FakeSids()).Map(Acl((1, true)), "Combined", null, true).Allow);
        Assert.Empty(m.Map(Acl((1, true)), "Combined", Acl((1, false)), true).Allow);           // denied by the state ACL
        Assert.Equal([Alice], m.Map(Acl((10, true)), "Override", Acl((1, true)), true).Allow);  // state ACL overrides
        var denied = m.Map(Acl((1, true), (11, false)), null, null, false);                     // a deny through a Vault group lands on its members
        Assert.Contains(Alice, denied.Deny);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fetches_the_indexed_version_into_the_scratch_folder_in_parts(bool mtom)
    {
        var fake = new FakeVault { Mtom = mtom, Content = Enumerable.Range(0, 20).Select(i => (byte)('a' + i)).ToArray() };
        var vc = new VaultClient(new VaultConnection { Server = "http://vault.test", Vault = "Vault", User = "u", Password = "pw" }, fake);
        var dir = Path.Combine(Path.GetTempPath(), "silo-vault-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "x.dwg");
            await vc.DownloadAsync(new VFile { Id = 301, MasterId = 3000, Name = "x.dwg", Size = 20 }, path, CancellationToken.None, partBytes: 7);
            Assert.Equal("abcdefghijklmnopqrst", File.ReadAllText(path));
            Assert.Equal(3, fake.Calls.Count(c => c == "DownloadFilePart"));

            var src = new VaultSource(new FakeSids(), c => new VaultClient(c, new FakeVault { Mtom = mtom }));
            var got = await src.FetchAsync(new EnumerateArgs { SourceId = "v", Config = Config() }, "m:3000", dir, CancellationToken.None);
            Assert.NotNull(got);
            Assert.StartsWith(Path.Combine(dir, "silo-fetch-"), got);
            Assert.Equal("Bracket.ipt", Path.GetFileName(got));
            Assert.Null(await src.FetchAsync(new EnumerateArgs { SourceId = "v", Config = Config() }, "../../etc", dir, CancellationToken.None));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_wrong_password_says_so()
    {
        var (src, _) = Source();
        var e = await Assert.ThrowsAsync<VaultFault>(() => src.EnumerateAsync(new EnumerateArgs { SourceId = "v", Config = Config(c => c["password"] = "bad") }, _ => { }, CancellationToken.None));
        Assert.Contains("password", e.Message);
    }

    [Fact]
    public async Task Probe_report_covers_services_permissions_and_download_without_the_password()
    {
        var fake = new FakeVault();
        var lines = new List<string>();
        await VaultProbe.RunAsync(Config(), lines.Add, CancellationToken.None, c => new VaultClient(c, fake), new FakeSids());
        var text = string.Join("\n", lines);
        Assert.Contains("v31", text);
        Assert.Contains("GetEntACLsByEntityIds(entityIds)", text);
        Assert.Contains("(OK)", text);
        Assert.Contains("$/Designs/Bracket.ipt: allow 1 SIDs", text);
        Assert.DoesNotContain("\"pw\"", text);
        Assert.Contains("Nothing in Vault was changed", text);
    }

    [Fact]
    public void Admin_never_shows_source_passwords()
    {
        var masked = AdminRoutes.Masked(new JsonObject { ["server"] = "http://v", ["password"] = "secret", ["apiKey"] = "", ["user"] = "u" });
        Assert.Equal("********", masked["password"]!.GetValue<string>());
        Assert.Equal("", masked["apiKey"]!.GetValue<string>());
        Assert.Equal("u", masked["user"]!.GetValue<string>());
    }

    [Fact]
    public void Built_in_manifest_says_the_source_fetches()
    {
        var m = new VaultSource().Manifest;
        Assert.True(m.Fetches);
        Assert.Equal("source", m.Kind);
        Assert.Contains(m.Fields, f => f.Name == "vault_state" && f.Facet);
    }
}
