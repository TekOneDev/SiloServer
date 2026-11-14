using System.Security.Cryptography.X509Certificates;

namespace Silo.Server.Core;

/// <summary>
/// HTTPS for the server (0.1.5). Listen addresses that start with "https://" use a certificate from the computer's
/// store (LocalMachine\My), picked by thumbprint or subject (settings.certificate), or the newest valid one named
/// "Silo Server" that the installer made. A .pfx path also works (for testing). Without a usable certificate the
/// HTTPS addresses are dropped and the server stays on this computer only, so the admin page can still be reached
/// to fix it, and nothing is served over the network unencrypted by surprise.
/// </summary>
public static class Https
{
    public const string FriendlyName = "Silo Server";

    public sealed record Plan(List<string> Listen, X509Certificate2? Certificate, string Status, int? HttpsPort);

    public static bool IsHttps(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static Plan Prepare(ServerSettings s, Log log)
    {
        var listen = s.Listen.ToList();
        if (!listen.Any(IsHttps)) return new Plan(listen, null, "off", null);
        var cert = Find(s.Certificate, out var why);
        if (cert == null)
        {
            log.Error($"HTTPS is set up in settings.json but no certificate could be used ({why}). Silo Server is listening on this computer only " +
                      "until this is fixed: run the installer with -Https, or set \"certificate\" to the thumbprint of a certificate in the computer's store.");
            return new Plan(["http://localhost:" + PortOf(listen.FirstOrDefault(u => !IsHttps(u)) ?? "http://localhost:47820", 47820)], null, "off: " + why, null);
        }
        var days = (cert.NotAfter - DateTime.Now).TotalDays;
        if (days < 30) log.Warn($"The HTTPS certificate {cert.Subject} expires on {cert.NotAfter:d}. Renew it (run the installer with -Https again for the self-made one).");
        var port = PortOf(listen.First(IsHttps), 443);
        var status = $"on, port {port}, certificate {Short(cert.Subject)} until {cert.NotAfter:yyyy-MM-dd}";
        log.Info("HTTPS " + status);
        return new Plan(listen, cert, status, port);
    }

    static string Short(string subject) => subject.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? subject.Split(',')[0][3..] : subject;

    public static int PortOf(string url, int dflt)
    {
        var u = url.Replace("://*", "://localhost").Replace("://+", "://localhost").Replace("://0.0.0.0", "://localhost");
        return Uri.TryCreate(u, UriKind.Absolute, out var uri) && !uri.IsDefaultPort ? uri.Port : (Uri.TryCreate(u, UriKind.Absolute, out uri) ? uri.Port : dflt);
    }

    /// <summary>Finds the certificate: a .pfx path, a thumbprint, a subject, or (empty) the newest valid "Silo Server" one.</summary>
    public static X509Certificate2? Find(string? spec, out string why)
    {
        why = "";
        spec = spec?.Trim();
        try
        {
            if (!string.IsNullOrEmpty(spec) && spec.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(spec)) { why = $"{spec} not found"; return null; }
                var c = X509CertificateLoader.LoadPkcs12FromFile(spec, null);
                return Usable(c, out why) ? c : null;
            }
            if (!OperatingSystem.IsWindows()) { why = "a certificate store is only available on Windows; give a .pfx path"; return null; }
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            IEnumerable<X509Certificate2> all = store.Certificates.Cast<X509Certificate2>();
            var thumb = spec?.Replace(" ", "").Replace(":", "");
            IEnumerable<X509Certificate2> pick =
                string.IsNullOrEmpty(spec) ? all.Where(c => c.FriendlyName == FriendlyName)
                : thumb!.Length == 40 && thumb.All(Uri.IsHexDigit) ? all.Where(c => string.Equals(c.Thumbprint, thumb, StringComparison.OrdinalIgnoreCase))
                : all.Where(c => c.Subject.Contains(spec!, StringComparison.OrdinalIgnoreCase) || c.FriendlyName == spec);
            var reasons = new List<string>();
            foreach (var c in pick.OrderByDescending(c => c.NotAfter))
            {
                if (Usable(c, out var r)) return c;
                reasons.Add(r);
            }
            why = reasons.Count > 0 ? string.Join("; ", reasons.Distinct()) : string.IsNullOrEmpty(spec) ? $"no certificate named \"{FriendlyName}\" in the computer's store" : $"no certificate matching \"{spec}\" in the computer's store";
            return null;
        }
        catch (Exception e) { why = e.Message; return null; }
    }

    static bool Usable(X509Certificate2 c, out string why)
    {
        why = "";
        if (DateTime.Now > c.NotAfter) { why = $"{c.Subject} expired on {c.NotAfter:d}"; return false; }
        if (DateTime.Now < c.NotBefore) { why = $"{c.Subject} is not valid until {c.NotBefore:d}"; return false; }
        if (!c.HasPrivateKey) { why = $"{c.Subject} has no private key"; return false; }
        try
        {
            // Opening the key proves the service account may use it (the installer grants that).
            using var k = (IDisposable?)c.GetRSAPrivateKey() ?? c.GetECDsaPrivateKey();
            if (k == null) { why = $"{c.Subject}: unsupported key type"; return false; }
        }
        catch (Exception e) { why = $"the service account cannot use the private key of {c.Subject} ({e.Message})"; return false; }
        return true;
    }

    /// <summary>
    /// Where to send a plain-HTTP request from another computer, or null to serve it as it is. Requests on this
    /// computer stay on HTTP so local tools and the installer's health check keep working.
    /// </summary>
    public static string? RedirectTarget(HttpRequest req, bool remoteIsLocal, int? httpsPort)
    {
        if (httpsPort == null || req.IsHttps || remoteIsLocal) return null;
        var host = req.Host.Host;
        if (string.IsNullOrEmpty(host)) return null;
        var authority = host.Contains(':') ? $"[{host}]" : host;
        if (httpsPort != 443) authority += ":" + httpsPort;
        return "https://" + authority + req.PathBase + req.Path + req.QueryString;
    }
}
