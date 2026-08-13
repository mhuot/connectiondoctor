namespace ConnectionDoctor.Tests;

public sealed class SnapshotComparerTests
{
    [Fact]
    public void CompareDetectsDisplayAliveWithMissingLgUsbBranch()
    {
        var baseline = MonitorHubSnapshot("LG ULTRAWIDE", "043E", "9C04");
        var current = Snapshot(baseline.Devices.Single(device => device.ClassName == "Monitor"));

        var report = SnapshotComparer.Compare(baseline, current);

        Assert.Contains(report.Findings, finding =>
            finding.Severity == "critical" &&
            finding.Title.Contains("Display is active", StringComparison.Ordinal));
        Assert.Contains(report.Findings, finding =>
            finding.Title.Contains("Keyboard and mouse", StringComparison.Ordinal));
    }

    [Fact]
    public void CompareGeneralizesMonitorHubFindingToDell()
    {
        var baseline = MonitorHubSnapshot("DELL U4025QW", "413C", "BEEF");
        var current = Snapshot(baseline.Devices.Single(device => device.ClassName == "Monitor"));

        var report = SnapshotComparer.Compare(baseline, current);

        var finding = Assert.Single(report.Findings.Where(item => item.Severity == "critical"));
        Assert.Contains("413C:BEEF", finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingInputHubWithoutSurvivingMonitorIsOnlyWarning()
    {
        var baselineWithMonitor = MonitorHubSnapshot("DELL U4025QW", "413C", "BEEF");
        var baseline = baselineWithMonitor with
        {
            Devices = baselineWithMonitor.Devices.Where(device => device.ClassName != "Monitor").ToList()
        };

        var report = SnapshotComparer.Compare(baseline, Snapshot());

        Assert.DoesNotContain(report.Findings, finding => finding.Severity == "critical");
        Assert.Contains(report.Findings, finding => finding.Severity == "warning");
    }

    [Fact]
    public void CompareIgnoresSoftwareDeviceChurn()
    {
        var softwareDevice = Device(
            @"SWD\PRINTENUM\PRINTER",
            "PrintQueue",
            "Office Printer");

        var report = SnapshotComparer.Compare(Snapshot(softwareDevice), Snapshot());

        Assert.Empty(report.Missing);
        Assert.Empty(report.Added);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void CompareReportsNoChangesForEquivalentSnapshots()
    {
        var devices = new[]
        {
            Device(@"USB\VID_043E&PID_9C04\HUB", "USB", "Generic USB Hub"),
            Device(@"HID\VID_046D&PID_C08A&MI_00\MOUSE", "Mouse", "HID-compliant mouse")
        };

        var report = SnapshotComparer.Compare(Snapshot(devices), Snapshot(devices));

        Assert.Empty(report.Missing);
        Assert.Empty(report.Added);
        Assert.Empty(report.Findings);
    }

    private static ConnectionSnapshot MonitorHubSnapshot(
        string monitorName,
        string hubVendor,
        string hubProduct)
    {
        const string compositeId = @"USB\VID_046D&PID_C08A\COMPOSITE";
        var hubId = $@"USB\VID_{hubVendor}&PID_{hubProduct}\HUB";
        return Snapshot(
            Device(@"DISPLAY\MONITOR\1", "Monitor", $"Generic Monitor ({monitorName})"),
            Device(hubId, "USB", "Generic USB Hub"),
            Device(compositeId, "USB", "USB Composite Device", hubId),
            Device(@"HID\VID_046D&PID_C08A&MI_01\KEYBOARD", "Keyboard", "HID Keyboard Device", compositeId),
            Device(@"HID\VID_046D&PID_C08A&MI_00\MOUSE", "Mouse", "HID-compliant mouse", compositeId));
    }

    internal static ConnectionSnapshot Snapshot(params DeviceNode[] devices) =>
        new(
            DateTimeOffset.Parse("2026-08-13T16:00:00-05:00"),
            "SURFACE",
            "Arm64",
            new PowerState(true, 100, 0),
            devices);

    internal static DeviceNode Device(
        string id,
        string className,
        string name,
        string? parentId = null) =>
        new(id, className, name, null, parentId);
}
