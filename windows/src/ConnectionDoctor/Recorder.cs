namespace ConnectionDoctor;

internal static class RecorderEntryKinds
{
    public const string Snapshot = "snapshot";
    public const string DeviceAppeared = "device-appeared";
    public const string DeviceDisappeared = "device-disappeared";
    public const string PowerChanged = "power-changed";
    public const string DeficitStarted = "deficit-started";
    public const string DeficitEnded = "deficit-ended";
}

internal sealed record RecorderEntry(
    DateTimeOffset At,
    string Kind,
    DeviceNode? Device,
    PowerState? Power,
    ConnectionSnapshot? Snapshot,
    /// <summary>On full snapshots: the analysis group computed when the snapshot was written, so the sync point is a complete envelope. Additive in the on-disk format.</summary>
    EmbeddedAnalysis? Analysis = null)
{
    public static RecorderEntry FullSnapshot(ConnectionSnapshot snapshot, EmbeddedAnalysis? analysis = null) =>
        new(snapshot.CapturedAt, RecorderEntryKinds.Snapshot, null, snapshot.Power, snapshot, analysis);
}

internal static class Recorder
{
    public static IReadOnlyList<RecorderEntry> DetectChanges(
        ConnectionSnapshot previous,
        ConnectionSnapshot current)
    {
        var entries = new List<RecorderEntry>();
        var previousDevices = DeviceFilters.ConnectionDevices(previous)
            .ToDictionary(device => device.InstanceId, StringComparer.OrdinalIgnoreCase);
        var currentDevices = DeviceFilters.ConnectionDevices(current)
            .ToDictionary(device => device.InstanceId, StringComparer.OrdinalIgnoreCase);

        foreach (var device in previousDevices.Values.Where(device => !currentDevices.ContainsKey(device.InstanceId)))
        {
            entries.Add(new RecorderEntry(
                current.CapturedAt,
                RecorderEntryKinds.DeviceDisappeared,
                device,
                current.Power,
                null));
        }

        foreach (var device in currentDevices.Values.Where(device => !previousDevices.ContainsKey(device.InstanceId)))
        {
            entries.Add(new RecorderEntry(
                current.CapturedAt,
                RecorderEntryKinds.DeviceAppeared,
                device,
                current.Power,
                null));
        }

        if (previous.Power.IsDeficit != current.Power.IsDeficit)
        {
            entries.Add(new RecorderEntry(
                current.CapturedAt,
                current.Power.IsDeficit ? RecorderEntryKinds.DeficitStarted : RecorderEntryKinds.DeficitEnded,
                null,
                current.Power,
                null));
        }
        else if (previous.Power.LineOnline != current.Power.LineOnline)
        {
            entries.Add(new RecorderEntry(
                current.CapturedAt,
                RecorderEntryKinds.PowerChanged,
                null,
                current.Power,
                null));
        }

        return entries;
    }
}

/// <summary>Contract findings/incidents/analysis as stored beside a full snapshot.</summary>
internal sealed record EmbeddedAnalysis(
    IReadOnlyList<ContractFinding> Findings,
    IReadOnlyList<ContractIncident> Incidents,
    ContractAnalysis Analysis);

internal sealed record ConnectionIncident(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<RecorderEntry> Events)
{
    public TimeSpan Duration => End - Start;
}

internal static class IncidentStitcher
{
    private static readonly TimeSpan IncidentGap = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<ConnectionIncident> Stitch(IEnumerable<RecorderEntry> entries)
    {
        var changes = entries
            .Where(entry => entry.Kind != RecorderEntryKinds.Snapshot)
            .OrderBy(entry => entry.At)
            .ToList();
        if (changes.Count == 0)
        {
            return [];
        }

        var incidents = new List<ConnectionIncident>();
        var group = new List<RecorderEntry> { changes[0] };
        foreach (var entry in changes.Skip(1))
        {
            if (entry.At - group[^1].At <= IncidentGap)
            {
                group.Add(entry);
                continue;
            }

            incidents.Add(Create(group));
            group = [entry];
        }

        incidents.Add(Create(group));
        return incidents;
    }

    private static ConnectionIncident Create(IReadOnlyList<RecorderEntry> entries) =>
        new(entries[0].At, entries[^1].At, entries.ToList());
}
