namespace ConnectionDoctor;

internal static class SnapshotComparer
{
    public static ComparisonReport Compare(ConnectionSnapshot baseline, ConnectionSnapshot current)
    {
        var baselineDevices = baseline.Devices.Where(DeviceFilters.IsConnectionDevice).ToList();
        var currentDevices = current.Devices.Where(DeviceFilters.IsConnectionDevice).ToList();
        var missing = Difference(baselineDevices, currentDevices);
        var added = Difference(currentDevices, baselineDevices);
        var findings = new List<Finding>();

        var lgDisplayPresent = currentDevices.Any(device =>
            device.ClassName.Equals("Monitor", StringComparison.OrdinalIgnoreCase) &&
            device.FriendlyName.Contains("LG", StringComparison.OrdinalIgnoreCase));
        var missingLgHub = missing.Any(device =>
            device.VidPid == "043E:9C04" ||
            device.FriendlyName.Contains("LG", StringComparison.OrdinalIgnoreCase) &&
            device.ClassName.Equals("USB", StringComparison.OrdinalIgnoreCase));

        if (lgDisplayPresent && missingLgHub)
        {
            findings.Add(new Finding(
                "critical",
                "LG display is active but its USB hub branch is missing",
                "The video path survived while the monitor's USB hub did not enumerate. Devices behind that hub are fallout, not separate failures.",
                "Cold power-cycle the LG monitor for at least 30 seconds, then reconnect USB-C."));
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
