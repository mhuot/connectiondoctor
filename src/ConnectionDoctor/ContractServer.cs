using System.ComponentModel;
using System.Net;
using System.Text;

namespace ConnectionDoctor;

/// <summary>
/// Read-only HTTP endpoint for the Connection Dashboard, the Windows twin of
/// TBDoctor's --serve:
///   GET /contract  → current state as a Connection Contract v1 envelope
///   GET /events    → recorded changes as v1 events JSONL
///
/// Loopback by default. `--bind lan` exposes it on the local network — the data
/// is topology and power telemetry with no authentication, which is fine for a
/// home lab fleet and explicitly opt-in for anything else.
/// </summary>
internal static class ContractServer
{
    public const int DefaultPort = 8787;

    public static int Run(int port, bool lan)
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

        Console.WriteLine(
            $"ConnectionDoctor serving on {(lan ? "0.0.0.0" : "127.0.0.1")}:{port}  (GET /contract, GET /events)");
        if (lan)
        {
            Console.WriteLine("note: LAN binding is unauthenticated read-only telemetry — opt-in by design");
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

    private static void Respond(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var isGet = string.Equals(context.Request.HttpMethod, "GET", StringComparison.Ordinal);
        var response = (isGet, path) switch
        {
            (true, "/contract") => Contract(),
            (true, "/events") => Events(),
            (true, "/") => new Payload(
                200,
                "text/plain",
                "ConnectionDoctor contract endpoint. GET /contract or GET /events\n"),
            _ => new Payload(404, "text/plain", "not found\n")
        };

        var bytes = Encoding.UTF8.GetBytes(response.Body);
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength64 = bytes.Length;

        // The dashboard is a browser app served from a different origin.
        context.Response.AddHeader("Access-Control-Allow-Origin", "*");
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static Payload Contract()
    {
        try
        {
            return new Payload(
                200,
                "application/json",
                ContractV1.Serialize(ContractV1.ToEnvelope(DeviceProbe.Capture())));
        }
        catch (Win32Exception exception)
        {
            return new Payload(500, "text/plain", $"probe failed: {exception.Message}\n");
        }
    }

    private static Payload Events()
    {
        try
        {
            return new Payload(
                200,
                "application/x-ndjson",
                ContractV1.ToEventStream(BackgroundCollector.ReadEntries()));
        }
        catch (IOException exception)
        {
            return new Payload(500, "text/plain", $"events unavailable: {exception.Message}\n");
        }
    }

    private sealed record Payload(int Status, string ContentType, string Body);
}
