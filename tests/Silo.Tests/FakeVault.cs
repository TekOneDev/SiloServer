using System.Net;
using System.Text;
using System.Xml.Linq;

namespace Silo.Tests;

/// <summary>
/// A pretend Vault server for tests: answers ?wsdl with a WCF-shaped description and SOAP calls with records in
/// Vault's attribute style. Built from the Vault SDK's documented operation and field names.
/// </summary>
public sealed class FakeVault : HttpMessageHandler
{
    public const string Version = "v31";
    public readonly List<string> Calls = new();
    public byte[] Content = Encoding.ASCII.GetBytes("0123456789ABCDEFGHIJ");
    public bool Mtom = true;

    static readonly Dictionary<string, (string path, string ns, (string op, string[] ps)[] ops)> Services = new()
    {
        ["AuthService"] = ($"Filestore/{Version}/AuthService.svc", "http://AutodeskDM/Filestore/Auth/1/7/2020/",
            [("SignIn", ["dataServer", "userName", "userPassword", "knowledgeVault"]), ("SignInReadOnly", ["dataServer", "userName", "userPassword", "knowledgeVault"]), ("SignOut", [])]),
        ["DocumentService"] = ($"{Version}/DocumentService.svc", "http://AutodeskDM/Services/Document/1/7/2020/",
            [("GetFolderRoot", []), ("GetFolderByPath", ["path"]), ("GetFoldersByParentId", ["parentFolderId", "recurse"]), ("GetLatestFilesByFolderId", ["folderId", "includeHidden"]),
             ("GetFilesByMasterId", ["fileMasterId"]), ("GetLatestFileByMasterId", ["fileMasterId"]), ("GetFileById", ["fileId"]), ("GetDownloadTicketsByFileIds", ["fileIds"])]),
        ["PropertyService"] = ($"{Version}/PropertyService.svc", "http://AutodeskDM/Services/Property/1/7/2020/",
            [("GetPropertyDefinitionsByEntityClassId", ["entityClassId"]), ("GetPropertiesByEntityIds", ["entityClassId", "entityIds"])]),
        ["SecurityService"] = ($"{Version}/SecurityService.svc", "http://AutodeskDM/Services/Security/1/7/2020/",
            [("GetEntACLsByEntityIds", ["entityIds"])]),
        ["AdminService"] = ($"{Version}/AdminService.svc", "http://AutodeskDM/Services/Admin/1/7/2020/",
            [("GetAllUsers", []), ("GetAllGroups", []), ("GetGroupInfoByGroupId", ["groupId"])]),
        ["FilestoreService"] = ($"Filestore/{Version}/FilestoreService.svc", "http://AutodeskDM/Filestore/Filestore/1/7/2020/",
            [("DownloadFilePart", ["downloadTicket", "firstByte", "lastByte", "allowSync"])]),
    };

    static readonly HashSet<string> ArrayParams = ["entityIds", "fileIds"];

    public static string Wsdl(string name)
    {
        var (_, ns, ops) = Services[name];
        var sb = new StringBuilder();
        sb.Append($"""<wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:tns="{ns}" targetNamespace="{ns}" name="{name}">""");
        sb.Append($"""<wsdl:types><xs:schema elementFormDefault="qualified" targetNamespace="{ns}">""");
        sb.Append("""<xs:complexType name="ArrayOfLong"><xs:sequence><xs:element minOccurs="0" maxOccurs="unbounded" name="long" type="xs:long"/></xs:sequence></xs:complexType>""");
        sb.Append("""<xs:complexType name="ByteArray"><xs:sequence><xs:element minOccurs="0" name="Bytes" type="xs:base64Binary"/></xs:sequence></xs:complexType>""");
        foreach (var (op, ps) in ops)
        {
            sb.Append($"""<xs:element name="{op}"><xs:complexType><xs:sequence>""");
            foreach (var p in ps) sb.Append($"""<xs:element minOccurs="0" name="{p}" type="{(ArrayParams.Contains(p) ? "tns:ArrayOfLong" : p == "downloadTicket" ? "tns:ByteArray" : "xs:string")}"/>""");
            sb.Append($"""</xs:sequence></xs:complexType></xs:element><xs:element name="{op}Response"><xs:complexType><xs:sequence/></xs:complexType></xs:element>""");
        }
        sb.Append("</xs:schema></wsdl:types>");
        foreach (var (op, _) in ops)
            sb.Append($"""<wsdl:message name="{name}_{op}_In"><wsdl:part name="parameters" element="tns:{op}"/></wsdl:message><wsdl:message name="{name}_{op}_Out"><wsdl:part name="parameters" element="tns:{op}Response"/></wsdl:message>""");
        sb.Append($"""<wsdl:portType name="I{name}">""");
        foreach (var (op, _) in ops) sb.Append($"""<wsdl:operation name="{op}"><wsdl:input message="tns:{name}_{op}_In"/><wsdl:output message="tns:{name}_{op}_Out"/></wsdl:operation>""");
        sb.Append($"""</wsdl:portType><wsdl:binding name="BasicHttpBinding_I{name}" type="tns:I{name}"><soap:binding transport="http://schemas.xmlsoap.org/soap/http"/>""");
        foreach (var (op, _) in ops) sb.Append($"""<wsdl:operation name="{op}"><soap:operation soapAction="{ns}{op}" style="document"/><wsdl:input><soap:body use="literal"/></wsdl:input><wsdl:output><soap:body use="literal"/></wsdl:output></wsdl:operation>""");
        sb.Append("</wsdl:binding></wsdl:definitions>");
        return sb.ToString();
    }

