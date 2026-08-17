using System.Text.Json;

namespace ConnectionDoctor.Tests;

public sealed class ContractV1Tests
{
    [Fact]
    public void EnvelopeRoundTripsThroughAJsonParserWithTheContractFields()
    {
        var document = Export(DockedSnapshot());

        Assert.Equal("connection-contract/v1", document.GetProperty("schema").GetString());
        Assert.True(DateTimeOffset.TryParse(document.GetProperty("capturedAt").GetString(), out _));
        Assert.Equal("windows", document.GetProperty("host").GetProperty("os").GetString());
        Assert.Equal("SURFACE", document.GetProperty("host").GetProperty("name").GetString());
        Assert.Equal("arm64", document.GetProperty("host").GetProperty("arch").GetString());
        Assert.NotEqual(0, document.GetProperty("nodes").GetArrayLength());
        Assert.True(document.TryGetProperty("power", out _));
    }

    [Fact]
    public void NodeIdsAreUniqueAndEveryParentResolves()
    {
        var nodes = Export(DockedSnapshot()).GetProperty("nodes").EnumerateArray().ToList();
        var ids = nodes.Select(node => node.GetProperty("id").GetString()!).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        foreach (var node in nodes)
        {
            if (node.TryGetProperty("parentId", out var parentId))
            {
                Assert.Contains(parentId.GetString(), ids);
            }
        }
    }

    [Fact]
    public void HierarchyFromParentIdMatchesTreeOutput()
    {
        var snapshot = DockedSnapshot();

        var writer = new StringWriter();
        TopologyRenderer.Write(snapshot, writer);
        var tree = writer.ToString();

        var envelope = ContractV1.ToEnvelope(snapshot);
        var devices = envelope.Nodes.Where(node => node.Id != ContractV1.HostNodeId).ToList();

        // Same population as `tree`, and the same parent for each member.
        var rendered = DeviceFilters.TopologyDevices(snapshot, includeBuiltIn: true);
        Assert.Equal(rendered.Count, devices.Count);
        foreach (var device in rendered)
        {
            Assert.Contains(device.FriendlyName, tree, StringComparison.Ordinal);
            var node = envelope.Nodes.Single(item =>
                item.Platform is not null &&
                item.Platform["instanceId"] == device.InstanceId);

            var expectedParent = device.ParentInstanceId is null
                ? ContractV1.HostNodeId
                : envelope.Nodes
                    .SingleOrDefault(item =>
                        item.Platform is not null &&
                        item.Platform["instanceId"] == device.ParentInstanceId)
                    ?.Id ?? ContractV1.HostNodeId;

            Assert.Equal(expectedParent, node.ParentId);
        }
    }

    [Fact]
    public void DesktopWithoutABatteryReportsMains()
    {
        var snapshot = DockedSnapshot() with { Power = new PowerState(true, -1, null) };

        var power = ContractV1.ToEnvelope(snapshot).Power;

        Assert.Equal("mains", power.Source);
        Assert.False(power.BatteryPresent);
        Assert.True(power.ExternalConnected);
        Assert.Null(power.BatteryPercent);
    }

    [Fact]
    public void SurfaceChargingOverTheDockCableReportsDock()
    {
        var power = ContractV1.ToEnvelope(DockedSnapshot()).Power;

        Assert.Equal("dock", power.Source);
        Assert.True(power.BatteryPresent);
        Assert.Equal(87, power.BatteryPercent);
    }

    [Fact]
    public void ChargingWithNoDockInTheTreeReportsAdapter()
    {
        var snapshot = DockedSnapshot();
        var withoutDock = snapshot with
        {
            Devices = snapshot.Devices
                .Where(device => !device.FriendlyName.Contains("Thunderbolt", StringComparison.Ordinal))
                .ToList()
        };

        Assert.Equal("adapter", ContractV1.ToEnvelope(withoutDock).Power.Source);
    }

    [Fact]
    public void OnBatteryReportsBattery()
    {
        var snapshot = DockedSnapshot() with { Power = new PowerState(false, 62, -8200) };

        var power = ContractV1.ToEnvelope(snapshot).Power;

        Assert.Equal("battery", power.Source);
        Assert.Equal(-8200, power.BatteryRateMilliwatts);
    }

    [Fact]
    public void HubClassMakesAHubEvenWhenTheNameSaysNothing()
    {
        var root = SnapshotComparerTests.Device(@"USB\ROOT_HUB30\1", "USB", "USB Root Hub (USB 3.0)");
        var anonymous = new DeviceNode(
            @"USB\VID_05E3&PID_0606\7&2A",
            "USB",
            "USB Composite Device",
            null,
            root.InstanceId,
            @"USB\Class_09&SubClass_00&Prot_02;USB\Class_09");
        var snapshot = SnapshotComparerTests.Snapshot(root, anonymous);

        var node = ContractV1.ToEnvelope(snapshot).Nodes
            .Single(item => item.Platform is not null && item.Platform["instanceId"] == anonymous.InstanceId);

        Assert.Equal(9, node.UsbClass);
        Assert.Equal("hub", node.Kind);
    }

