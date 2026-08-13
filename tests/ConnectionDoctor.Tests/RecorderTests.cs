namespace ConnectionDoctor.Tests;

public sealed class RecorderTests
{
    [Fact]
    public void StitcherGroupsSimultaneousDockLossesIntoOneIncident()
    {
        var start = DateTimeOffset.Parse("2026-08-13T16:00:00-05:00");
        var entries = new[]
        {
            Entry(start, "Dock Ethernet"),
            Entry(start.AddSeconds(1), "Dock Audio"),
            Entry(start.AddSeconds(2), "Keyboard"),
            Entry(start.AddMinutes(2), "Camera")
        };

        var incidents = IncidentStitcher.Stitch(entries);

        Assert.Equal(2, incidents.Count);
        Assert.Equal(3, incidents[0].Events.Count);
        Assert.Single(incidents[1].Events);
    }

    [Fact]
    public void RecorderEmitsConnectionChangesButIgnoresSoftwareChurn()
    {
        var usb = SnapshotComparerTests.Device(@"USB\VID_1234&PID_5678\1", "USB", "USB Device");
        var software = SnapshotComparerTests.Device(@"SWD\PRINTENUM\1", "PrintQueue", "Printer");
        var previous = SnapshotComparerTests.Snapshot(usb, software);
        var current = SnapshotComparerTests.Snapshot();

        var entries = Recorder.DetectChanges(previous, current);

        var entry = Assert.Single(entries);
        Assert.Equal(RecorderEntryKinds.DeviceDisappeared, entry.Kind);
        Assert.Equal(usb.InstanceId, entry.Device?.InstanceId);
    }

    private static RecorderEntry Entry(DateTimeOffset at, string name) =>
        new(
            at,
            RecorderEntryKinds.DeviceDisappeared,
            SnapshotComparerTests.Device($@"USB\{name}", "USB", name),
            new PowerState(true, 100, 0),
            null);
}
