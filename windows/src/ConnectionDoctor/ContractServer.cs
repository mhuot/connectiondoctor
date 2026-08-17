using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;

namespace ConnectionDoctor;

/// <summary>
/// Serves the Connection Dashboard and the data behind it, the Windows twin of
/// TBDoctor's --serve:
///   GET /contract  → current state as a Connection Contract v1 envelope
///   GET /events    → recorded changes as v1 events JSONL
///   GET /*         → the dashboard bundle compiled into this exe
///
/// One process and one URL: the user downloads an exe and opens a browser.
/// Loopback by default. `--bind lan` exposes it on the local network — the data
/// is topology and power telemetry with no authentication, which is fine for a
/// home lab fleet and explicitly opt-in for anything else.
/// </summary>
internal static class ContractServer
{
    public const int DefaultPort = 8787;

    /// <summary>Set while a LAN-bound server is running: mutations are refused there.</summary>
    private static bool boundToLan;

    public static int Run(int port, bool lan, bool openBrowser = false)
    {
        boundToLan = lan;
        using var listener = new HttpListener();

        // "localhost" is reserved for unelevated processes by default; a
        // wildcard prefix is not, which is why LAN mode needs a urlacl.
        listener.Prefixes.Add(lan ? $"http://+:{port}/" : $"http://localhost:{port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException exception)
        {
            Console.Error.WriteLine($"ConnectionDoctor: cannot listen on port {port}: {exception.Message}");
            if (lan)
            {
                Console.Error.WriteLine(
                    "Binding every interface needs a one-time reservation from an elevated prompt:");
                Console.Error.WriteLine(
                    $"  netsh http add urlacl url=http://+:{port}/ user={Environment.UserName}");
            }

            return 1;
        }

        // Advertise a URL that the registered prefix actually answers: the
        // loopback prefix is "localhost", and HTTP.sys matches on the Host
        // header, so http://127.0.0.1 would 400 (issue #39). LAN mode binds
        // every interface, so any of the machine's addresses works.
        var address = $"http://{(lan ? "0.0.0.0" : "localhost")}:{port}";
        Console.WriteLine(EmbeddedUi.IsPresent
            ? $"ConnectionDoctor serving the dashboard on {address}"
            : $"ConnectionDoctor serving on {address}  (GET /contract, GET /events)");
        if (!EmbeddedUi.IsPresent)
        {
            Console.WriteLine("note: no dashboard bundle is embedded; run scripts/build-ui.ps1 and rebuild");
        }

        if (lan)
        {
            Console.WriteLine("note: LAN binding is unauthenticated read-only telemetry — opt-in by design");
        }

        if (openBrowser)
        {
            OpenBrowser($"http://localhost:{port}/");
        }

        var stopping = false;
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping = true;
            listener.Stop();
        };

        while (!stopping)
        {
            HttpListenerContext context;
            try
            {
                context = listener.GetContext();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            try
            {
                Respond(context);
            }
            catch (HttpListenerException)
            {
                // Client hung up mid-response; the next request is unaffected.
            }
            catch (IOException)
            {
            }
        }

        return 0;
    }

    /// <summary>
    /// Opens the dashboard, reusing a collector or `serve` that already holds
    /// the port instead of failing on a bind conflict.
    /// </summary>
    public static int OpenDashboard(int port)
    {
        if (IsAlreadyServing(port))
        {
            Console.WriteLine($"ConnectionDoctor is already serving on port {port}; opening the dashboard.");
            OpenBrowser($"http://localhost:{port}/");
            return 0;
        }

        return Run(port, lan: false, openBrowser: true);
    }

    private static bool IsAlreadyServing(int port)
    {
        try
        {
            // Probe the root, not /contract: a contract request enumerates every
            // device on the machine and would time out here long before it answered.
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = client.GetAsync($"http://localhost:{port}/").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Opens the dashboard without starting a server of our own.</summary>
    public static void OpenInBrowser(int port) => OpenBrowser($"http://localhost:{port}/");

    private static void OpenBrowser(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Console.WriteLine($"Open {url} in a browser.");
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine($"Open {url} in a browser.");
        }
    }

    private static void Respond(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;
        var isGet = string.Equals(method, "GET", StringComparison.Ordinal);
        var isMutation = path == "/baseline";

        Payload response;
        if (isMutation)
        {
            // The one state-changing route: loopback-only, same-origin, custom
            // header, and no CORS headers at all (docs/embedding.md
            // § Mutations). CORS gates reads, not writes: any page open in the
            // browser can POST to localhost, so the request itself must prove
            // it came from our own page.
            response = string.Equals(method, "OPTIONS", StringComparison.Ordinal)
                ? new Payload(204, "text/plain; charset=utf-8", [], null)
                : string.Equals(method, "POST", StringComparison.Ordinal)
                    ? Baseline(context)
                    : Text(405, "method not allowed\n");
        }
        else
        {
            response = !isGet
                ? Text(405, "method not allowed\n")
                : path switch
                {
                    "/contract" => Contract(),
                    "/events" => Events(),
                    _ => Ui(path)
                };
        }

        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength64 = response.Body.Length;

        // Product identity on every response: `ui` and the resident process
        // reuse a port only when they see it (docs/embedding.md).
        context.Response.AddHeader("Server", $"connectiondoctor/{ProductVersion}");
        if (!isMutation)
        {
            // The dashboard may also be served from a dev Vite origin — reads only.
            context.Response.AddHeader("Access-Control-Allow-Origin", "*");
        }

        if (response.CacheControl is not null)
        {
            context.Response.AddHeader("Cache-Control", response.CacheControl);
        }

        context.Response.OutputStream.Write(response.Body, 0, response.Body.Length);
        context.Response.Close();
    }

    private static string ProductVersion =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>
    /// POST /baseline — capture, or with ?replace=1 replace, the known-good
    /// baseline. Refused unless bound to loopback, exactly same-origin, and
    /// carrying X-ConnectionDoctor-Request. Replacement is a compare-and-swap:
    /// If-Match must be the quoted capture time the client was shown, checked
    /// against the file inside one cross-process lock (BaselineTransaction),
    /// so a stale tab or a concurrent `baseline save` cannot clobber it.
    /// See docs/embedding.md § Mutations.
    /// </summary>
    private static Payload Baseline(HttpListenerContext context)
    {
        if (boundToLan)
        {
            return Json(403, "{\"error\":\"read-only-binding\"}");
        }

        var requestUrl = context.Request.Url;
        if (requestUrl is null || !IsSameOrigin(context.Request.Headers["Origin"], requestUrl))
        {
            return Json(403, "{\"error\":\"cross-origin\"}");
        }

        if (context.Request.Headers["X-ConnectionDoctor-Request"] is not "1")
        {
            return Json(403, "{\"error\":\"missing-request-header\"}");
        }

        var replace = requestUrl.Query.Contains("replace=1", StringComparison.Ordinal);
        var expected = ParseIfMatch(context.Request.Headers["If-Match"]);
        var result = BaselineTransaction.Run(replace, expected, DeviceProbe.Capture, requireExpectedOnReplace: true);

        return result.Outcome switch
        {
            BaselineTransaction.Outcome.Captured => Json(201, BaselineBody(result, replaced: false)),
            BaselineTransaction.Outcome.Replaced => Json(200, BaselineBody(result, replaced: true)),
            BaselineTransaction.Outcome.Exists => Json(409, Current("exists", result.CurrentCapturedAt)),
            BaselineTransaction.Outcome.Stale => Json(409, Current("stale", result.CurrentCapturedAt)),
            BaselineTransaction.Outcome.Busy => Json(503, "{\"error\":\"busy\"}"),
            BaselineTransaction.Outcome.Unreadable => Json(500, "{\"error\":\"baseline-unreadable\"}"),
            _ => Json(500, "{\"error\":\"write-failed\"}")
        };
    }

    private static string BaselineBody(BaselineTransaction.Result result, bool replaced) =>
        "{\"baseline\":{\"capturedAt\":\"" + result.CapturedAt?.ToString("O") + "\",\"nodes\":" + result.Nodes + "},\"replaced\":" + (replaced ? "true" : "false") + "}";

    private static string Current(string error, DateTimeOffset? capturedAt) =>
        "{\"error\":\"" + error + "\",\"current\":{\"capturedAt\":\"" + capturedAt?.ToString("O") + "\"}}";

    /// <summary>
    /// Origin must equal the scheme and authority this request actually
    /// arrived on. http://localhost:8787 and http://127.0.0.1:8787 are
    /// different browser origins; accepting either would let a page served
    /// from one drive a mutation on the other.
    /// </summary>
    internal static bool IsSameOrigin(string? origin, Uri requestUrl) =>
        !string.IsNullOrEmpty(origin) &&
        string.Equals(origin, $"{requestUrl.Scheme}://{requestUrl.Authority}", StringComparison.Ordinal);

    /// <summary>
    /// If-Match is exactly one strong ETag: a quoted timestamp. Unquoted,
    /// weak (W/"…"), multiple values or `*` are rejected — a client that does
    /// not send what it was shown does not get to replace anything.
    /// </summary>
    internal static DateTimeOffset? ParseIfMatch(string? header)
    {
        if (header is null)
        {
            return null;
        }

        var value = header.Trim();
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' || value.AsSpan(1, value.Length - 2).Contains('"'))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value[1..^1], out var parsed) ? parsed : null;
    }

    private static Payload Json(int status, string body) =>
        new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body + "\n"), null);

