using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
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

    public static int Run(int port, bool lan, bool openBrowser = false)
    {
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

        var address = $"http://{(lan ? "0.0.0.0" : "127.0.0.1")}:{port}";
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
        var isGet = string.Equals(context.Request.HttpMethod, "GET", StringComparison.Ordinal);

        var response = !isGet
            ? Text(405, "method not allowed\n")
            : path switch
            {
                "/contract" => Contract(),
                "/events" => Events(),
                _ => Ui(path)
            };

        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength64 = response.Body.Length;

        // The dashboard may also be served from a dev Vite origin.
        context.Response.AddHeader("Access-Control-Allow-Origin", "*");
        if (response.CacheControl is not null)
        {
            context.Response.AddHeader("Cache-Control", response.CacheControl);
        }

        context.Response.OutputStream.Write(response.Body, 0, response.Body.Length);
        context.Response.Close();
    }

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
                ContractV1.Serialize(ContractV1.ToEnvelope(DeviceProbe.Capture())),
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

    private sealed record Payload(int Status, string ContentType, byte[] Body, string? CacheControl);
}
