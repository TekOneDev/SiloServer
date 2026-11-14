using System.Security.Cryptography;
using Silo.Errors;

namespace Silo.Server.Core;

/// <summary>
/// Errors from the Silo error registry (SiloCommon, Silo.Errors), as Silo Server reports them. What a person sees is
/// the registry's plain message and its code; the technical detail (the values behind the message, the exception)
/// goes to the server log in DataDir\logs, on one line that starts with the code.
/// </summary>
public static class Fail
{
    static Log? _log;
    public static void Use(Log log) => _log = log;

    /// <summary>A short reference shown with unexpected errors, so the message a user quotes finds its log line.</summary>
    public static string NewRef() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4));

    /// <summary>Writes the full detail of an error to the log (or to standard error before the log exists).</summary>
    public static void Write(ErrorDef def, object? args = null, Exception? e = null, string? traceId = null)
    {
        if (_log != null) _log.Problem(def, args, e, traceId);
        else Console.Error.WriteLine("Silo Server: " + SiloError.LogLine(def, args, e, traceId));
    }

    /// <summary>
    /// The standard error response: {"error":{"code","message","severity","kb","traceId"}} with the code's HTTP status.
    /// Server-side failures (5xx, or anything with an exception behind it) are logged with a reference.
    /// </summary>
    public static IResult Result(ErrorDef def, object? args = null, int? status = null, Exception? cause = null)
    {
        var code = status is >= 400 and < 600 ? status.Value : def.Http is >= 400 and < 600 ? def.Http : 500;
        string? trace = null;
        if (cause != null || code >= 500)
        {
            trace = NewRef();
            Write(def, args, cause, trace);
        }
        return Results.Json(new SiloError(def, args, trace).ToBody(), statusCode: code);
    }

    public static IResult Result(SiloException e) => Result(e.Error, e.Args, cause: e.InnerException);

    /// <summary>The catch-all: an error nobody expected. Full detail to the log; the user gets the code and a reference.</summary>
    public static IResult Unexpected(Exception e, string? where = null)
    {
        var trace = NewRef();
        Write(Err.Srv.Gen.Unexpected, new { @ref = trace, where }, e, trace);
        return Results.Json(new SiloError(Err.Srv.Gen.Unexpected, new { @ref = trace }, trace).ToBody(), statusCode: 500);
    }

    /// <summary>One line for a status field or a stored problem: "message (CODE)".</summary>
    public static string Text(ErrorDef def, object? args = null) => new SiloError(def, args).Display;

    /// <summary>
    /// Wraps the whole API: an exception that escapes an endpoint becomes a coded error instead of a bare HTTP 500, and
    /// an endpoint that answered with only a status code gets a coded body, so a client never has to show "Not Found".
    /// </summary>
    public static async Task Middleware(HttpContext http, Func<Task> next)
    {
        try { await next(); }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            if (!http.Response.HasStarted) http.Response.StatusCode = 499;
            return;
        }
        catch (Exception e) when (!http.Response.HasStarted)
        {
            var result = e switch
            {
                SiloException s => Result(s),
                BadHttpRequestException b => Result(Err.Srv.Api.BadRequest, status: b.StatusCode is >= 400 and < 500 ? b.StatusCode : 400, cause: b),
                _ => Unexpected(e, http.Request.Method + " " + http.Request.Path),
            };
            http.Response.Clear();
            await result.ExecuteAsync(http);
            return;
        }
        if (http.Response.HasStarted || http.Response.StatusCode < 400 || http.Response.StatusCode == 499) return;
        if (!http.Request.Path.StartsWithSegments("/api") || http.Response.ContentType != null || http.Response.ContentLength is > 0) return;
        // A Windows sign-in challenge must reach the browser untouched, or the browser never signs in.
        if (http.Response.StatusCode == 401 && http.Response.Headers.ContainsKey("WWW-Authenticate")) return;
        var def = http.Response.StatusCode switch
        {
            400 => Err.Srv.Api.BadRequest,
            401 => Err.Srv.Auth.SignInNeeded,
            403 => Err.Srv.Auth.NotAllowed,
            404 => http.GetEndpoint() == null ? Err.Srv.Api.NoSuchEndpoint : Err.Srv.Api.ItemNotFound,
            405 => Err.Srv.Api.NoSuchEndpoint,
            _ => Err.Srv.Api.ServerAnswered,
        };
        await Result(def, new { status = "HTTP " + http.Response.StatusCode }, http.Response.StatusCode).ExecuteAsync(http);
    }
}

/// <summary>
/// Which registry code a plugin failure is. Plugins are separate programs (and may come from other authors), so they
/// say what went wrong in words; this sorts those words, and the ways a plugin process itself can fail, into codes.
/// </summary>
public static class PluginFaults
{
    /// <summary>
    /// What is kept with a file whose text could not be read: "message (CODE)" and, after it, the reader's own short
    /// reason. A timeout and a crash have their own codes; a reader's reason is sorted by <see cref="ReadFaults"/>.
    /// </summary>
    public static string OfExtract(Exception e)
    {
        if (e is SiloException s) return ReadFaults.Stored(s.Error, s.Args, null);
        var (kind, message) = e is PluginError ? Split(e.Message) : (e.GetType().Name, e.Message);
        var def = ReadFaults.Of(kind, message);
        return ReadFaults.Stored(def, ReadFaults.ArgsFor(def, message), message);
    }

    /// <summary>Why a source could not be scanned.</summary>
    public static (ErrorDef Def, object? Args) OfScan(Exception e, string source)
    {
        if (e is SiloException s) return (s.Error, s.Args);
        var m = e is PluginError ? Split(e.Message).Message : e.Message;
        if (m.StartsWith("Folder not found or not reachable", StringComparison.OrdinalIgnoreCase)) return (Err.Srv.Src.SourceFolderUnreachable, new { source });
        if (m.StartsWith("config.root is required", StringComparison.OrdinalIgnoreCase)) return (Err.Srv.Src.SourceFolderNotSet, new { source });
        return (Err.Srv.Src.ScanFailed, new { source, detail = SafeText.Detail(e) });
    }

    /// <summary>A plugin reports a failure as "ExceptionType: message" (PluginRunner); this takes the two apart.</summary>
    public static (string? Kind, string Message) Split(string error)
    {
        var i = error.IndexOf(": ", StringComparison.Ordinal);
        return i > 0 && error[..i].All(char.IsLetterOrDigit) && error[..i].EndsWith("Exception", StringComparison.Ordinal) ? (error[..i], error[(i + 2)..]) : (null, error);
    }

    /// <summary>What is kept with a file when OCR gave an error instead of text.</summary>
    public static string? OfOcr(string? error) => string.IsNullOrEmpty(error) ? null
        : ErrorRegistry.CodeIn(error) != null ? error
        : ReadFaults.Stored(Err.Srv.Ocr.OcrFailed, new { detail = SafeText.Scrub(error) }, null);
}
