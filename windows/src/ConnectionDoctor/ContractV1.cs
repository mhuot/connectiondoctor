using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace ConnectionDoctor;

/// <summary>
/// Connection Contract v1 — the one JSON shape TBDoctor (macOS) and
/// ConnectionDoctor (Windows) both emit, so a single dashboard can read every
/// machine's recording. Canonical spec: docs/schema-v1.md at the repo root.
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
    public static ContractEnvelope ToEnvelope(
        ConnectionSnapshot snapshot,
        bool includeBuiltIn = true,
        ResolvedIdentity? identity = null)
    {
        // Once, here, for the whole document — never once per device.
        var who = identity ?? ResolvedIdentity.ForThisProcess();
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
                Protocol = ProtocolOf(kind, device.LinkSpeed),
                LinkBitsPerSecond = BitsPerSecond(device.LinkSpeed),
                UsbClass = device.UsbClass,
                // Two identical docks are two units here and nothing anywhere
                // else: the serial is keyed with a secret that stays on this
                // machine, and is absent when the device reports no serial.
                UnitKey = who.UnitKey(device.Serial),
                // The producer's classification (dashboard-topology-controls):
                // integrated panel/touch/HID and the internal buses they hang
                // off are built-in; anything reached through an external bus
                // node is not. The dashboard filters on this flag as a view
                // choice; nodes are always exported.
                BuiltIn = !DeviceFilters.IsExternalDevice(device, byId),
                Platform = new Dictionary<string, string> { ["instanceId"] = device.InstanceId }
            });
        }

        return new ContractEnvelope
        {
            CapturedAt = snapshot.CapturedAt,
            Host = ToHost(snapshot, who),
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
            // A sync point is a complete envelope: the analysis group computed
            // when the snapshot was written rides along (schema § Events).
            Snapshot = entry.Snapshot is null ? null : entry.Analysis is null
                ? ToEnvelope(entry.Snapshot)
                : ToEnvelope(entry.Snapshot) with
                {
                    Findings = entry.Analysis.Findings,
                    Incidents = entry.Analysis.Incidents,
                    Analysis = entry.Analysis.Analysis
                }
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

    /// <summary>
    /// The live envelope plus findings/incidents/analysis over the recorded
    /// history when there is any — what /contract, `contract`, `probe --json`
    /// and connection_probe all return (three doors, one document).
    /// </summary>
    public static ContractEnvelope ToEnvelopeWithAnalysis(ConnectionSnapshot snapshot, double windowHours = WindowsAnalysis.DefaultWindowHours)
    {
        var envelope = ToEnvelope(snapshot);
        WindowsAnalysis.Attach(envelope, WindowsAnalysis.Run(WindowsAnalysis.ReadInputs(), snapshot, windowHours), out var withAnalysis);
        return withAnalysis;
    }

    public static string Serialize(ContractEnvelope envelope, bool indented = true) =>
        JsonSerializer.Serialize(envelope, indented ? IndentedOptions : CompactOptions);

    /// <summary>Any contract document (report, diff, …) with the same JSON conventions as the envelope.</summary>
    public static string SerializeDocument<T>(T document, bool indented = true) =>
        JsonSerializer.Serialize(document, indented ? IndentedOptions : CompactOptions);

    public static ContractHost ToHost(ConnectionSnapshot snapshot, ResolvedIdentity? identity = null) => new()
    {
        Name = snapshot.HostName,
        // Random and per-installation, and omitted entirely when there is no
        // durable identity: an id that changes between runs would split one
        // endpoint into many, so absent (fall back to the hostname) is honest.
        Id = (identity ?? ResolvedIdentity.ForThisProcess()).HostId,
        Arch = snapshot.OperatingSystemArchitecture.ToLowerInvariant()
    };

    /// <summary>
    /// A subset of a snapshot's devices as contract nodes — used by the diff
    /// document so `missing`/`added` render with the same code as topology.
    /// Parents resolve within the snapshot the devices came from.
    /// </summary>
    public static IReadOnlyList<ContractNode> ToNodes(
        ConnectionSnapshot snapshot,
        IEnumerable<DeviceNode> devices,
        ResolvedIdentity? identity = null)
    {
        var who = identity ?? ResolvedIdentity.ForThisProcess();
        var byId = snapshot.Devices.ToDictionary(device => device.InstanceId, StringComparer.OrdinalIgnoreCase);
        var included = new HashSet<string>(
            DeviceFilters.TopologyDevices(snapshot, includeBuiltIn: true).Select(device => device.InstanceId),
            StringComparer.OrdinalIgnoreCase);
        return devices.Select(device =>
        {
            var kind = KindOf(device);
            return new ContractNode
            {
                Id = NodeId(device),
                ParentId = ParentNodeId(device, byId, included),
                Kind = kind,
                Name = device.FriendlyName,
                VendorName = device.Manufacturer,
                VidPid = device.VidPid,
                Protocol = ProtocolOf(kind, device.LinkSpeed),
                LinkBitsPerSecond = BitsPerSecond(device.LinkSpeed),
                UsbClass = device.UsbClass,
                UnitKey = who.UnitKey(device.Serial),
                Platform = new Dictionary<string, string> { ["instanceId"] = device.InstanceId }
            };
        }).ToList();
    }

    /// <summary>Schema Finding: string severity, mandatory evidence.</summary>
    public static ContractFinding ToFinding(Finding finding) => new()
    {
        Severity = finding.Severity,
        Title = finding.Title,
        Explanation = finding.Explanation,
        Evidence = finding.Evidence,
        Recommendation = finding.Recommendation,
        Confidence = finding.Confidence
    };

    /// <summary>
    /// Schema Incident from a stitched group of recorded changes. Windows has no
    /// kernel link events yet, so rootEvent is absent (origin unattributed);
    /// sharedParent is set when every lost device hangs off one parent.
    /// </summary>
    /// <param name="incident">The stitched incident.</param>
    /// <param name="recording">The recorded entries the incident came from (snapshots included), used to
    /// resolve a shared parent from the pre-incident topology; the stitcher strips snapshots from
    /// <see cref="ConnectionIncident.Events"/>.</param>
    public static ContractIncident ToIncident(ConnectionIncident incident, IReadOnlyList<RecorderEntry>? recording = null)
    {
        var lost = incident.Events
            .Where(entry => entry.Kind == RecorderEntryKinds.DeviceDisappeared && entry.Device is not null)
            .Select(entry => entry.Device!)
            .ToList();

        // sharedParent is the grouped-loss finding in data form, so it is
        // asserted only on evidence: every lost device must name a parent, all
        // must name the same one, and that parent must be a device we can
        // actually resolve (from the incident's own entries or a snapshot in
        // it) — never a guessed "usb:" prefix on an unresolved id. A device
        // with unknown parentage makes the attribution unknown, not "the rest".
        string? sharedParent = null;
        if (lost.Count >= 2 && lost.All(device => device.ParentInstanceId is not null))
        {
            var parents = lost.Select(device => device.ParentInstanceId!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (parents.Count == 1)
            {
                var parentDevice = ResolveDevice(parents[0], incident, recording);
                if (parentDevice is not null)
                {
                    sharedParent = NodeId(parentDevice);
                }
            }
        }

        // Peak discharge across the incident, from the recorded power samples
        // (negative while discharging). Absent when no sample carried a rate.
        var peak = incident.Events
            .Select(entry => entry.Power?.BatteryRateMilliwatts)
            .Where(rate => rate is not null and < 0)
            .Select(rate => rate!.Value)
            .DefaultIfEmpty()
            .Min();

        return new ContractIncident
        {
            Start = incident.Start,
            End = incident.End,
            DevicesLost = lost.Select(device => new ContractIncidentDevice
            {
                VidPid = device.VidPid,
                Name = device.FriendlyName,
                NodeId = NodeId(device)
            }).ToList(),
            SharedParent = sharedParent,
            Power = peak < 0 ? new ContractIncidentPower { PeakDischargeMilliwatts = peak } : null
        };
    }

    /// <summary>
    /// A device by instance id, from the incident's own entries or — preferably —
    /// the last full snapshot at or before the incident started (the pre-incident
    /// topology). Null when nothing recorded names it: an unresolved parent is
    /// unknown, never a guessed id.
    /// </summary>
    private static DeviceNode? ResolveDevice(string instanceId, ConnectionIncident incident, IReadOnlyList<RecorderEntry>? recording)
    {
        bool Matches(DeviceNode device) => device.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase);

        var fromIncident = incident.Events
            .Select(entry => entry.Device)
            .FirstOrDefault(device => device is not null && Matches(device));
        if (fromIncident is not null)
        {
            return fromIncident;
        }

        if (recording is null)
        {
            return null;
        }

        // Evidence boundary: only the last full snapshot at or before the
        // incident. Nothing recorded *after* it may turn an unknown parent into
        // a known one retroactively.
        var preIncidentSnapshot = recording
            .Where(entry => entry.Snapshot is not null && entry.At <= incident.Start)
            .OrderByDescending(entry => entry.At)
            .Select(entry => entry.Snapshot!)
            .FirstOrDefault();
        return preIncidentSnapshot?.Devices.FirstOrDefault(Matches);
    }

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
    /// The link into a node, from the speed the hub says the port negotiated
    /// (see UsbSpeedProbe). Still "unknown" when no hub would answer, and
    /// Tunneled stays false throughout: knowing a link is 10 Gbps does not
    /// establish that USB4 is tunneling it.
    /// </summary>
    private static string ProtocolOf(string kind, UsbLinkSpeed speed)
    {
        if (kind == "display")
        {
            return "displayPort";
        }

        if (kind == "thunderbolt")
        {
            return "thunderbolt";
        }

        return speed switch
        {
            UsbLinkSpeed.Super or UsbLinkSpeed.SuperPlus => "usb3",
            UsbLinkSpeed.High => "usb2",
            UsbLinkSpeed.Low or UsbLinkSpeed.Full => "usbLow",
            _ => "unknown"
        };
    }

    /// <summary>Nominal signalling rate for a negotiated speed.</summary>
    private static long? BitsPerSecond(UsbLinkSpeed speed) => speed switch
    {
        UsbLinkSpeed.Low => 1_500_000L,
        UsbLinkSpeed.Full => 12_000_000L,
        UsbLinkSpeed.High => 480_000_000L,
        UsbLinkSpeed.Super => 5_000_000_000L,
        UsbLinkSpeed.SuperPlus => 10_000_000_000L,
        _ => null
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
    /// <summary>Present only when recorded history exists (absent ≠ empty). See WindowsAnalysis.</summary>
    public IReadOnlyList<ContractFinding>? Findings { get; init; }
    public IReadOnlyList<ContractIncident>? Incidents { get; init; }
    public ContractAnalysis? Analysis { get; init; }
}

/// <summary>schema-v1.md § Envelope — analysis{}: window, coverage, baseline state, capabilities.</summary>
internal sealed record ContractAnalysis
{
    public required double WindowHours { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required ContractCoverage Coverage { get; init; }
    public ContractBaselineState? Baseline { get; init; }
    public ContractCapabilities? Capabilities { get; init; }
}

internal sealed record ContractCoverage
{
    public required DateTimeOffset AvailableFrom { get; init; }
    public required DateTimeOffset Through { get; init; }
    public required bool Complete { get; init; }
    public IReadOnlyList<string>? Reasons { get; init; }
}

internal sealed record ContractBaselineState
{
    public required string State { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public DateTimeOffset? FaultSince { get; init; }
    public DateTimeOffset? RecoveredAt { get; init; }
}

internal sealed record ContractCapabilities
{
    public required string LinkEvents { get; init; }
    /// <summary>
    /// Whether the known-good baseline could be evaluated: `available`, or
    /// `busy` / `unreadable` / `history-unreadable` / `history-unwritable`.
    /// Deliberately here and not in coverage.reasons — coverage is temporal,
    /// and a complete history with an unknown baseline is a real state.
    /// </summary>
    public string? Baseline { get; init; }
}

internal sealed record ContractHost
{
    public required string Name { get; init; }
    public string? Id { get; init; }
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
    public long? LinkBitsPerSecond { get; init; }
    public bool Tunneled { get; init; }
    public int? UsbClass { get; init; }
    /// <summary>Producer classification of integrated devices; absent means unknown.</summary>
    public bool? BuiltIn { get; init; }
    /// <summary>HMAC of the device serial under this installation's key; absent when it reports none.</summary>
    public string? UnitKey { get; init; }
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

/// <summary>docs/schema-v1.md § Documents — Report.</summary>
internal sealed record ContractReport
{
    public string Schema { get; init; } = ContractV1.SchemaId;
    public string Kind { get; init; } = "report";
    public required ContractHost Host { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required double WindowHours { get; init; }
    public IReadOnlyList<ContractFinding>? Findings { get; init; }
    public IReadOnlyList<ContractIncident>? Incidents { get; init; }
    public string? Note { get; init; }
}

/// <summary>docs/schema-v1.md § Documents — Diff.</summary>
internal sealed record ContractDiff
{
    public string Schema { get; init; } = ContractV1.SchemaId;
    public string Kind { get; init; } = "diff";
    public required ContractHost Host { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required DateTimeOffset BaselineCapturedAt { get; init; }
    public required IReadOnlyList<ContractFinding> Findings { get; init; }
    public required IReadOnlyList<ContractNode> Missing { get; init; }
    public required IReadOnlyList<ContractNode> Added { get; init; }
    public string? Note { get; init; }
}

internal sealed record ContractFinding
{
    public required string Severity { get; init; }
    public required string Title { get; init; }
    public required string Explanation { get; init; }
    public required IReadOnlyList<string> Evidence { get; init; }
    public required string Recommendation { get; init; }
    public string? Confidence { get; init; }
}

internal sealed record ContractIncident
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public string? RootEvent { get; init; }
    public required IReadOnlyList<ContractIncidentDevice> DevicesLost { get; init; }
    public string? SharedParent { get; init; }
    public ContractIncidentPower? Power { get; init; }
}

internal sealed record ContractIncidentPower
{
    public required int PeakDischargeMilliwatts { get; init; }
}

internal sealed record ContractIncidentDevice
{
    public string? VidPid { get; init; }
    public required string Name { get; init; }
    public string? NodeId { get; init; }
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
