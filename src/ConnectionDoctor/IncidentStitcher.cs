namespace ConnectionDoctor;

/// <summary>
/// Groups consecutive <see cref="WatchEvent"/> change records that fall
/// within a 30-second window into <see cref="Incident"/> objects.
/// </summary>
internal static class IncidentStitcher
{
    private static readonly TimeSpan StitchWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Produces incidents from a flat list of change events, newest first.
    /// </summary>
    public static IReadOnlyList<Incident> Stitch(IReadOnlyList<WatchEvent> events)
    {
        // Only care about change events (not snapshot sync-points)
        var changes = events
            .Where(e => e.Kind == "change")
            .OrderBy(e => e.Timestamp)
            .ToList();

        var incidents = new List<Incident>();
        var i = 0;
        while (i < changes.Count)
        {
            var windowStart = changes[i];
            var windowEnd = windowStart;
            var lost = new List<DeviceNode>(windowStart.DevicesRemoved);
            var gained = new List<DeviceNode>(windowStart.DevicesAdded);

            // Accumulate events within the stitch window
            var j = i + 1;
            while (j < changes.Count && changes[j].Timestamp - windowStart.Timestamp <= StitchWindow)
            {
                windowEnd = changes[j];
                lost.AddRange(windowEnd.DevicesRemoved);
                gained.AddRange(windowEnd.DevicesAdded);
                j++;
            }

            // De-duplicate: a device that was removed then re-added in the same window
            // is not reported as lost — only net changes are surfaced.
            var lostNet = NetLost(lost, gained);
            var gainedNet = NetGained(gained, lost);

            if (lostNet.Count > 0 || gainedNet.Count > 0)
            {
                incidents.Add(new Incident(
                    windowStart.Timestamp,
                    windowEnd.Timestamp,
                    lostNet,
                    gainedNet,
                    windowStart.Power));
            }

            i = j;
        }

        // Newest first
        incidents.Reverse();
        return incidents;
    }

    private static IReadOnlyList<DeviceNode> NetLost(List<DeviceNode> lost, List<DeviceNode> gained)
    {
        var gainedIds = gained
            .GroupBy(d => d.StableId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new List<DeviceNode>();
        foreach (var device in lost)
        {
            if (gainedIds.TryGetValue(device.StableId, out var count) && count > 0)
            {
                gainedIds[device.StableId] = count - 1;
            }
            else
            {
                result.Add(device);
            }
        }
        return result;
    }

    private static IReadOnlyList<DeviceNode> NetGained(List<DeviceNode> gained, List<DeviceNode> lost)
    {
        var lostIds = lost
            .GroupBy(d => d.StableId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new List<DeviceNode>();
        foreach (var device in gained)
        {
            if (lostIds.TryGetValue(device.StableId, out var count) && count > 0)
            {
                lostIds[device.StableId] = count - 1;
            }
            else
            {
                result.Add(device);
            }
        }
        return result;
    }
}
