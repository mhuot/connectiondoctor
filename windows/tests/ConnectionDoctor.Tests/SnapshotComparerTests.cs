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
        var hub = Device(
            @"USB\VID_043E&PID_9C04\HUB",
            "USB",
            "Generic USB Hub");
        var keyboard = Device(
            @"HID\VID_046D&PID_C08A&MI_01\KEYBOARD",
            "Keyboard",
            "HID Keyboard Device");
        var mouse = Device(
            @"HID\VID_046D&PID_C08A&MI_00\MOUSE",
            "Mouse",
            "HID-compliant mouse");

        var baseline = Snapshot(monitor, hub, keyboard, mouse);
        var current = Snapshot(monitor);

        var report = SnapshotComparer.Compare(baseline, current);

        Assert.Contains(report.Findings, finding =>
            finding.Severity == "critical" &&
            finding.Title.Contains("LG display", StringComparison.Ordinal));
        Assert.Contains(report.Findings, finding =>
            finding.Title.Contains("Keyboard and mouse", StringComparison.Ordinal));
        Assert.Equal(3, report.Missing.Count);
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
}
