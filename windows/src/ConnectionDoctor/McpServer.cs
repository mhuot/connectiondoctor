using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConnectionDoctor;

/// <summary>
/// One tool call's outcome: the JSON text to hand back, or an error message.
/// MCP conveys tool failures as a normal result with <c>isError</c>, so the
/// model can read the message and try something else.
/// </summary>
internal sealed record McpToolResult(bool IsError, string Text)
{
    public static McpToolResult Ok(string json) => new(false, json);
    public static McpToolResult Error(string message) => new(true, message);
}

/// <summary>What the protocol layer needs from the tools; swapped for a fake in tests.</summary>
internal interface IMcpToolHost
{
    McpToolResult Call(string tool, JsonElement? arguments);
}

/// <summary>
/// Model Context Protocol server over stdio — the Windows twin of TBDoctor's
/// <c>--mcp</c>, per docs/mcp.md. Newline-delimited JSON-RPC 2.0: stdout carries
/// protocol traffic and nothing else; every diagnostic goes to stderr, because
/// one stray Console.WriteLine corrupts the stream.
///
/// The tool list is served verbatim from docs/mcp-tools.json, embedded at build
/// time, so names, descriptions and input schemas cannot drift from macOS.
/// </summary>
internal sealed class McpServer
{
    public const string ServerName = "connectiondoctor";
    /// <summary>
    /// Protocol versions this server implements — the stdio surface here
    /// (initialize, tools/list, tools/call, ping) is identical across them.
    /// Newest first: when a client asks for something else we answer with the
    /// newest we support, per the MCP negotiation rule; we never echo an
    /// arbitrary client string as if we implemented it.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];
    public const string ProtocolVersion = "2024-11-05";
    private const string ToolsResourceName = "mcp-tools.json";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly TextWriter log;
    private readonly IMcpToolHost tools;

    public McpServer(TextReader input, TextWriter output, TextWriter log, IMcpToolHost tools)
    {
        this.input = input;
        this.output = output;
        this.log = log;
        this.tools = tools;
    }