    // ---- the vault's contents ----
    // $ (1) ─ Designs (10, ACL 500: Engineers allow, Mallory deny) ─ Pit (11)
    //        └ Archive (12, excluded by the test's config)
    // Users: alice (AD), bob (Vault-only), mallory (AD), admin (system). Group Engineers (Vault-only): alice, mallory, bob.
    // Group Drafting (AD).
    public string Folders => """
        <Folder Id="10" ParId="1" FullName="$/Designs" Name="Designs" Cloaked="false" IsLib="false"/>
        <Folder Id="11" ParId="10" FullName="$/Designs/Pit" Name="Pit" Cloaked="false" IsLib="false"/>
        <Folder Id="12" ParId="1" FullName="$/Archive" Name="Archive" Cloaked="false" IsLib="false"/>
        """;

    static string File(long id, long master, int ver, string name, long folder, string? state, bool released, string rev = "A", long size = 20) =>
        $"""<File Id="{id}" MasterId="{master}" Name="{name}" VerNum="{ver}" FileSize="{size}" FolderId="{folder}" CkInDate="2026-03-0{ver}T01:02:03Z" CreateDate="2025-01-01T00:00:00Z" CreateUserName="alice" Hidden="false" Cloaked="false" CheckedOut="false"><Cat CatId="1" CatName="Engineering"/><FileRev Label="{rev}"/>{(state == null ? "" : $"<FileLfCyc LfCycStateId=\"3\" LfCycStateName=\"{state}\" Consume=\"{(released ? "true" : "false")}\"/>")}</File>""";

