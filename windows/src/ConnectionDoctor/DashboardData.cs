namespace ConnectionDoctor;

internal sealed record DashboardData(
    CollectorStatus Collector,
    ConnectionSnapshot? Snapshot,
    IReadOnlyList<ConnectionIncident> Incidents,
    ComparisonReport? BaselineComparison,
    string Topology,
    DateTimeOffset LoadedAt)
{
    public static DashboardData Load()
    {
        var snapshot = BackgroundCollector.ReadCurrentSnapshot();
        var incidents = IncidentStitcher.Stitch(BackgroundCollector.ReadEntries())
            .OrderByDescending(incident => incident.Start)
            .ToList();
        ComparisonReport? comparison = null;
        if (snapshot is not null && File.Exists(SnapshotStore.DefaultBaselinePath))
        {
            var currentSnapshot = snapshot;
            try
            {
                comparison = SnapshotComparer.Compare(
                    SnapshotStore.Load(SnapshotStore.DefaultBaselinePath),
                    currentSnapshot);
            }
            catch (JsonException)
            {
                comparison = null;
            }
            catch (IOException)
            {
                comparison = null;
            }
        }

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
            incidents,
            comparison,
            topology,
            DateTimeOffset.Now);
    }
}
