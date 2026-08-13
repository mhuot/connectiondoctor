namespace ConnectionDoctor;

internal sealed record DashboardData(
    CollectorStatus Collector,
    ConnectionSnapshot? Snapshot,
    IReadOnlyList<ConnectionIncident> Incidents,
    ComparisonReport? BaselineComparison,
    string Topology,
    DateTimeOffset LoadedAt);

internal sealed class DashboardDataLoader
{
    private readonly string eventsPath;
    private readonly string currentSnapshotPath;
    private readonly string baselinePath;
    private readonly EventLogCursor eventCursor = new();
    private readonly List<RecorderEntry> recordedEntries = [];
    private IReadOnlyList<ConnectionIncident> cachedIncidents = [];
    private ConnectionSnapshot? cachedBaseline;
    private DateTime baselineWriteTimeUtc = DateTime.MinValue;
    private long baselineLength = -1;

    public DashboardDataLoader(
        string? eventsPath = null,
        string? currentSnapshotPath = null,
        string? baselinePath = null)
    {
        this.eventsPath = eventsPath ?? BackgroundCollector.EventsPath;
        this.currentSnapshotPath = currentSnapshotPath ?? BackgroundCollector.CurrentSnapshotPath;
        this.baselinePath = baselinePath ?? SnapshotStore.DefaultBaselinePath;
    }

    internal long ParsedEventLineCount => eventCursor.ParsedLineCount;
    internal int BaselineLoadCount { get; private set; }

    public DashboardData Load()
    {
        var newEvents = BackgroundCollector.ReadEntriesIncremental(eventsPath, eventCursor);
        if (newEvents.Reset)
        {
            recordedEntries.Clear();
        }
        if (newEvents.Entries.Count > 0)
        {
            recordedEntries.AddRange(newEvents.Entries);
        }
        if (newEvents.Reset || newEvents.Entries.Count > 0)
        {
            cachedIncidents = IncidentStitcher.Stitch(recordedEntries)
                .OrderByDescending(incident => incident.Start)
                .ToList();
        }

        var snapshot = BackgroundCollector.ReadCurrentSnapshot(currentSnapshotPath);
        var baseline = ReadBaseline();
        var comparison = snapshot is not null && baseline is not null
            ? SnapshotComparer.Compare(baseline, snapshot)
            : null;

        var topology = "Waiting for the collector's first snapshot.";
        if (snapshot is not null)
        {
            using var writer = new StringWriter();
            TopologyRenderer.Write(snapshot, writer);
            topology = writer.ToString();
        }

        return new DashboardData(
            BackgroundCollector.ReadStatus(),
            snapshot,
            cachedIncidents,
            comparison,
            topology,
            DateTimeOffset.Now);
    }

    private ConnectionSnapshot? ReadBaseline()
    {
        if (!File.Exists(baselinePath))
        {
            cachedBaseline = null;
            baselineWriteTimeUtc = DateTime.MinValue;
            baselineLength = -1;
            return null;
        }

        var file = new FileInfo(baselinePath);
        if (cachedBaseline is not null &&
            file.LastWriteTimeUtc == baselineWriteTimeUtc &&
            file.Length == baselineLength)
        {
            return cachedBaseline;
        }

        try
        {
            cachedBaseline = SnapshotStore.Load(baselinePath);
            BaselineLoadCount++;
            baselineWriteTimeUtc = file.LastWriteTimeUtc;
            baselineLength = file.Length;
            return cachedBaseline;
        }
        catch (JsonException)
        {
            cachedBaseline = null;
            return null;
        }
        catch (IOException)
        {
            return cachedBaseline;
        }
    }
}
