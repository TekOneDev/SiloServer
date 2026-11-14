using Silo.Errors;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Silo.Server.Api;
using Silo.Server.Core;
using Silo.Server.Index;
using Silo.Server.Pipeline;

// Worker mode: OCR runs in child processes of this executable.
if (args.Length > 0 && args[0] == "ocr-worker") return OcrEngine.WorkerMain();
// Installer helper: write the default settings file (if there is none) and stop.
if (args.Length > 0 && args[0] == "init") { var s0 = new SettingsStore(); Console.WriteLine(Path.Combine(s0.Root, "settings.json")); return 0; }

// One copy at a time: Windows can start a new service process while the old one is still stopping.
using var single = new Mutex(false, OperatingSystem.IsWindows() ? @"Global\SiloServer" : "SiloServer");
bool owned;
try { owned = single.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { owned = true; }
if (!owned) { Fail.Write(Err.Srv.Svc.AnotherCopyRunning); return 1; }

SettingsStore settings;
// A start-up failure with a code of its own (settings.json unreadable with no backup) stops the service: exit code 3.
try { settings = new SettingsStore(); }
catch (SiloException e) { Fail.Write(e.Error, e.Args, e.InnerException); Log.Emergency(e.Error, e.Args, e.InnerException); return 3; }
var log = new Log(settings.DataDir);
Fail.Use(log);
if (settings.LoadFault is { } lf) log.Problem(lf.Def, lf.Args, lf.Cause);
log.Info($"Silo Server {typeof(Program).Assembly.GetName().Version?.ToString(3)} starting; data in {settings.DataDir}");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Host.UseWindowsService(o => o.ServiceName = "SiloServer");
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
var https = Https.Prepare(settings.Current, log);
builder.WebHost.UseUrls(https.Listen.ToArray());
if (https.Certificate != null)
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h =>
    {
        h.ServerCertificate = https.Certificate;
        h.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
    }));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});
bool windowsAuth = OperatingSystem.IsWindows() && settings.Current.Auth == "windows";
if (windowsAuth)
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
    builder.Services.AddAuthorization();
}
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

// Only SYSTEM and Administrators may write where the service keeps settings, plugins and the index (0.1.4).
try { DataFolder.Lock(settings, log); }
catch (SiloException e) { log.Problem(e.Error, e.Args, e.InnerException); return 3; }
// ...and the sandboxed plugins and OCR workers may not read it at all (0.1.5).
if (settings.Current.SandboxPlugins) DataFolder.ProtectFromPlugins(settings, log);
log.Info($"Running as {DataFolder.RunningAs()}; plugins {(settings.Current.SandboxPlugins ? "sandboxed" : "NOT sandboxed (sandboxPlugins is off)")}; HTTPS {https.Status}.");
if (DataFolder.IsLocalSystem())
    log.Problem(Err.Srv.Svc.RunsAsLocalSystem);
if (settings.Current.Auth == "anonymous" && settings.Current.Listen.Any(u => !ApiRoutes.IsLoopbackUrl(u)) &&
    settings.Current.Sources.Any(s => !string.Equals(s.Config["access"]?.GetValue<string>(), "public", StringComparison.OrdinalIgnoreCase)))
    log.Problem(Err.Srv.Svc.NetworkWithoutSignIn);

var catalog = new Catalog(Path.Combine(settings.DataDir, "catalog.db"));
var plugins = new PluginManager(settings, log);
var rules = new Rules(settings, log, catalog);
var index = new SearchIndex(Path.Combine(settings.DataDir, "index"));
if (index.Recovered != null) log.Problem(Err.Srv.Idx.IndexRebuiltFromEmpty, new { reason = index.Recovered });
var engine = new Engine(settings, catalog, plugins, rules, index, log);
var searcher = new Searcher(index, rules, () => engine.IndexBuilding)
{
    ExtraDims = plugins.DeclaredFields().Where(kv => kv.Value.spec.Facet).Select(kv => kv.Key).ToList(),
};
log.Info($"{plugins.All.Count} plugins: {string.Join(", ", plugins.All.Select(p => $"{p.Id} {p.Manifest.Version}{(p.Enabled ? "" : " (off)")}"))}");

builder.Services.AddSingleton(settings).AddSingleton(log).AddSingleton(catalog).AddSingleton(plugins).AddSingleton(rules)
    .AddSingleton(index).AddSingleton(searcher).AddSingleton(engine).AddSingleton(https).AddHostedService(sp => sp.GetRequiredService<Engine>());

var app = builder.Build();
// Outermost: whatever goes wrong below, an API client gets the coded error body.
app.Use(Fail.Middleware);
app.UseResponseCompression();
if (https.HttpsPort != null && settings.Current.HttpsRedirect)
    app.Use(async (http, next) =>
    {
        // Other computers are moved to HTTPS before anything else, sign-in included, so passwords and results never cross the network in the clear.
        var remote = http.Connection.RemoteIpAddress;
        var target = Https.RedirectTarget(http.Request, remote == null || System.Net.IPAddress.IsLoopback(remote), https.HttpsPort);
        if (target != null) { http.Response.Redirect(target, permanent: true, preserveMethod: true); return; }
        await next();
    });
app.Use(async (http, next) =>
{
    if (http.Request.IsHttps) http.Response.Headers["Strict-Transport-Security"] = "max-age=15552000";
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    // The pages run only Silo's own scripts; nothing in an indexed file can add script or call out (0.1.4).
    // The file endpoint sets its own, stricter policy.
    http.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob: https://*.basemaps.cartocdn.com https://server.arcgisonline.com; " +
        "connect-src 'self'; frame-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'";
    http.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";   // tile servers want to know the site
    if (http.Request.Path.StartsWithSegments("/api")) http.Response.Headers["Cache-Control"] = "no-store";
    await next();
});
if (windowsAuth) { app.UseAuthentication(); app.Use(async (http, next) => { if (http.User.Identity?.IsAuthenticated != true) { await http.ChallengeAsync(); return; } await next(); }); }
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers["Cache-Control"] = c.File.Name.EndsWith(".html") || c.File.Name.EndsWith(".js") || c.File.Name.EndsWith(".css") ? "no-cache" : "public, max-age=3600",
});
ApiRoutes.Map(app);
// An API address that does not exist is an error with a code, not the search page.
app.MapFallback("/api/{**rest}", () => Fail.Result(Err.Srv.Api.NoSuchEndpoint));
app.MapFallbackToFile("index.html");

app.Lifetime.ApplicationStopped.Register(() =>
{
    try { engine.Dispose(); } catch { }
    try { plugins.Dispose(); } catch { }
    try { index.Dispose(); } catch { }
    try { catalog.Dispose(); } catch { }
    log.Info("Stopped.");
});
try { app.Run(); }
catch (OperationCanceledException) { }
catch (IOException e) when (e.Message.Contains("address", StringComparison.OrdinalIgnoreCase)) { log.Problem(Err.Srv.Svc.PortInUse, new { detail = SafeText.Detail(e) }, e); return 2; }
return 0;
