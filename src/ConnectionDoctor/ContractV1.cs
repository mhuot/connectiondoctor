using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace ConnectionDoctor;

/// <summary>
/// Connection Contract v1 — the one JSON shape TBDoctor (macOS) and
/// ConnectionDoctor (Windows) both emit, so a single dashboard can read every
/// machine's recording. Canonical spec: mhuot/tbdoctor docs/schema-v1.md.
/// Additive-only within v1; consumers tolerate unknown fields.
/// </summary>
internal static class ContractV1
{
    public const string SchemaId = "connection-contract/v1";
    public const string HostNodeId = "host";

    /// <summary>RecorderEntry kinds map 1:1 onto the contract's event kinds.</summary>
    public static readonly IReadOnlyDictionary<string, string> EventKinds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RecorderEntryKinds.Snapshot] = "fullSnapshot",
            [RecorderEntryKinds.DeviceAppeared] = "deviceAdded",
            [RecorderEntryKinds.DeviceDisappeared] = "deviceRemoved",
            [RecorderEntryKinds.DeficitStarted] = "deficitStart",
            [RecorderEntryKinds.DeficitEnded] = "deficitEnd",
            [RecorderEntryKinds.PowerChanged] = "adapterChanged"
        };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Instance IDs are full of "&"; this output is read by people pasting it
        // into tickets, not embedded in HTML, so leave it legible.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions IndentedOptions = new(CompactOptions)
    {
        WriteIndented = true
    };

    /// <summary>Current state as a v1 envelope.</summary>
    public static ContractEnvelope ToEnvelope(ConnectionSnapshot snapshot, bool includeBuiltIn = true)
    {
        var devices = DeviceFilters.TopologyDevices(snapshot, includeBuiltIn);
        var included = new HashSet<string>(
            devices.Select(device => device.InstanceId),
            StringComparer.OrdinalIgnoreCase);
        var byId = snapshot.Devices.ToDictionary(device => device.InstanceId, StringComparer.OrdinalIgnoreCase);

        // One root the whole tree hangs from, matching TBDoctor's envelope.
        var nodes = new List<ContractNode>(devices.Count + 1)
        {
            new()
            {
                Id = HostNodeId,
                Kind = "host",
                Name = snapshot.HostName,
                Protocol = "power"
            }
        };

        foreach (var device in devices)
        {
            var kind = KindOf(device);
            nodes.Add(new ContractNode
            {
                Id = NodeId(device),
                ParentId = ParentNodeId(device, byId, included),
                Kind = kind,
                Name = device.FriendlyName,
                VendorName = device.Manufacturer,
                VidPid = device.VidPid,
                Protocol = ProtocolOf(kind),
                UsbClass = device.UsbClass,
                Platform = new Dictionary<string, string> { ["instanceId"] = device.InstanceId }
            });
        }

        return new ContractEnvelope
        {
            CapturedAt = snapshot.CapturedAt,
            Host = new ContractHost
            {
                Name = snapshot.HostName,
                Arch = snapshot.OperatingSystemArchitecture.ToLowerInvariant()
            },
            Power = ToPower(snapshot.Power, nodes),

            // Native pixel sizes need QueryDisplayConfig (connectiondoctor#14).
            // "We do not know" is not the same claim as "none attached", and
            // displaysKnown exists precisely to keep those apart.
            DisplaysKnown = false,
            Nodes = nodes
        };
    }

    /// <summary>One recorded change as a v1 event, or null if it has no mapping.</summary>
    public static ContractEvent? ToEvent(RecorderEntry entry)
    {
        if (!EventKinds.TryGetValue(entry.Kind, out var kind))
        {
            return null;
        }

        return new ContractEvent
        {
            T = entry.At,
            Kind = kind,
            NodeId = entry.Device is null ? null : NodeId(entry.Device),
            VidPid = entry.Device?.VidPid,
            Name = entry.Device?.FriendlyName,
            Snapshot = entry.Snapshot is null ? null : ToEnvelope(entry.Snapshot)
        };
    }

    public static string ToEventStream(IEnumerable<RecorderEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            var mapped = ToEvent(entry);
            if (mapped is null)
            {
                continue;
            }

            builder.Append(JsonSerializer.Serialize(mapped, CompactOptions)).Append('\n');
        }

        return builder.ToString();
    }

    public static string Serialize(ContractEnvelope envelope, bool indented = true) =>
        JsonSerializer.Serialize(envelope, indented ? IndentedOptions : CompactOptions);

    /// <summary>Namespace-prefixed, and unique because InstanceId is unique.</summary>
    private static string NodeId(DeviceNode device) =>
        $"{Namespace(device)}:{device.InstanceId}";

    private static string Namespace(DeviceNode device) =>
        IsDisplay(device) ? "display" : IsThunderbolt(device) ? "tb" : "usb";

    /// <summary>
    /// Nearest emitted ancestor, so a branch never dangles off a devnode the
    /// export filtered out. Falls back to the host, because the contract builds
    /// hierarchy from parentId alone and an unresolvable one reads as an orphan.
    /// </summary>
    private static string ParentNodeId(
        DeviceNode device,
        IReadOnlyDictionary<string, DeviceNode> byId,
        IReadOnlySet<string> included)
    {
        var parentId = device.ParentInstanceId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (parentId is not null && visited.Add(parentId) && byId.TryGetValue(parentId, out var parent))
        {
            if (included.Contains(parent.InstanceId))
            {
                return NodeId(parent);
            }

            parentId = parent.ParentInstanceId;
        }

        return HostNodeId;
    }

    private static string KindOf(DeviceNode device)
    {
        if (IsDisplay(device))
        {
            return "display";
        }

        if (IsThunderbolt(device))
        {
            return "thunderbolt";
        }

        return device.UsbClass == DeviceNode.UsbHubClass ||
               device.FriendlyName.Contains("Hub", StringComparison.OrdinalIgnoreCase)
            ? "hub"
            : "device";
    }

    /// <summary>
    /// The link into a node. SetupAPI reports no negotiated speed, so USB links
    /// say "unknown" rather than guessing usb2 against usb3, and Tunneled stays
    /// false because nothing available here can prove a USB4 tunnel. Real link
    /// speeds need IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX.
    /// </summary>
    private static string ProtocolOf(string kind) => kind switch
    {
        "display" => "displayPort",
        "thunderbolt" => "thunderbolt",
        _ => "unknown"
    };

    private static ContractPower ToPower(PowerState power, IReadOnlyList<ContractNode> nodes)
    {
        // Judged by capacity, not by a battery device existing: desktops expose
        // a battery service with no charge, and Windows reports 255 for unknown
        // which PowerState has already normalized to -1.
        var batteryPresent = power.BatteryPercent is >= 0 and <= 100;

        // "dock" means the supply carries data on the same cable. The evidence
        // available on Windows is a USB4 or Thunderbolt router in the tree while
        // external power is connected.
        var docked = nodes.Any(node => node.Kind == "thunderbolt");

        var source = !batteryPresent
            ? "mains"
            : !power.LineOnline
                ? "battery"
                : docked
                    ? "dock"
                    : "adapter";

        return new ContractPower
        {
            Source = source,
            ExternalConnected = power.LineOnline,
            BatteryPresent = batteryPresent,
            BatteryPercent = batteryPresent ? power.BatteryPercent : null,
            BatteryRateMilliwatts = batteryPresent ? power.BatteryRateMilliwatts : null
        };
    }

    private static bool IsDisplay(DeviceNode device) =>
        device.ClassName.Equals("Monitor", StringComparison.OrdinalIgnoreCase);

    private static bool IsThunderbolt(DeviceNode device) =>
        device.InstanceId.StartsWith(@"USB4\", StringComparison.OrdinalIgnoreCase) ||
        device.FriendlyName.Contains("USB4", StringComparison.OrdinalIgnoreCase) ||
        device.FriendlyName.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase);
}

