using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Silo.Plugins.Vault;

/// <summary>A SOAP fault from Vault, with Vault's own error code when it sent one (detail/sldetail/errorcode).</summary>
public sealed class VaultFault : Exception
{
    public int? Code { get; }
    public string Operation { get; }
    public VaultFault(string op, string message, int? code) : base(code != null ? $"{op}: Vault error {code} ({VaultErrors.Describe(code.Value)}): {message}" : $"{op}: {message}")
    { Operation = op; Code = code; }
}

/// <summary>The few Vault error codes the plugin reacts to or explains.</summary>
public static class VaultErrors
{
    public const int BadTicket = 300, SessionExpired = 301;
    public static string Describe(int code) => code switch
    {
        300 => "sign-in ticket not valid",
        301 => "session expired",
        302 => "user is not allowed",
        303 => "access denied",
        1001 => "folder not found",
        1013 => "file not found",
        1700 => "no vault by that name",
        1701 => "user is not allowed into this vault",
        _ => "see the Vault SDK error code list",
    };
}

/// <summary>Values that a SOAP call takes as a complex type (a record with child elements).</summary>
public sealed record SoapRecord(IReadOnlyList<(string Name, object? Value)> Fields);

/// <summary>What came back from one call: the operation's result element, the SOAP header and any MTOM parts.</summary>
public sealed class SoapReply
{
    public required XElement Body { get; init; }
    public XElement? Header { get; init; }
    public Dictionary<string, byte[]> Parts { get; init; } = new();

    /// <summary>The "{Op}Result" element, or the response element itself when the operation returns nothing.</summary>
    public XElement? Result => Body.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith("Result", StringComparison.Ordinal)) ?? Body.Elements().FirstOrDefault();
}

/// <summary>
/// One Vault web service (DocumentService, SecurityService...), described by its own WSDL, read once at start-up.
/// Every Vault release moves its services to a new URL and namespace; reading the description means one build of
/// the plugin talks to any of them, and nothing of Autodesk's is shipped.
/// </summary>
public sealed class SoapService
{
    static readonly XNamespace Wsdl = "http://schemas.xmlsoap.org/wsdl/", Xs = "http://www.w3.org/2001/XMLSchema",
        Soap11 = "http://schemas.xmlsoap.org/wsdl/soap/", Soap12Ns = "http://schemas.xmlsoap.org/wsdl/soap12/",
        Env11 = "http://schemas.xmlsoap.org/soap/envelope/", Env12 = "http://www.w3.org/2003/05/soap-envelope",
        Xop = "http://www.w3.org/2004/08/xop/include";

    public string Name { get; }
    public Uri Url { get; }
    public bool Soap12 { get; private set; }
    readonly Dictionary<string, Op> _ops = new(StringComparer.Ordinal);
    readonly List<(XElement schema, string tns, bool qualified)> _schemas = new();

    sealed class Op
    {
        public string Name = "", Action = "", Ns = "";
        public bool Qualified = true;
        public List<Param> Params = new();
    }

    sealed class Param
    {
        public string Name = "";
        public string? ItemName;      // array item element name (ArrayOfLong -> "long")
        public string? ItemNs;
    }

    public IEnumerable<string> Operations => _ops.Keys;
    public bool Has(string op) => _ops.ContainsKey(op);

    SoapService(string name, Uri url) { Name = name; Url = url; }

