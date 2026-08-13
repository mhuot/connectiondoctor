namespace ConnectionDoctor;

internal static class SnapshotComparer
{
    public static ComparisonReport Compare(ConnectionSnapshot baseline, ConnectionSnapshot current)
    {
        var missing = Difference(baseline.Devices, current.Devices);
        var added = Difference(current.Devices, baseline.Devices);
        var findings = new List<Finding>();

        // Build a parent→children index from the baseline snapshot.
        var baselineChildrenByParent = baseline.Devices
            .Where(device => device.ParentInstanceId != null)
            .GroupBy(device => device.ParentInstanceId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        // Identify missing hubs whose baseline children include ≥ 2 input-class devices.
        var inputClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Keyboard", "Mouse", "HIDClass" };
        var missingHubsWithInputChildren = missing
            .Where(device => device.ClassName.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                             device.ClassName.Equals("USBDevice", StringComparison.OrdinalIgnoreCase))
            .Where(device =>
                baselineChildrenByParent.TryGetValue(device.InstanceId, out var children) &&
                children.Count(child => inputClasses.Contains(child.ClassName)) >= 2)
            .ToList();

        var monitorStillPresent = baseline.Devices.Any(device =>
            device.ClassName.Equals("Monitor", StringComparison.OrdinalIgnoreCase)) &&
            current.Devices.Any(device =>
            device.ClassName.Equals("Monitor", StringComparison.OrdinalIgnoreCase));

        if (missingHubsWithInputChildren.Count > 0 && monitorStillPresent)
        {
            var hubDesc = missingHubsWithInputChildren[0].VidPid ?? missingHubsWithInputChildren[0].FriendlyName;
            findings.Add(new Finding(
                "critical",
                "Display is active but its USB hub branch is missing",
                $"The video path survived while the monitor's USB hub ({hubDesc}) did not enumerate. Devices behind that hub are fallout, not separate failures.",
                "Cold power-cycle the monitor for at least 30 seconds, then reconnect USB-C."));
        }

        var missingInputDevices = missing.Where(device =>
            device.ClassName.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) ||
            device.ClassName.Equals("Mouse", StringComparison.OrdinalIgnoreCase)).ToList();
        if (missingInputDevices.Count >= 2)
        {
            findings.Add(new Finding(
                "warning",
                "Keyboard and mouse disappeared together",
                $"{missingInputDevices.Count} input-device interfaces from the known-good state are absent, which suggests an upstream hub or connection failure.",
                "Investigate their shared parent hub before reinstalling individual device drivers."));
        }

        return new ComparisonReport(missing, added, findings);
    }

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
