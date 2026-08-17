namespace ConnectionDoctor;

internal static class SnapshotComparer
{
    public static ComparisonReport Compare(ConnectionSnapshot baseline, ConnectionSnapshot current)
    {
        var baselineDevices = DeviceFilters.ConnectionDevices(baseline);
        var currentDevices = DeviceFilters.ConnectionDevices(current);
        var missing = Difference(baselineDevices, currentDevices);
        var added = Difference(currentDevices, baselineDevices);
        var findings = new List<Finding>();

        findings.AddRange(MonitorHubFindings(baseline, currentDevices, missing));

        var missingInputDevices = missing.Where(IsInputDevice).ToList();
        if (missingInputDevices.Count >= 2)
        {
            findings.Add(new Finding(
                "warning",
                "Keyboard and mouse interfaces disappeared together",
                $"{missingInputDevices.Count} input-device interfaces from the known-good state are absent, which suggests an upstream hub or connection failure.",
                "Investigate their shared parent hub before reinstalling individual device drivers.",
                missingInputDevices
                    .Select(device => $"Missing: {device.FriendlyName} [{device.VidPid ?? device.ClassName}]")
                    .Take(8)
                    .ToList()));
        }

        findings.AddRange(PowerDiagnosis.Analyze(current.Power));
        return new ComparisonReport(missing, added, findings);
    }

    private static IEnumerable<Finding> MonitorHubFindings(
        ConnectionSnapshot baseline,
        IReadOnlyList<DeviceNode> currentDevices,
        IReadOnlyList<DeviceNode> missing)
    {
        var currentStableIds = currentDevices.Select(device => device.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var monitorSurvived = baseline.Devices.Any(device =>
            device.ClassName.Equals("Monitor", StringComparison.OrdinalIgnoreCase) &&
            currentStableIds.Contains(device.StableId));
        if (!monitorSurvived)
        {
            yield break;
        }

        var children = baseline.Devices
            .Where(device => device.ParentInstanceId is not null)
            .GroupBy(device => device.ParentInstanceId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var hub in missing.Where(IsHub))
        {
            var strandedInputs = Descendants(hub.InstanceId, children)
                .Where(IsInputDevice)
                .ToList();
            if (strandedInputs.Count < 2)
            {
                continue;
            }

            var hubIdentity = hub.VidPid is null
                ? hub.FriendlyName
                : $"{hub.FriendlyName} ({hub.VidPid})";
            var childNames = string.Join(
                ", ",
                strandedInputs.Select(device => device.FriendlyName).Distinct().Take(4));
            var evidence = new List<string>
            {
                $"Display path present in both baseline and current state",
                $"Hub absent now, present in baseline: {hubIdentity}"
            };
            evidence.AddRange(strandedInputs
                .Select(device => $"Stranded behind it: {device.FriendlyName} [{device.VidPid ?? device.ClassName}]")
                .Distinct()
                .Take(6));
            yield return new Finding(
                "critical",
                "Display is active but a baseline USB hub branch is missing",
                $"{hubIdentity} did not enumerate while the display path survived. Stranded input interfaces include: {childNames}. These are downstream fallout, not separate failures.",
                "Cold power-cycle the monitor or dock containing the missing hub for at least 30 seconds, then reconnect USB-C.",
                evidence,
                "high");
        }
    }

    private static IEnumerable<DeviceNode> Descendants(
        string parentId,
        IReadOnlyDictionary<string, List<DeviceNode>> children)
    {
        var queue = new Queue<string>();
        queue.Enqueue(parentId);
        while (queue.TryDequeue(out var current))
        {
            if (!children.TryGetValue(current, out var childNodes))
            {
                continue;
            }

            foreach (var child in childNodes)
            {
                yield return child;
                queue.Enqueue(child.InstanceId);
            }
        }
    }

    private static bool IsHub(DeviceNode device) =>
        device.ClassName.Equals("USB", StringComparison.OrdinalIgnoreCase) &&
        device.FriendlyName.Contains("Hub", StringComparison.OrdinalIgnoreCase) &&
        !device.FriendlyName.Contains("Root Hub", StringComparison.OrdinalIgnoreCase);

    private static bool IsInputDevice(DeviceNode device) =>
        device.ClassName.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) ||
        device.ClassName.Equals("Mouse", StringComparison.OrdinalIgnoreCase) ||
        device.ClassName.Equals("HIDClass", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<DeviceNode> Difference(
        IReadOnlyList<DeviceNode> expected,
        IReadOnlyList<DeviceNode> actual)
    {
        var actualCounts = actual
            .GroupBy(device => device.StableId)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var missing = new List<DeviceNode>();

        foreach (var device in expected)
        {
            if (actualCounts.TryGetValue(device.StableId, out var count) && count > 0)
            {
                actualCounts[device.StableId] = count - 1;
            }
            else
            {
                missing.Add(device);
            }
        }

        return missing;
    }
}