    /// <summary>Reads a service's WSDL (one document, or the WCF ?wsdl + ?xsd imports).</summary>
    public static async Task<SoapService> LoadAsync(HttpClient http, string name, Uri url, CancellationToken ct)
    {
        var svc = new SoapService(name, url);
        XDocument? wsdl = null;
        foreach (var q in new[] { "?singleWsdl", "?wsdl" })
        {
            try
            {
                using var r = await http.GetAsync(url + q, ct);
                if (!r.IsSuccessStatusCode) continue;
                var text = await r.Content.ReadAsStringAsync(ct);
                if (!text.Contains("definitions", StringComparison.Ordinal)) continue;
                wsdl = Parse(text);
                break;
            }
            catch (HttpRequestException) { }
        }
        if (wsdl == null) throw new InvalidOperationException($"{name}: no service description at {url}");
        await svc.Describe(http, wsdl, url, ct, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return svc;
    }

    /// <summary>For tests and the probe: build from WSDL text already in hand.</summary>
    public static SoapService FromWsdl(string name, Uri url, string wsdlText)
    {
        var svc = new SoapService(name, url);
        svc.Describe(null, Parse(wsdlText), url, CancellationToken.None, new()).GetAwaiter().GetResult();
        return svc;
    }

    static XDocument Parse(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 };
        using var sr = new StringReader(text);
        using var xr = XmlReader.Create(sr, settings);
        return XDocument.Load(xr);
    }

    readonly List<XElement> _defs = new();

    async Task Describe(HttpClient? http, XDocument wsdl, Uri baseUrl, CancellationToken ct, HashSet<string> seen)
    {
        await Collect(http, wsdl, baseUrl, ct, seen);
        Build();
    }

    async Task Collect(HttpClient? http, XDocument wsdl, Uri baseUrl, CancellationToken ct, HashSet<string> seen)
    {
        var defs = wsdl.Root!;
        _defs.Add(defs);
        // Imported pieces (WCF splits the description over ?wsdl=wsdl0 and ?xsd=xsd0...).
        if (http != null)
        {
            foreach (var imp in defs.Elements(Wsdl + "import").Select(e => (string?)e.Attribute("location")).Where(l => l != null))
            {
                var u = new Uri(baseUrl, imp!);
                if (!SameServer(u, baseUrl) || !seen.Add(u.ToString())) continue;
                await Collect(http, Parse(await http.GetStringAsync(u, ct)), u, ct, seen);
            }
        }
        foreach (var schema in defs.Descendants(Xs + "schema").ToList()) await AddSchema(http, schema, baseUrl, ct, seen);
    }

    void Build()
    {
        // Bindings give the SOAP action and version; the port type and messages give the request element.
        var messages = _defs.SelectMany(d => d.Elements(Wsdl + "message")).GroupBy(m => (string)m.Attribute("name")!).ToDictionary(g => g.Key, g => g.First());
        var portTypes = _defs.SelectMany(d => d.Elements(Wsdl + "portType")).ToList();
        var bindings = _defs.SelectMany(d => d.Elements(Wsdl + "binding")).ToList();
        var binding = bindings.FirstOrDefault(b => b.Element(Soap11 + "binding") != null) ?? bindings.FirstOrDefault(b => b.Element(Soap12Ns + "binding") != null);
        if (binding == null) throw new InvalidOperationException($"{Name}: the service description has no SOAP binding");
        Soap12 = binding.Element(Soap11 + "binding") == null;
        var typeName = Local((string?)binding.Attribute("type"));
        var portType = portTypes.FirstOrDefault(p => (string?)p.Attribute("name") == typeName) ?? portTypes.FirstOrDefault();
        foreach (var bop in binding.Elements(Wsdl + "operation"))
        {
            var name = (string)bop.Attribute("name")!;
            var action = (string?)(bop.Element(Soap11 + "operation") ?? bop.Element(Soap12Ns + "operation"))?.Attribute("soapAction") ?? "";
            var op = new Op { Name = name, Action = action };
            var input = portType?.Elements(Wsdl + "operation").FirstOrDefault(o => (string?)o.Attribute("name") == name)?.Element(Wsdl + "input");
            var msgName = Local((string?)input?.Attribute("message"));
            XName? element = null;
            if (msgName != null && messages.TryGetValue(msgName, out var msg))
            {
                var part = msg.Elements(Wsdl + "part").FirstOrDefault(p => p.Attribute("element") != null);
                if (part != null) element = Resolve(part, (string)part.Attribute("element")!);
            }
            op.Ns = element?.NamespaceName ?? (action.EndsWith(name, StringComparison.Ordinal) ? action[..^name.Length] : "");
            FillParams(op, element?.LocalName ?? name);
            _ops[name] = op;
        }
    }

