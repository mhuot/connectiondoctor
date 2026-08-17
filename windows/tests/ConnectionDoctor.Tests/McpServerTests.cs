using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConnectionDoctor.Tests;

/// <summary>
/// The protocol layer over in-memory streams with a fake tool host, plus the
/// document builders over synthetic snapshots — nothing here touches hardware,
/// so it runs on any CI runner.
/// </summary>
public sealed class McpServerTests
{
    private static readonly string[] ExpectedTools =
    [
        "connection_probe",
        "connection_diagnose",
        "connection_incidents",
        "connection_diff",
        "connection_diagram"
    ];

    [Fact]
    public void EmbeddedToolMetadataMatchesRepositoryFile()
    {
        var repoFile = FindRepoFile(Path.Combine("docs", "mcp-tools.json"));
        Assert.True(repoFile is not null, "docs/mcp-tools.json not found above the test directory; run from a repo checkout.");

        Assert.Equal(File.ReadAllText(repoFile!), McpServer.ToolsJson());
    }

    [Fact]
    public void ToolsListServesTheFiveToolsVerbatim()
    {
        var responses = RoundTrip(
            new NullToolHost(),
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.Equal(2, responses.Count);

        var initialize = responses[0];
        Assert.Equal(1, initialize["id"]!.GetValue<int>());
        Assert.Equal(McpServer.ServerName, initialize["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("2024-11-05", initialize["result"]!["protocolVersion"]!.GetValue<string>());

        var list = responses[1];
        var names = list["result"]!["tools"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>())
            .ToArray();
        Assert.Equal(ExpectedTools, names);
        Assert.Equal(ExpectedTools, McpServer.ToolNames());

        // Verbatim: the served definitions are the embedded file's definitions.
        var embedded = JsonNode.Parse(McpServer.ToolsJson())!["tools"]!.ToJsonString();
        Assert.Equal(embedded, list["result"]!["tools"]!.ToJsonString());
    }

    [Fact]
    public void ToolCallWrapsResultInTextContentAndFlagsErrors()
    {
        var host = new FakeToolHost();
        var responses = RoundTrip(
            host,
            """{"jsonrpc":"2.0","id":"a","method":"tools/call","params":{"name":"connection_probe","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":"b","method":"tools/call","params":{"name":"connection_diagram","arguments":{"style":"flow"}}}""",
            """{"jsonrpc":"2.0","id":"c","method":"tools/call","params":{"name":"nope"}}""",
            """{"jsonrpc":"2.0","id":"d","method":"resources/list"}""",
            """{"jsonrpc":"2.0","id":"e","method":"ping"}""");

        Assert.Equal(5, responses.Count);

        var probe = responses[0]["result"]!;
        Assert.Null(probe["isError"]);
        var block = Assert.Single(probe["content"]!.AsArray());
        Assert.Equal("text", block!["type"]!.GetValue<string>());
        Assert.Equal("{\"schema\":\"connection-contract/v1\"}", block["text"]!.GetValue<string>());

        var diagram = responses[1]["result"]!;
        Assert.True(diagram["isError"]!.GetValue<bool>());
        Assert.Contains("not yet available", diagram["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("flow", host.Arguments["connection_diagram"]?.GetProperty("style").GetString());
        Assert.Null(host.Arguments["nope"]);

        var unknownTool = responses[2]["result"]!;
        Assert.True(unknownTool["isError"]!.GetValue<bool>());

        var unknownMethod = responses[3];
        Assert.Null(unknownMethod["result"]);
        Assert.Equal(-32601, unknownMethod["error"]!["code"]!.GetValue<int>());

        Assert.NotNull(responses[4]["result"]);
    }

    [Fact]
    public void ToolExceptionsBecomeErrorResultsNotCrashes()
    {
        var responses = RoundTrip(
            new ThrowingToolHost(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"connection_probe"}}""");

        var result = Assert.Single(responses)["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("boom", result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ServerIgnoresBlankAndUnparseableLinesAndNotifications()
    {
        var responses = RoundTrip(
            new NullToolHost(),
            "",
            "   ",
            "not json",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":9,"method":"ping"}""");

        var response = Assert.Single(responses);
        Assert.Equal(9, response["id"]!.GetValue<int>());
    }

    [Fact]
    public void DiffDocumentCarriesContractNodesAndInterimNote()
    {
        var hub = SnapshotComparerTests.Device(@"USB\VID_043E&PID_9C04\HUB", "USB", "Generic USB Hub");
        var mouse = SnapshotComparerTests.Device(@"HID\VID_046D&PID_C08A&MI_00\MOUSE", "Mouse", "HID-compliant mouse", hub.InstanceId);
        var camera = SnapshotComparerTests.Device(@"USB\VID_046D&PID_085E\CAM", "USB", "Camera");
        var baseline = SnapshotComparerTests.Snapshot(hub, mouse);
        var current = SnapshotComparerTests.Snapshot(hub, camera);

        var diff = DeviceToolHost.BuildDiff(baseline, current);
        var json = JsonNode.Parse(ContractV1.SerializeDocument(diff))!;

        Assert.Equal("connection-contract/v1", json["schema"]!.GetValue<string>());
        Assert.Equal("diff", json["kind"]!.GetValue<string>());
        Assert.Equal("windows", json["host"]!["os"]!.GetValue<string>());
        Assert.NotNull(json["baselineCapturedAt"]);
        Assert.Contains("instance id", json["note"]!.GetValue<string>(), StringComparison.Ordinal);

        var missing = Assert.Single(json["missing"]!.AsArray());
        Assert.Equal("usb:" + mouse.InstanceId, missing!["id"]!.GetValue<string>());
        Assert.Equal("usb:" + hub.InstanceId, missing["parentId"]!.GetValue<string>());
        Assert.Equal("046D:C08A", missing["vidPid"]!.GetValue<string>());

        var added = Assert.Single(json["added"]!.AsArray());
        Assert.Equal("Camera", added!["name"]!.GetValue<string>());
        Assert.Equal("device", added["kind"]!.GetValue<string>());
    }

    [Fact]
    public void FindingsSerializeWithStringSeverityAndNonEmptyEvidence()
    {
        var current = SnapshotComparerTests.Snapshot() with { Power = new PowerState(true, 80, -10_500) };
        var findings = PowerDiagnosis.Analyze(current.Power).Select(ContractV1.ToFinding).ToList();

        var report = new ContractReport
        {
            Host = ContractV1.ToHost(current),
            GeneratedAt = current.CapturedAt,
            WindowHours = 6,
            Findings = findings
        };
        var json = JsonNode.Parse(ContractV1.SerializeDocument(report))!;

        Assert.Equal("report", json["kind"]!.GetValue<string>());
        Assert.Null(json["incidents"]);
        var finding = Assert.Single(json["findings"]!.AsArray());
        Assert.Equal("warning", finding!["severity"]!.GetValue<string>());
        Assert.NotEmpty(finding["evidence"]!.AsArray());
        Assert.Contains("10.5 W", finding["evidence"]![0]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void IncidentDocumentNamesLostDevicesAndSharedParent()
    {
        var start = DateTimeOffset.Parse("2026-08-13T16:00:00-05:00");
        var hub = SnapshotComparerTests.Device(@"USB\VID_043E&PID_9C04\HUB", "USB", "Generic USB Hub");
        var keyboard = SnapshotComparerTests.Device(@"HID\VID_046D&PID_C08A&MI_01\KEYBOARD", "Keyboard", "HID Keyboard Device", hub.InstanceId);
        var mouse = SnapshotComparerTests.Device(@"HID\VID_046D&PID_C08A&MI_00\MOUSE", "Mouse", "HID-compliant mouse", hub.InstanceId);
        var power = new PowerState(true, 100, 0);
        var entries = new[]
        {
            new RecorderEntry(start, RecorderEntryKinds.DeviceDisappeared, keyboard, power, null),
            new RecorderEntry(start.AddSeconds(1), RecorderEntryKinds.DeviceDisappeared, mouse, power, null)
        };

        var incidents = IncidentStitcher.Stitch(entries).Select(ContractV1.ToIncident).ToList();
        var incident = Assert.Single(incidents);

        Assert.Equal(2, incident.DevicesLost.Count);
        Assert.Equal("046D:C08A", incident.DevicesLost[0].VidPid);
        Assert.Equal("usb:" + hub.InstanceId, incident.SharedParent);
        Assert.Null(incident.RootEvent);

        var json = JsonNode.Parse(ContractV1.SerializeDocument(new ContractReport
        {
            Host = ContractV1.ToHost(SnapshotComparerTests.Snapshot()),
            GeneratedAt = start,
            WindowHours = 24,
            Incidents = incidents,
            Note = "recorder has not run"
        }))!;
        Assert.Equal("report", json["kind"]!.GetValue<string>());
        Assert.Null(json["findings"]);
        Assert.Equal("recorder has not run", json["note"]!.GetValue<string>());
        Assert.Single(json["incidents"]!.AsArray());
    }

    private static List<JsonNode> RoundTrip(IMcpToolHost host, params string[] lines)
    {
        var input = new StringReader(string.Join('\n', lines) + "\n");
        var output = new StringWriter();
        var log = new StringWriter();
        new McpServer(input, output, log, host).Serve();

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!)
            .ToList();
    }

    private static string? FindRepoFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed class NullToolHost : IMcpToolHost
    {
        public McpToolResult Call(string tool, JsonElement? arguments) => McpToolResult.Error("not used");
    }

    private sealed class ThrowingToolHost : IMcpToolHost
    {
        public McpToolResult Call(string tool, JsonElement? arguments) => throw new InvalidOperationException("boom");
    }

    private sealed class FakeToolHost : IMcpToolHost
    {
        public Dictionary<string, JsonElement?> Arguments { get; } = new(StringComparer.Ordinal);

        public McpToolResult Call(string tool, JsonElement? arguments)
        {
            Arguments[tool] = arguments;
            return tool switch
            {
                "connection_probe" => McpToolResult.Ok("{\"schema\":\"connection-contract/v1\"}"),
                "connection_diagram" => McpToolResult.Error("The topology diagram is not yet available on Windows"),
                _ => McpToolResult.Error($"Unknown tool: {tool}")
            };
        }
    }
}