    public string Reply(string op, XElement body)
    {
        long L(string n) => long.Parse(body.Elements().First(e => e.Name.LocalName == n).Value);
        return op switch
        {
            "GetFolderRoot" => """<Folder Id="1" ParId="-1" FullName="$" Name="$" Cloaked="false"/>""",
            "GetFoldersByParentId" => Folders,
            "GetLatestFilesByFolderId" => L("folderId") switch
            {
                1 => File(100, 1000, 1, "readme.txt", 1, null, false),
                10 => File(203, 2000, 3, "Haul road.dwg", 10, "Work in Progress", false, "B") + File(301, 3000, 1, "Bracket.ipt", 10, "Released", true),
                11 => File(401, 4000, 1, "Pit shell.dxf", 11, "Work in Progress", false),
                12 => File(501, 5000, 1, "old.dwg", 12, null, false),
                _ => "",
            },
            "GetFilesByMasterId" => L("fileMasterId") switch
            {
                2000 => File(201, 2000, 1, "Haul road.dwg", 10, "Released", true, "A") + File(202, 2000, 2, "Haul road.dwg", 10, "Work in Progress", false, "B") + File(203, 2000, 3, "Haul road.dwg", 10, "Work in Progress", false, "B"),
                4000 => File(401, 4000, 1, "Pit shell.dxf", 11, "Work in Progress", false),
                _ => "",
            },
            "GetLatestFileByMasterId" => L("fileMasterId") switch { 2000 => File(203, 2000, 3, "Haul road.dwg", 10, "Work in Progress", false, "B"), 3000 => File(301, 3000, 1, "Bracket.ipt", 10, "Released", true), _ => File(100, 1000, 1, "readme.txt", 1, null, false) },
            "GetPropertyDefinitionsByEntityClassId" => """<PropDef Id="1" DispName="Title" SysName="Title"/><PropDef Id="2" DispName="Part Number" SysName="PartNumber"/><PropDef Id="3" DispName="Project" SysName="Project"/><PropDef Id="4" DispName="Description" SysName="Description"/>""",
            "GetPropertiesByEntityIds" => string.Concat(body.Descendants().Where(e => e.Name.LocalName == "long").Select(e => e.Value).Select(id =>
                $"""<PropInst EntityId="{id}" PropDefId="1" Val="Title of {id}"/><PropInst EntityId="{id}" PropDefId="2" Val="PN-{id}"/><PropInst EntityId="{id}" PropDefId="3" Val="J24011"/><PropInst EntityId="{id}" PropDefId="4" Val=""/>""")),
            "GetEntACLsByEntityIds" => Acls(body.Descendants().Where(e => e.Name.LocalName == "long").Select(e => long.Parse(e.Value)).ToList()),
            "GetAllUsers" => """<User Id="51" Name="alice" Auth="ActiveDir" IsActive="true" IsSys="false"/><User Id="52" Name="bob" Auth="Vault" IsActive="true" IsSys="false"/><User Id="53" Name="mallory" Auth="ActiveDir" IsActive="true" IsSys="false"/><User Id="2" Name="Administrator" Auth="Vault" IsActive="true" IsSys="true"/>""",
            "GetAllGroups" => """<Group Id="61" Name="Engineers" Auth="Vault" IsActive="true" IsSys="false"/><Group Id="62" Name="Drafting" Auth="ActiveDir" IsActive="true" IsSys="false"/>""",
            "GetGroupInfoByGroupId" => L("groupId") == 61 ? """<Group Id="61" Name="Engineers"/><Users><User Id="51" Name="alice"/><User Id="52" Name="bob"/><User Id="53" Name="mallory"/></Users><Groups/>""" : """<Group Id="62" Name="Drafting"/><Users/><Groups/>""",
            "GetDownloadTicketsByFileIds" => $"""<ByteArray><Bytes>{Convert.ToBase64String(Encoding.ASCII.GetBytes("ticket-" + body.Descendants().First(e => e.Name.LocalName == "long").Value))}</Bytes></ByteArray>""",
            _ => "",
        };
    }