    static bool SameServer(Uri a, Uri b) => a.Scheme == b.Scheme && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    async Task AddSchema(HttpClient? http, XElement schema, Uri baseUrl, CancellationToken ct, HashSet<string> seen)
    {
        var tns = (string?)schema.Attribute("targetNamespace") ?? "";
        bool q = (string?)schema.Attribute("elementFormDefault") == "qualified";
        if (_schemas.Any(s => ReferenceEquals(s.schema, schema))) return;
        _schemas.Add((schema, tns, q));
        if (http == null) return;
        foreach (var imp in schema.Elements(Xs + "import").Concat(schema.Elements(Xs + "include")))
        {
            var loc = (string?)imp.Attribute("schemaLocation");
            if (loc == null) continue;
            var u = new Uri(baseUrl, loc);
            if (!SameServer(u, baseUrl) || !seen.Add(u.ToString())) continue;
            var doc = Parse(await http.GetStringAsync(u, ct));
            if (doc.Root != null) await AddSchema(http, doc.Root, u, ct, seen);
        }
    }

    static string? Local(string? qname) => qname == null ? null : qname.Contains(':') ? qname[(qname.IndexOf(':') + 1)..] : qname;

    static XName Resolve(XElement context, string qname)
    {
        var i = qname.IndexOf(':');
        if (i < 0) return XName.Get(qname, context.GetDefaultNamespace().NamespaceName);
        var ns = context.GetNamespaceOfPrefix(qname[..i]);
        return XName.Get(qname[(i + 1)..], ns?.NamespaceName ?? "");
    }

    void FillParams(Op op, string elementName)
    {
        var (el, tns, qualified) = FindGlobal("element", elementName, op.Ns);
        if (el == null) return;
        op.Qualified = qualified;
        if (!string.IsNullOrEmpty(tns)) op.Ns = tns;
        var ct = el.Element(Xs + "complexType");
        if (ct == null && el.Attribute("type") is { } t) ct = FindGlobal("complexType", Resolve(el, t.Value).LocalName, null).el;
        foreach (var p in ct?.Descendants(Xs + "element") ?? Enumerable.Empty<XElement>())
        {
            var prm = new Param { Name = (string?)p.Attribute("name") ?? "" };
            if (prm.Name.Length == 0) continue;
            if (p.Attribute("type") is { } pt)
            {
                var tn = Resolve(p, pt.Value);
                if (tn.NamespaceName != Xs.NamespaceName)
                {
                    var (arr, atns, aq) = FindGlobal("complexType", tn.LocalName, tn.NamespaceName);
                    var items = arr?.Element(Xs + "sequence")?.Elements(Xs + "element").ToList();
                    if (items is { Count: 1 } && (string?)items[0].Attribute("maxOccurs") == "unbounded")
                    { prm.ItemName = (string?)items[0].Attribute("name"); prm.ItemNs = aq ? atns : ""; }
                }
            }
            op.Params.Add(prm);
        }
    }

    (XElement? el, string tns, bool qualified) FindGlobal(string kind, string name, string? ns)
    {
        foreach (var (schema, tns, q) in _schemas.OrderBy(s => ns != null && s.tns == ns ? 0 : 1))
        {
            var e = schema.Elements(Xs + kind).FirstOrDefault(x => (string?)x.Attribute("name") == name);
            if (e != null) return (e, tns, q);
        }
        return (null, "", true);
    }

    /// <summary>The parameter names of an operation, in order (for the probe).</summary>
    public IReadOnlyList<string> ParamsOf(string op) => _ops.TryGetValue(op, out var o) ? o.Params.Select(p => p.Name).ToList() : [];

