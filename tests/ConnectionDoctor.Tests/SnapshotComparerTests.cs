namespace ConnectionDoctor.Tests;

public sealed class SnapshotComparerTests
{
    [Fact]
    public void CompareDetectsDisplayAliveWithMissingLgUsbBranch()
    {
        var monitor = Device(
            @"DISPLAY\GSM77B3\1",
            "Monitor",
            "Generic Monitor (LG ULTRAWIDE)");
        var hub = DeviceWithParent(
            @"USB\VID_043E&PID_9C04\HUB",
            "USB",
            "Generic USB Hub",
            @"DISPLAY\GSM77B3\1");
        var keyboard = DeviceWithParent(
            @"HID\VID_046D&PID_C08A&MI_01\KEYBOARD",
            "Keyboard",
            "HID Keyboard Device",
            @"USB\VID_043E&PID_9C04\HUB");
        var mouse = DeviceWithParent(
            @"HID\VID_046D&PID_C08A&MI_00\MOUSE",
            "Mouse",
            "HID-compliant mouse",
            @"USB\VID_043E&PID_9C04\HUB");

        var baseline = Snapshot(monitor, hub, keyboard, mouse);
        var current = Snapshot(monitor);

        var report = SnapshotComparer.Compare(baseline, current);

        Assert.Contains(report.Findings, finding =>
            finding.Severity == "critical" &&
            finding.Title.Contains("USB hub branch is missing", StringComparison.Ordinal));
        Assert.Contains(report.Findings, finding =>
            finding.Title.Contains("Keyboard and mouse", StringComparison.Ordinal));
        Assert.Equal(3, report.Missing.Count);
    }

    [Fact]
    public void CompareDetectsDisplayAliveWithMissingNonLgUsbBranch()
    {
        var monitor = Device(
            @"DISPLAY\DELL1234\1",
            "Monitor",
            "Generic Monitor (Dell UltraSharp)");
        var hub = DeviceWithParent(
            @"USB\VID_413C&PID_B06E\HUB",
            "USB",
            "Dell USB Hub",
            @"DISPLAY\DELL1234\1");
        var keyboard = DeviceWithParent(
            @"HID\VID_046D&PID_C340&MI_01\KEYBOARD",
            "Keyboard",
            "HID Keyboard Device",
            @"USB\VID_413C&PID_B06E\HUB");
        var mouse = DeviceWithParent(
            @"HID\VID_046D&PID_C340&MI_00\MOUSE",
            "Mouse",
            "HID-compliant mouse",
            @"USB\VID_413C&PID_B06E\HUB");

        var baseline = Snapshot(monitor, hub, keyboard, mouse);
        var current = Snapshot(monitor);

        var report = SnapshotComparer.Compare(baseline, current);

        Assert.Contains(report.Findings, finding =>
            finding.Severity == "critical" &&
            finding.Title.Contains("USB hub branch is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void CompareMissingHubWithInputChildrenButNoMonitorProducesWarningNotCritical()
    {
        var hub = DeviceWithParent(
            @"USB\VID_1234&PID_5678\HUB",
            "USB",
            "Generic USB Hub",
            @"PCI\VEN_1234\ROOT");
        var keyboard = DeviceWithParent(
            @"HID\VID_046D&PID_C08A&MI_01\KEYBOARD",
            "Keyboard",
            "HID Keyboard Device",
            @"USB\VID_1234&PID_5678\HUB");
        var mouse = DeviceWithParent(
            @"HID\VID_046D&PID_C08A&MI_00\MOUSE",
            "Mouse",
            "HID-compliant mouse",
            @"USB\VID_1234&PID_5678\HUB");

        var baseline = Snapshot(hub, keyboard, mouse);
        var current = Snapshot();

        var report = SnapshotComparer.Compare(baseline, current);

        Assert.DoesNotContain(report.Findings, finding => finding.Severity == "critical");
        Assert.Contains(report.Findings, finding =>
            finding.Title.Contains("Keyboard and mouse", StringComparison.Ordinal));
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

    private static ConnectionSnapshot Snapshot(params DeviceNode[] devices) =>
        new(
            DateTimeOffset.Parse("2026-08-13T16:00:00-05:00"),
            "SURFACE",
            "Arm64",
            new PowerState(true, 100),
            devices);

    private static DeviceNode Device(string id, string className, string name) =>
        new(id, className, name, null, null);

    private static DeviceNode DeviceWithParent(string id, string className, string name, string parentId) =>
        new(id, className, name, null, parentId);
}