    private static Payload Ui(string path)
    {
        var asset = EmbeddedUi.Find(path);
        if (asset is not null)
        {
            return new Payload(
                200,
                asset.ContentType,
                asset.Bytes,
                asset.Immutable ? "public, max-age=31536000, immutable" : "no-cache");
        }

        if (!EmbeddedUi.IsPresent && path == "/")
        {
            return Text(
                200,
                "ConnectionDoctor contract endpoint. GET /contract or GET /events\n" +
                "No dashboard bundle is embedded in this build; run scripts/build-ui.ps1 and rebuild.\n");
        }

        return Text(404, "not found\n");
    }

    private static Payload Contract()
    {
        try
        {
            return Text(
                200,
                ContractV1.Serialize(ContractV1.ToEnvelopeWithAnalysis(DeviceProbe.Capture())),
                "application/json");
        }
        catch (Win32Exception exception)
        {
            return Text(500, $"probe failed: {exception.Message}\n");
        }
    }

    private static Payload Events()
    {
        try
        {
            return Text(
                200,
                ContractV1.ToEventStream(BackgroundCollector.ReadEntries()),
                "application/x-ndjson");
        }
        catch (IOException exception)
        {
            return Text(500, $"events unavailable: {exception.Message}\n");
        }
    }

    private static Payload Text(int status, string body, string contentType = "text/plain; charset=utf-8") =>
        new(status, contentType, Encoding.UTF8.GetBytes(body), null);

    internal sealed record Payload(int Status, string ContentType, byte[] Body, string? CacheControl);
}