    /// <summary>Builds the request envelope. Parameters not named in the call are left out (Vault treats them as null).</summary>
    public string Envelope(string op, IReadOnlyDictionary<string, object?> args, XElement? header)
    {
        if (!_ops.TryGetValue(op, out var o)) throw new InvalidOperationException($"{Name} has no operation {op} on this Vault version");
        XNamespace env = Soap12 ? Env12 : Env11;
        XNamespace ns = o.Ns;
        XNamespace pns = o.Qualified ? ns : XNamespace.None;
        var body = new XElement(ns + o.Name);
        var known = o.Params.Select(p => p.Name).ToHashSet();
        foreach (var unknown in args.Keys.Where(k => !known.Contains(k)))
            throw new InvalidOperationException($"{Name}.{op} has no parameter '{unknown}' on this Vault version (it takes {string.Join(", ", known)})");
        foreach (var p in o.Params)
        {
            if (!args.TryGetValue(p.Name, out var v) || v == null) continue;
            body.Add(Value(pns + p.Name, v, p.ItemName, p.ItemNs ?? pns.NamespaceName));
        }
        var e = new XElement(env + "Envelope", new XAttribute(XNamespace.Xmlns + "s", env.NamespaceName));
        if (header != null) e.Add(new XElement(env + "Header", header));
        e.Add(new XElement(env + "Body", body));
        return e.ToString(SaveOptions.DisableFormatting);
    }

    static XElement Value(XName name, object v, string? itemName, string itemNs)
    {
        switch (v)
        {
            case string s: return new XElement(name, s);
            case bool b: return new XElement(name, b ? "true" : "false");
            case DateTime d: return new XElement(name, d.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            case byte[] bytes: return new XElement(name, Convert.ToBase64String(bytes));
            case XElement x: return new XElement(name, x.Nodes());
            case SoapRecord r:
                var rec = new XElement(name);
                foreach (var (n, fv) in r.Fields) if (fv != null) rec.Add(Value(XName.Get(n, name.NamespaceName), fv, null, name.NamespaceName));
                return rec;
            case System.Collections.IEnumerable list:
                var arr = new XElement(name);
                var item = XName.Get(itemName ?? "long", itemNs);
                foreach (var i in list) if (i != null) arr.Add(Value(item, i, null, itemNs));
                return arr;
            case IFormattable f: return new XElement(name, f.ToString(null, CultureInfo.InvariantCulture));
            default: return new XElement(name, v.ToString());
        }
    }

    /// <summary>Calls an operation. Throws <see cref="VaultFault"/> for a SOAP fault.</summary>
    public async Task<SoapReply> CallAsync(HttpClient http, string op, IReadOnlyDictionary<string, object?> args, XElement? header, CancellationToken ct)
    {
        var xml = Envelope(op, args, header);
        using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(xml, Encoding.UTF8) };
        var action = _ops[op].Action;
        if (Soap12)
            req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"application/soap+xml; charset=utf-8; action=\"{action}\"");
        else
        {
            req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
            req.Headers.TryAddWithoutValidation("SOAPAction", "\"" + action + "\"");
        }
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var ctype = resp.Content.Headers.ContentType;
        return ReadReply(op, bytes, ctype?.MediaType, ctype?.Parameters.FirstOrDefault(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"'), resp.StatusCode);
    }

    /// <summary>Parses a response: plain SOAP, or MTOM (multipart/related with binary parts). Public for tests.</summary>
    public static SoapReply ReadReply(string op, byte[] bytes, string? mediaType, string? boundary, HttpStatusCode status)
    {
        var parts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[] root = bytes;
        if (mediaType != null && mediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) && boundary != null)
        {
            var all = Mime.Split(bytes, boundary);
            if (all.Count == 0) throw new VaultFault(op, "empty MTOM reply", null);
            root = all[0].body;
            foreach (var (id, body) in all.Skip(1)) if (id != null) parts[id] = body;
        }
        XDocument doc;
        try { doc = Parse(Encoding.UTF8.GetString(root)); }
        catch (XmlException) { throw new VaultFault(op, $"HTTP {(int)status}: reply is not SOAP ({Encoding.UTF8.GetString(root, 0, Math.Min(root.Length, 200))})", null); }
        var envelope = doc.Root!;
        var bodyEl = envelope.Elements().FirstOrDefault(e => e.Name.LocalName == "Body") ?? throw new VaultFault(op, "reply has no SOAP body", null);
        var first = bodyEl.Elements().FirstOrDefault() ?? new XElement("empty");
        if (first.Name.LocalName == "Fault")
        {
            var msg = first.Descendants().FirstOrDefault(e => e.Name.LocalName is "faultstring" or "Text")?.Value ?? "SOAP fault";
            var codeText = first.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("errorcode", StringComparison.OrdinalIgnoreCase))?.Value;
            throw new VaultFault(op, msg.Trim(), int.TryParse(codeText, out var c) ? c : null);
        }
        if ((int)status >= 400) throw new VaultFault(op, $"HTTP {(int)status}", null);
        // xop:Include points at a binary part: put the bytes back inline as base64 so callers see one shape.
        foreach (var inc in first.Descendants(Xop + "Include").ToList())
        {
            var href = ((string?)inc.Attribute("href") ?? "").Replace("cid:", "", StringComparison.OrdinalIgnoreCase);
            href = Uri.UnescapeDataString(href);
            if (parts.TryGetValue(href, out var data) || parts.TryGetValue("<" + href + ">", out data)) inc.ReplaceWith(new XText(Convert.ToBase64String(data)));
        }
        return new SoapReply { Body = first, Header = envelope.Elements().FirstOrDefault(e => e.Name.LocalName == "Header"), Parts = parts };
    }
}

