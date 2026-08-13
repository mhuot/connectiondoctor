namespace ConnectionDoctor.Tests;

public sealed class IncidentStitcherTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-13T10:00:00Z");
    private static readonly PowerState Ac = new(true, 100);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DeviceNode Device(string id, string cls, string name) =>
        new(id, cls, name, null, null);

    private static WatchEvent Change(
        DateTimeOffset ts,
        IReadOnlyList<DeviceNode>? added = null,
        IReadOnlyList<DeviceNode>? removed = null) =>
        new(ts, "change", added ?? [], removed ?? [], Ac);

    private static WatchEvent Snapshot(DateTimeOffset ts, IReadOnlyList<DeviceNode>? devices = null) =>
        new(ts, "snapshot", devices ?? [], [], Ac);

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyInputProducesNoIncidents()
    {
        Assert.Empty(IncidentStitcher.Stitch([]));
    }

    [Fact]
    public void SnapshotEventsAloneProduceNoIncidents()
    {
        var hub = Device(@"USB\VID_1234\HUB", "USB", "Generic USB Hub");
        var events = new[] { Snapshot(T0, [hub]), Snapshot(T0.AddHours(1), [hub]) };

        Assert.Empty(IncidentStitcher.Stitch(events));
    }

    [Fact]
    public void SingleChangeBecomesSingleIncident()
    {
        var hub = Device(@"USB\VID_1234\HUB", "USB", "Generic USB Hub");
        var events = new[] { Change(T0, removed: [hub]) };

        var incidents = IncidentStitcher.Stitch(events);

        Assert.Single(incidents);
        Assert.Contains(hub, incidents[0].Lost);
        Assert.Empty(incidents[0].Gained);
    }

    [Fact]
    public void MultipleChangesWithinWindowMergeIntoOneIncident()
    {
        var hub = Device(@"USB\VID_1234\HUB", "USB", "Generic USB Hub");
        var kbd = Device(@"HID\VID_046D\KBD", "Keyboard", "HID Keyboard Device");
        var mou = Device(@"HID\VID_046D\MOU", "Mouse", "HID Mouse Device");

        // Three events within 10 seconds — should be one incident
        var events = new[]
        {
            Change(T0,              removed: [hub]),
            Change(T0.AddSeconds(2), removed: [kbd]),
            Change(T0.AddSeconds(4), removed: [mou])
        };

        var incidents = IncidentStitcher.Stitch(events);

        Assert.Single(incidents);
        Assert.Equal(3, incidents[0].Lost.Count);
    }

    [Fact]
    public void ChangesOutsideWindowBecomeSeperateIncidents()
    {
        var hub = Device(@"USB\VID_1234\HUB", "USB", "Generic USB Hub");
        var kbd = Device(@"HID\VID_046D\KBD", "Keyboard", "HID Keyboard Device");

        var events = new[]
        {
            Change(T0,                removed: [hub]),
            Change(T0.AddSeconds(60), removed: [kbd])
        };

        var incidents = IncidentStitcher.Stitch(events);

        Assert.Equal(2, incidents.Count);
    }

    [Fact]
    public void IncidentsAreReturnedNewestFirst()
    {
        var hub = Device(@"USB\VID_1234\HUB", "USB", "Hub");
        var kbd = Device(@"HID\VID_046D\KBD", "Keyboard", "Keyboard");

        var events = new[]
        {
            Change(T0,                removed: [hub]),
            Change(T0.AddSeconds(60), removed: [kbd])
        };

        var incidents = IncidentStitcher.Stitch(events);

        Assert.True(incidents[0].StartedAt > incidents[1].StartedAt);
    }

    [Fact]
    public void DeviceRemovedThenRestoredInWindowIsNotReportedAsLost()
    {
        var hub = Device(@"USB\VID_1234\HUB", "USB", "Generic USB Hub");

        var events = new[]
        {
            Change(T0,                removed: [hub]),
            Change(T0.AddSeconds(5),  added:   [hub])
        };

        var incidents = IncidentStitcher.Stitch(events);

        // Net change is zero — hub removed and then restored within the window
        Assert.Empty(incidents);
    }

    [Fact]
    public void IncidentStartedAtMatchesEarliestEventInWindow()
    {
        var hub = Device(@"USB\VID_1234\HUB", "USB", "Hub");
        var kbd = Device(@"HID\VID_046D\KBD", "Keyboard", "Keyboard");

        var events = new[]
        {
            Change(T0,                removed: [hub]),
            Change(T0.AddSeconds(3),  removed: [kbd])
        };

        var incidents = IncidentStitcher.Stitch(events);

        Assert.Single(incidents);
        Assert.Equal(T0, incidents[0].StartedAt);
        Assert.Equal(T0.AddSeconds(3), incidents[0].EndedAt);
    }
}