    [Fact]
    public void WindowsHubCompatibleIdCountsAsTheHubClass()
    {
        // Windows reports "USB\USB20_HUB" here, never "USB\Class_09".
        var hub = new DeviceNode(
            @"USB\VID_1A40&PID_0101\5&C1BD4D0&0&2",
            "USB",
            "Generic USB Hub",
            null,
            null,
            @"USB\USB20_HUB");

        Assert.Equal(DeviceNode.UsbHubClass, hub.UsbClass);
    }

    [Fact]
    public void CompositeDeviceClassIsZeroNotMistakenForAHub()
    {
        var device = new DeviceNode(
            @"USB\VID_1038&PID_1612\5&1",
            "USB",
            "SteelSeries Apex 7",
            null,
            null,
            @"USB\DevClass_00&SubClass_00&Prot_00;USB\COMPOSITE");

        Assert.Equal(0, device.UsbClass);
    }

    // The parameter is int, not UsbLinkSpeed: xunit needs public test methods,
    // and a public signature cannot expose an internal type.
    [Theory]
    [InlineData((int)UsbLinkSpeed.Low, "usbLow", 1_500_000L)]
    [InlineData((int)UsbLinkSpeed.Full, "usbLow", 12_000_000L)]
    [InlineData((int)UsbLinkSpeed.High, "usb2", 480_000_000L)]
    [InlineData((int)UsbLinkSpeed.Super, "usb3", 5_000_000_000L)]
    [InlineData((int)UsbLinkSpeed.SuperPlus, "usb3", 10_000_000_000L)]
    public void NegotiatedSpeedBecomesProtocolAndRate(
        int negotiated,
        string protocol,
        long bitsPerSecond)
    {
        var device = SnapshotComparerTests.Device(@"USB\VID_046D&PID_C08A\X", "USB", "Some device")
            with { LinkSpeed = (UsbLinkSpeed)negotiated };
        var snapshot = SnapshotComparerTests.Snapshot(device);

        var node = ContractV1.ToEnvelope(snapshot).Nodes
            .Single(item => item.Platform is not null && item.Platform["instanceId"] == device.InstanceId);

        Assert.Equal(protocol, node.Protocol);
        Assert.Equal(bitsPerSecond, node.LinkBitsPerSecond);
    }

    [Fact]
    public void UnmeasuredLinksStaySilentRatherThanGuessing()
    {
        var device = SnapshotComparerTests.Device(@"USB\VID_046D&PID_C08A\X", "USB", "Some device");
        var snapshot = SnapshotComparerTests.Snapshot(device);

        var node = ContractV1.ToEnvelope(snapshot).Nodes
            .Single(item => item.Platform is not null && item.Platform["instanceId"] == device.InstanceId);

        Assert.Equal("unknown", node.Protocol);
        Assert.Null(node.LinkBitsPerSecond);
    }

    [Fact]
    public void KnowingTheSpeedStillDoesNotClaimAUsb4Tunnel()
    {
        // 10 Gbps is not evidence of tunneling; only USB4 router facts would be.
        var device = SnapshotComparerTests.Device(@"USB\VID_046D&PID_C08A\X", "USB", "Fast device")
            with { LinkSpeed = UsbLinkSpeed.SuperPlus };

        var node = ContractV1.ToEnvelope(SnapshotComparerTests.Snapshot(device)).Nodes
            .Single(item => item.Platform is not null && item.Platform["instanceId"] == device.InstanceId);

        Assert.Equal("usb3", node.Protocol);
        Assert.False(node.Tunneled);
    }

    [Fact]
    public void NothingClaimsToBeTunneledBecauseNothingHereCanProveIt()
    {
        var nodes = ContractV1.ToEnvelope(DockedSnapshot()).Nodes;

        Assert.All(nodes, node => Assert.False(node.Tunneled));
    }

    [Fact]
    public void DisplaysAreReportedUnknownRatherThanAbsent()
    {
        var envelope = ContractV1.ToEnvelope(DockedSnapshot());

        Assert.False(envelope.DisplaysKnown);
        Assert.Null(envelope.Displays);
    }

    [Fact]
    public void EveryRecordedKindIsEitherAContractEventKindOrDeliberatelyInternal()
    {
        // Recorded kinds either map to a Connection Contract v1 event kind, or
        // are listed here as internal evidence that must NOT reach /events —
        // inventing a kind the shared schema does not have would break the
        // other platform's reader. Adding a kind forces this decision.
        string[] internalOnly = [RecorderEntryKinds.DeficitDeepened];

        var recorded = typeof(RecorderEntryKinds)
            .GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(recorded);
        Assert.All(recorded, kind => Assert.True(
            ContractV1.EventKinds.ContainsKey(kind) || internalOnly.Contains(kind),
            $"RecorderEntryKinds.{kind} is neither a Connection Contract v1 event kind nor declared internal"));

        // And the internal ones really are filtered out of the served stream.
        Assert.All(internalOnly, kind =>
        {
            Assert.False(ContractV1.EventKinds.ContainsKey(kind));
            var stream = ContractV1.ToEventStream(
                [new RecorderEntry(DateTimeOffset.Now, kind, null, new PowerState(true, 90, -20000), null)]);
            Assert.Equal(string.Empty, stream);
        });
    }