    /// <summary>The <c>mcp</c> verb: serve on the process's stdio until EOF.</summary>
    public static int Run()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true, NewLine = "\n" };
        var server = new McpServer(Console.In, stdout, Console.Error, new DeviceToolHost());
        server.Serve();
        return 0;
    }

    /// <summary>The embedded docs/mcp-tools.json, exactly as shipped in the repo.</summary>
    public static string ToolsJson()
    {
        using var stream = typeof(McpServer).Assembly.GetManifestResourceStream(ToolsResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ToolsResourceName}' is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<string> ToolNames()
    {
        var document = JsonNode.Parse(ToolsJson())?.AsObject();
        var tools = document?["tools"]?.AsArray() ?? [];
        return tools.Select(tool => tool?["name"]?.GetValue<string>() ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToList();
    }

    public void Serve()
    {
        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException exception)
            {
                // JSON-RPC 2.0: a parse error is answered (id null), and the
                // server keeps serving — one bad line must not end the session.
                log.WriteLine($"ConnectionDoctor mcp: parse error: {exception.Message}");
                RespondError(null, -32700, "Parse error");
                continue;
            }

            if (request is not JsonObject message)
            {
                RespondError(null, -32600, "Invalid Request: expected a JSON object");
                continue;
            }

            // JSON-RPC 2.0 shape, checked before anything is dispatched:
            // "jsonrpc" must be exactly "2.0"; an id, if present, must be a
            // string, number or null; "method" must be a non-empty string.
            // A request carries an "id" key — even an explicit null id is a
            // request and gets an answer with id null. Only an *absent* id is
            // a notification, which must not be answered — but a malformed
            // notification is still an Invalid Request and is answered (id
            // null) rather than dropped, so a broken client hears about it.
            var isNotification = !message.ContainsKey("id");
            var id = message["id"]?.DeepClone();
            if (!IsValidId(id))
            {
                RespondError(null, -32600, "Invalid Request: id must be a string, number or null");
                continue;
            }

            if (message["jsonrpc"] is not JsonValue versionValue ||
                !versionValue.TryGetValue<string>(out var version) || version != "2.0")
            {
                RespondError(isNotification ? null : id, -32600, "Invalid Request: jsonrpc must be \"2.0\"");
                continue;
            }

            if (message["method"] is not JsonValue methodValue ||
                !methodValue.TryGetValue<string>(out var method) || method.Length == 0)
            {
                RespondError(isNotification ? null : id, -32600, "Invalid Request: method must be a non-empty string");
                continue;
            }

            if (isNotification)
            {
                continue;
            }

            Handle(id, method, message["params"] as JsonObject);
        }
    }

    private void Handle(JsonNode? id, string method, JsonObject? parameters)
    {
        switch (method)
        {
            case "initialize":
            {
                // Negotiate: the client's version if we implement it, else the
                // newest we do — never an arbitrary echo.
                var requested = parameters?["protocolVersion"] is JsonValue pv && pv.TryGetValue<string>(out var s) ? s : null;
                var negotiated = requested is not null && SupportedProtocolVersions.Contains(requested)
                    ? requested
                    : SupportedProtocolVersions[0];
                Respond(id, new JsonObject
                {
                    ["protocolVersion"] = negotiated,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ServerName,
                        ["version"] = VersionString()
                    }
                });
                break;
            }

            case "ping":
                Respond(id, new JsonObject());
                break;

            case "tools/list":
            {
                var tools = JsonNode.Parse(ToolsJson())?["tools"]?.DeepClone() ?? new JsonArray();
                Respond(id, new JsonObject { ["tools"] = tools });
                break;
            }

            case "tools/call":
            {
                var name = parameters?["name"]?.GetValue<string>() ?? string.Empty;
                JsonElement? arguments = null;
                if (parameters?["arguments"] is JsonNode argumentsNode)
                {
                    arguments = JsonSerializer.SerializeToElement(argumentsNode);
                }

                McpToolResult result;
                try
                {
                    result = tools.Call(name, arguments);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    log.WriteLine($"ConnectionDoctor mcp: {name} failed: {exception}");
                    result = McpToolResult.Error($"{name} failed: {exception.Message}");
                }

                var payload = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = result.Text
                    })
                };
                if (result.IsError)
                {
                    payload["isError"] = true;
                }

                Respond(id, payload);
                break;
            }

            default:
                RespondError(id, -32601, $"Unknown method: {method}");
                break;
        }
    }

    /// <summary>JSON-RPC ids are strings, numbers or null — not booleans, objects or arrays.</summary>
    private static bool IsValidId(JsonNode? id)
    {
        if (id is null)
        {
            return true;
        }

        if (id is not JsonValue value)
        {
            return false;
        }

        return value.TryGetValue<string>(out _) || value.TryGetValue<double>(out _);
    }

    private void Respond(JsonNode? id, JsonObject result)
    {
        Write(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        });
    }

    private void RespondError(JsonNode? id, int code, string message)
    {
        Write(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        });
    }

    private void Write(JsonObject message)
    {
        output.WriteLine(message.ToJsonString(WireOptions));
        output.Flush();
    }

    private static string VersionString() =>
        typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(McpServer).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}

/// <summary>
/// The tools over the real machine: probe, recorded history, baseline. Kept
/// thin — every result is a Contract v1 document built by ContractV1, so the
/// CLI's --json, the dashboard and these tools are three views of one model.
/// </summary>
internal sealed class DeviceToolHost : IMcpToolHost
{
    private const string NoRecordingNote =
        "The recorder has not run on this machine, so there is no history to analyse. " +
        "Run `ConnectionDoctor.exe install` to record continuously; live state is still available via connection_probe.";

    private const string DiffMatchingNote =
        "matched by instance id; vidPid+parent matching arrives with contract-conformance";

    /// <summary>
    /// What connection_diagnose can honestly say on Windows today. The tool
    /// metadata advertises recorded-history analysis (link drops, grouped
    /// loss, power correlation); until the history engine lands
    /// (contract-findings-incidents), a success-shaped report would be a lie —
    /// so the note says exactly what was and was not analysed, always.
    /// </summary>
    public static string DiagnoseNote(bool hasRecording, double hours) =>
        (hasRecording
            ? $"Recorded history exists on this machine but is not yet analysed on Windows: findings below are the live power state and the known-good baseline comparison only. Link drops, grouped device loss and power correlation over the last {hours:0.#} h are NOT evaluated yet (contract-findings-incidents); treat their absence as unknown, not clear."
            : NoRecordingNote + " Findings below are the live power state and the known-good baseline comparison only.");