internal sealed record ContractEnvelope
{
    public string Schema { get; init; } = ContractV1.SchemaId;
    public required DateTimeOffset CapturedAt { get; init; }
    public required ContractHost Host { get; init; }
    public required ContractPower Power { get; init; }
    public IReadOnlyList<ContractDisplay>? Displays { get; init; }
    public bool DisplaysKnown { get; init; }
    public required IReadOnlyList<ContractNode> Nodes { get; init; }
}

internal sealed record ContractHost
{
    public required string Name { get; init; }
    public string Os => "windows";
    public required string Arch { get; init; }
    public string? Model { get; init; }
}

internal sealed record ContractPower
{
    public required string Source { get; init; }
    public required bool ExternalConnected { get; init; }
    public required bool BatteryPresent { get; init; }
    public int? BatteryPercent { get; init; }
    public int? BatteryRateMilliwatts { get; init; }
}

internal sealed record ContractNode
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string? VendorName { get; init; }
    public string? VidPid { get; init; }
    public required string Protocol { get; init; }
    public bool Tunneled { get; init; }
    public int? UsbClass { get; init; }
    public IReadOnlyDictionary<string, string>? Platform { get; init; }
}

internal sealed record ContractDisplay
{
    public required string Name { get; init; }
    public required int WidthPx { get; init; }
    public required int HeightPx { get; init; }
    public int? RefreshHz { get; init; }
    public required bool BuiltIn { get; init; }
    public string? AttachedTo { get; init; }
}

internal sealed record ContractEvent
{
    public required DateTimeOffset T { get; init; }
    public required string Kind { get; init; }
    public string? NodeId { get; init; }
    public string? VidPid { get; init; }
    public string? Name { get; init; }
    public ContractEnvelope? Snapshot { get; init; }
}