    [Fact]
    public void EventStreamIsJsonlThatNamesTheDeviceAndItsNode()
    {
        var snapshot = DockedSnapshot();
        var mouse = snapshot.Devices.Single(device => device.ClassName == "Mouse");
        var entries = new[]
        {
            RecorderEntry.FullSnapshot(snapshot),
            new RecorderEntry(snapshot.CapturedAt, RecorderEntryKinds.DeviceDisappeared, mouse, snapshot.Power, null),
            new RecorderEntry(snapshot.CapturedAt, RecorderEntryKinds.DeficitStarted, null, snapshot.Power, null)
        };

        var lines = ContractV1.ToEventStream(entries)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        Assert.Equal(3, lines.Count);
        Assert.Equal("fullSnapshot", lines[0].GetProperty("kind").GetString());
        Assert.Equal(
            "connection-contract/v1",
            lines[0].GetProperty("snapshot").GetProperty("schema").GetString());

        Assert.Equal("deviceRemoved", lines[1].GetProperty("kind").GetString());
        Assert.Equal(mouse.VidPid, lines[1].GetProperty("vidPid").GetString());
        Assert.Equal(mouse.FriendlyName, lines[1].GetProperty("name").GetString());
        Assert.StartsWith("usb:", lines[1].GetProperty("nodeId").GetString());

        Assert.Equal("deficitStart", lines[2].GetProperty("kind").GetString());
        Assert.False(lines[2].TryGetProperty("nodeId", out _));
    }


    [Fact]
    public void BuiltInIsTheProducersClassificationAndEveryNodeIsStillExported()
    {
        // A built-in root hub feeding both an integrated touchpad and the
        // external dock branch: the dashboard filters on the flag; the envelope
        // keeps every node (dashboard-topology-controls, issues #42 #43).
        var root = new DeviceNode(
            @"USB\ROOT_HUB30\4&1",
            "USB",
            "USB Root Hub (USB 3.0)",
            "Microsoft",
            null,
            @"USB\Class_09");
        var touchpad = new DeviceNode(
            @"HID\SURFACE_TOUCHPAD\1",
            "HIDClass",
            "Surface Touchpad Device",
            "Microsoft",
            root.InstanceId,
            @"HID\Class_03");
        var dock = SnapshotComparerTests.Device(
            @"USB4\VID_045E&PID_0963\DOCK",
            "USB",
            "Surface Thunderbolt(TM) 4 Dock",
            root.InstanceId);
        var mouse = SnapshotComparerTests.Device(
            @"USB\VID_046D&PID_C08A\MOUSE",
            "Mouse",
            "MX Vertical",
            dock.InstanceId);

        var nodes = Export(SnapshotComparerTests.Snapshot(root, touchpad, dock, mouse))
            .GetProperty("nodes").EnumerateArray()
            .ToDictionary(node => node.GetProperty("id").GetString()!, node => node);

        // All four devices (plus the host root) are exported regardless of classification.
        Assert.Equal(5, nodes.Count);
        Assert.True(nodes.Single(pair => pair.Key.Contains("ROOT_HUB")).Value.GetProperty("builtIn").GetBoolean());
        Assert.True(nodes.Single(pair => pair.Key.Contains("TOUCHPAD")).Value.GetProperty("builtIn").GetBoolean());
        Assert.False(nodes.Single(pair => pair.Key.Contains("DOCK")).Value.GetProperty("builtIn").GetBoolean());
        Assert.False(nodes.Single(pair => pair.Key.Contains("MOUSE")).Value.GetProperty("builtIn").GetBoolean());
    }

    private static JsonElement Export(ConnectionSnapshot snapshot) =>
        JsonDocument.Parse(ContractV1.Serialize(ContractV1.ToEnvelope(snapshot))).RootElement;

    /// <summary>A Surface on a Thunderbolt dock: monitor, hub branch, and input.</summary>
    private static ConnectionSnapshot DockedSnapshot()
    {
        var dock = SnapshotComparerTests.Device(
            @"USB4\VID_045E&PID_0963\DOCK",
            "USB",
            "Surface Thunderbolt(TM) 4 Dock");
        var hub = new DeviceNode(
            @"USB\VID_043E&PID_9C04\HUB",
            "USB",
            "Generic USB Hub",
            "LG Electronics Inc.",
            dock.InstanceId,
            @"USB\Class_09&SubClass_00&Prot_02");
        var mouse = SnapshotComparerTests.Device(
            @"HID\VID_046D&PID_C08A\MOUSE",
            "Mouse",
            "HID-compliant mouse",
            hub.InstanceId);
        var monitor = SnapshotComparerTests.Device(
            @"DISPLAY\GSM77B3\1",
            "Monitor",
            "Generic Monitor (LG ULTRAWIDE)");

        return SnapshotComparerTests.Snapshot(dock, hub, mouse, monitor) with
        {
            Power = new PowerState(true, 87, 12000)
        };
    }
}