    public McpToolResult Call(string tool, JsonElement? arguments) => tool switch
    {
        "connection_probe" => Probe(),
        "connection_diagnose" => Diagnose(NumberArgument(arguments, "hours") ?? 6),
        "connection_incidents" => Incidents(
            NumberArgument(arguments, "hours") ?? 24,
            (int)(NumberArgument(arguments, "limit") ?? 20)),
        "connection_diff" => Diff(StringArgument(arguments, "baseline")),
        "connection_diagram" => McpToolResult.Error(
            "The topology diagram is not yet available on Windows: the shared Excalidraw export " +
            "(dashboard/src/domain) is being wired into both collectors by contract-conformance. " +
            "Until then, open the dashboard (`ConnectionDoctor.exe ui`) and use Export…, or call " +
            "connection_probe for the topology as a Connection Contract v1 envelope."),
        _ => McpToolResult.Error($"Unknown tool: {tool}")
    };

    private static McpToolResult Probe() =>
        McpToolResult.Ok(ContractV1.Serialize(ContractV1.ToEnvelope(DeviceProbe.Capture())));

    private static McpToolResult Diagnose(double hours)
    {
        var current = DeviceProbe.Capture();
        var findings = new List<Finding>(PowerDiagnosis.Analyze(current.Power));

        // Baseline comparison is the Windows analysis that already exists; the
        // ranked history engine arrives with contract-findings-incidents.
        var baselinePath = SnapshotStore.DefaultBaselinePath;
        if (File.Exists(baselinePath))
        {
            var baseline = SnapshotStore.Load(baselinePath);
            findings.AddRange(SnapshotComparer.Compare(baseline, current).Findings
                .Where(finding => !findings.Any(existing => existing.Title == finding.Title)));
        }

        var report = new ContractReport
        {
            Host = ContractV1.ToHost(current),
            GeneratedAt = DateTimeOffset.Now,
            WindowHours = hours,
            Findings = findings.OrderBy(Rank).Select(ContractV1.ToFinding).ToList(),
            Note = DiagnoseNote(File.Exists(BackgroundCollector.EventsPath), hours)
        };
        return McpToolResult.Ok(ContractV1.SerializeDocument(report));
    }

    private static McpToolResult Incidents(double hours, int limit)
    {
        var current = DeviceProbe.Capture();
        var hasRecording = File.Exists(BackgroundCollector.EventsPath);
        var cutoff = DateTimeOffset.Now.AddHours(-hours);
        var entries = hasRecording ? BackgroundCollector.ReadEntries() : [];
        var incidents = hasRecording
            ? IncidentStitcher.Stitch(entries)
                .Where(incident => incident.End >= cutoff)
                .OrderByDescending(incident => incident.Start)
                .Take(Math.Max(0, limit))
                .Select(incident => ContractV1.ToIncident(incident, entries))
                .ToList()
            : [];

        var report = new ContractReport
        {
            Host = ContractV1.ToHost(current),
            GeneratedAt = DateTimeOffset.Now,
            WindowHours = hours,
            Incidents = incidents,
            Note = hasRecording ? null : NoRecordingNote
        };
        return McpToolResult.Ok(ContractV1.SerializeDocument(report));
    }

    private static McpToolResult Diff(string? baselinePath)
    {
        var path = baselinePath ?? SnapshotStore.DefaultBaselinePath;
        if (!File.Exists(path))
        {
            return McpToolResult.Error(
                $"No baseline at {Path.GetFullPath(path)}. Save one while the setup works: `ConnectionDoctor.exe baseline save`.");
        }

        var baseline = SnapshotStore.Load(path);
        var current = DeviceProbe.Capture();
        return McpToolResult.Ok(ContractV1.SerializeDocument(BuildDiff(baseline, current)));
    }

    /// <summary>Pure: a diff document from two snapshots (also used by tests and, later, `diff --json`).</summary>
    public static ContractDiff BuildDiff(ConnectionSnapshot baseline, ConnectionSnapshot current)
    {
        var report = SnapshotComparer.Compare(baseline, current);
        return new ContractDiff
        {
            Host = ContractV1.ToHost(current),
            CapturedAt = current.CapturedAt,
            BaselineCapturedAt = baseline.CapturedAt,
            Findings = report.Findings.OrderBy(Rank).Select(ContractV1.ToFinding).ToList(),
            Missing = ContractV1.ToNodes(baseline, report.Missing),
            Added = ContractV1.ToNodes(current, report.Added),
            Note = DiffMatchingNote
        };
    }

    private static int Rank(Finding finding) => finding.Severity switch
    {
        "critical" => 0,
        "warning" => 1,
        _ => 2
    };

    private static double? NumberArgument(JsonElement? arguments, string name)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? StringArgument(JsonElement? arguments, string name)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