/// <summary>Just enough multipart/related parsing for MTOM replies.</summary>
static class Mime
{
    public static List<(string? id, byte[] body)> Split(byte[] data, string boundary)
    {
        var result = new List<(string?, byte[])>();
        var delim = Encoding.ASCII.GetBytes("--" + boundary);
        int pos = IndexOf(data, delim, 0);
        while (pos >= 0)
        {
            int start = pos + delim.Length;
            if (start + 1 < data.Length && data[start] == '-' && data[start + 1] == '-') break;   // closing delimiter
            start = SkipLine(data, start);
            int next = IndexOf(data, delim, start);
            if (next < 0) next = data.Length;
            int headerEnd = IndexOf(data, "\r\n\r\n"u8.ToArray(), start);
            if (headerEnd < 0 || headerEnd > next) break;
            var headers = Encoding.ASCII.GetString(data, start, headerEnd - start);
            int bodyStart = headerEnd + 4, bodyEnd = next;
            if (bodyEnd >= 2 && data[bodyEnd - 2] == '\r' && data[bodyEnd - 1] == '\n') bodyEnd -= 2;
            string? id = null;
            foreach (var line in headers.Split("\r\n"))
                if (line.StartsWith("Content-ID:", StringComparison.OrdinalIgnoreCase)) id = line[11..].Trim().Trim('<', '>');
            result.Add((id, data[bodyStart..Math.Max(bodyStart, bodyEnd)]));
            pos = next < data.Length ? next : -1;
        }
        return result;
    }

    static int SkipLine(byte[] d, int i)
    {
        while (i < d.Length && d[i] != '\n') i++;
        return Math.Min(d.Length, i + 1);
    }

    static int IndexOf(byte[] hay, byte[] needle, int from)
    {
        var span = hay.AsSpan(from);
        int i = span.IndexOf(needle);
        return i < 0 ? -1 : from + i;
    }
}