    /// <summary>Folder 10 has ACL 500 (Engineers read allow, mallory read deny). File master 3000 has its own ACL 600
    /// (Drafting allow) plus state ACL 700 (alice allow) combined. Everything else: no ACL.</summary>
    static string Acls(List<long> ids)
    {
        var ents = new StringBuilder("<EntACLArray>");
        var acls = new StringBuilder("<ACLArray>");
        foreach (var id in ids)
        {
            if (id == 10) ents.Append("""<EntACL EntId="10" ACLId="500" SysAclBeh="Combined"/>""");
            else if (id == 3000) ents.Append("""<EntACL EntId="3000" ACLId="600" SysAclBeh="Combined"><SysACLIds><long>700</long></SysACLIds></EntACL>""");
            else ents.Append($"""<EntACL EntId="{id}" ACLId="-1" SysAclBeh="Combined"/>""");
        }
        if (ids.Contains(10)) acls.Append("""<ACL Id="500"><EntryArray><ACE UserGrpId="61"><PermisArray><AccessPermis Id="1" Val="true"/><AccessPermis Id="2" Val="true"/></PermisArray></ACE><ACE UserGrpId="53"><PermisArray><AccessPermis Id="1" Val="false"/></PermisArray></ACE></EntryArray></ACL>""");
        if (ids.Contains(3000)) acls.Append("""<ACL Id="600"><EntryArray><ACE UserGrpId="62"><PermisArray><AccessPermis Id="1" Val="true"/></PermisArray></ACE><ACE UserGrpId="51"><PermisArray><AccessPermis Id="1" Val="true"/></PermisArray></ACE></EntryArray></ACL><ACL Id="700"><EntryArray><ACE UserGrpId="51"><PermisArray><AccessPermis Id="1" Val="true"/></PermisArray></ACE></EntryArray></ACL>""");
        return ents + "</EntACLArray>" + acls + "</ACLArray>";
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        var path = url.AbsolutePath.Replace("/AutodeskDM/Services/", "");
        var svc = Services.FirstOrDefault(s => s.Value.path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (svc.Key == null) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (request.Method == HttpMethod.Get)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Wsdl(svc.Key), Encoding.UTF8, "text/xml") };
        var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var body = xml.Root!.Elements().First(e => e.Name.LocalName == "Body").Elements().First();
        var op = body.Name.LocalName;
        Calls.Add(op);
        var action = request.Headers.TryGetValues("SOAPAction", out var a) ? a.First().Trim('"') : "";
        if (action != svc.Value.ns + op) return Fault("bad SOAPAction " + action, null);
        if (body.Name.NamespaceName != svc.Value.ns) return Fault("wrong namespace", null);
        bool signIn = op.StartsWith("SignIn");
        var sec = xml.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "Header")?.Elements().FirstOrDefault(e => e.Name.LocalName == "SecurityHeader");
        if (!signIn && sec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Ticket")?.Value != "T-1") return Fault("Bad ticket", 300);
        if (signIn && body.Elements().FirstOrDefault(e => e.Name.LocalName == "userPassword")?.Value != "pw") return Fault("Bad user name or password", 1702);
        var header = signIn ? """<s:Header><SecurityHeader xmlns="http://AutodeskDM/Services"><Ticket>T-1</Ticket><UserId>51</UserId></SecurityHeader></s:Header>""" : "";
        if (op == "DownloadFilePart") return Download(body);
        var inner = Reply(op, body);
        string result;
        if (op is "GetFolderRoot" or "GetLatestFileByMasterId" or "GetFileById")
        {
            // A single object: its fields sit on the result element itself, as SOAP serialises them.
            var one = XElement.Parse(inner);
            one.Name = XName.Get(op + "Result", svc.Value.ns);
            result = one.ToString(SaveOptions.DisableFormatting);
        }
        else result = $"<{op}Result>{inner}</{op}Result>";
        var envelope = $"""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">{header}<s:Body><{op}Response xmlns="{svc.Value.ns}">{result}</{op}Response></s:Body></s:Envelope>""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") };
    }

    HttpResponseMessage Download(XElement body)
    {
        long V(string n) => long.Parse(body.Elements().First(e => e.Name.LocalName == n).Value);
        var ticket = body.Descendants().First(e => e.Name.LocalName == "Bytes").Value;
        if (!Encoding.ASCII.GetString(Convert.FromBase64String(ticket)).StartsWith("ticket-")) return Fault("bad ticket", null);
        long first = V("firstByte"), last = Math.Min(V("lastByte"), Content.Length - 1);
        var part = Content[(int)first..(int)(last + 1)];
        var hdr = $"""<s:Header><FileTransferHeader xmlns="http://AutodeskDM/Filestore"><Compression>None</Compression><UncompressedSize>{part.Length}</UncompressedSize><IsComplete>{(last + 1 >= Content.Length ? "true" : "false")}</IsComplete></FileTransferHeader></s:Header>""";
        if (!Mtom)
        {
            var env = $"""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">{hdr}<s:Body><DownloadFilePartResponse xmlns="x"><DownloadFilePartResult>{Convert.ToBase64String(part)}</DownloadFilePartResult></DownloadFilePartResponse></s:Body></s:Envelope>""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(env, Encoding.UTF8, "text/xml") };
        }
        var root = $"""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">{hdr}<s:Body><DownloadFilePartResponse xmlns="x"><DownloadFilePartResult><xop:Include href="cid:part1%40silo" xmlns:xop="http://www.w3.org/2004/08/xop/include"/></DownloadFilePartResult></DownloadFilePartResponse></s:Body></s:Envelope>""";
        var ms = new MemoryStream();
        void A(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        A("--uuid:b\r\nContent-ID: <root@silo>\r\nContent-Type: application/xop+xml;charset=utf-8;type=\"text/xml\"\r\n\r\n" + root + "\r\n");
        A("--uuid:b\r\nContent-ID: <part1@silo>\r\nContent-Transfer-Encoding: binary\r\nContent-Type: application/octet-stream\r\n\r\n");
        ms.Write(part); A("\r\n--uuid:b--\r\n");
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/related; type=\"application/xop+xml\"; boundary=\"uuid:b\"; start=\"<root@silo>\"; start-info=\"text/xml\"");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    static HttpResponseMessage Fault(string msg, int? code) => new(HttpStatusCode.InternalServerError)
    {
        Content = new StringContent($"""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Server</faultcode><faultstring>{msg}</faultstring><detail>{(code != null ? $"<sl:sldetail xmlns:sl=\"http://AutodeskDM/Services\"><sl:errorcode>{code}</sl:errorcode></sl:sldetail>" : "")}</detail></s:Fault></s:Body></s:Envelope>""", Encoding.UTF8, "text/xml"),
    };
}
