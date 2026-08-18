namespace ConnectionDoctor.Tests;

public sealed class DeviceFiltersTests
{
    [Fact]
    public void IncludesNetworkAndMediaDevicesOnlyWithUsbAncestors()
    {
        var usb = SnapshotComparerTests.Device(
            @"USB\ROOT_HUB30\1",
            "USB",
            "USB Root Hub");
        var dockEthernet = SnapshotComparerTests.Device(
            @"USB\VID_045E&PID_085C\LAN",
            "Net",
            "USB 10/100/1G/2.5G LAN",
            usb.InstanceId);
        var dockAudio = SnapshotComparerTests.Device(
            @"USB\VID_045E&PID_085B\AUDIO",
            "MEDIA",
            "Surface Dock Audio",
            usb.InstanceId);
        var pci = SnapshotComparerTests.Device(
            @"PCI\VEN_17CB&DEV_1103\WIFI",
            "System",
            "PCI Express Root");
        var wifi = SnapshotComparerTests.Device(
            @"PCI\VEN_17CB&DEV_1103\NET",
            "Net",
            "Qualcomm Wi-Fi",
            pci.InstanceId);
        var snapshot = SnapshotComparerTests.Snapshot(usb, dockEthernet, dockAudio, pci, wifi);

        var devices = DeviceFilters.ConnectionDevices(snapshot);

        Assert.Contains(dockEthernet, devices);
        Assert.Contains(dockAudio, devices);
        Assert.DoesNotContain(wifi, devices);
    }

    [Fact]
    public void ExternalViewHidesBuiltInSurfaceDevicesAndKeepsUsbBranches()
    {
        var internalKeyboard = SnapshotComparerTests.Device(
            @"HID\VID_045E&PID_0001\KEYBOARD",
            "Keyboard",
            "Surface HID Keyboard",
            @"ACPI\MSHW0001\1");
        var internalPanel = SnapshotComparerTests.Device(
            @"DISPLAY\LGD0780\1",
            "Monitor",
            "Surface Calibrated Panel");
        var rootHub = SnapshotComparerTests.Device(
            @"USB\ROOT_HUB30\1",
            "USB",
            "USB Root Hub (USB 3.0)");
        var externalHub = SnapshotComparerTests.Device(
            @"USB\VID_043E&PID_9C04\HUB",
            "USB",
            "Generic USB Hub",
            rootHub.InstanceId);
        var externalMouse = SnapshotComparerTests.Device(
            @"HID\VID_046D&PID_C08A\MOUSE",
            "Mouse",
            "HID-compliant mouse",
            externalHub.InstanceId);
        var externalMonitor = SnapshotComparerTests.Device(
            @"DISPLAY\GSM77B3\1",
            "Monitor",
            "Generic Monitor (LG ULTRAWIDE)");
        var snapshot = SnapshotComparerTests.Snapshot(
            internalKeyboard,
            internalPanel,
            rootHub,
            externalHub,
            externalMouse,
            externalMonitor);

        var external = DeviceFilters.VisibleConnectionDevices(snapshot, includeBuiltIn: false);
        var all = DeviceFilters.VisibleConnectionDevices(snapshot, includeBuiltIn: true);

        Assert.DoesNotContain(internalKeyboard, external);
        Assert.DoesNotContain(internalPanel, external);
        Assert.DoesNotContain(rootHub, external);
        Assert.Contains(externalHub, external);
        Assert.Contains(externalMouse, external);
        Assert.Contains(externalMonitor, external);
        Assert.Contains(internalKeyboard, all);
        Assert.Contains(internalPanel, all);
    }
    // Issue #14. The markers were right on a Surface and wrong nearly
    // everywhere else, in both directions at once.
    [Fact]
    public void WindowsOwnAnswerBeatsTheNameOnBuiltInPanels()
    {
        var byId = new Dictionary<string, DeviceNode>(StringComparer.OrdinalIgnoreCase);

        // The case that made this issue: most laptops enumerate their own
        // panel as "Generic PnP Monitor", which matches no marker, so the
        // panel was shown even with built-ins hidden.
        var genericPanel = SnapshotComparerTests.Device(
            @"DISPLAY\GPN0001\4&1a2b3c&0&UID4353", "Monitor", "Generic PnP Monitor",
            embeddedPanel: true);
        Assert.False(DeviceFilters.IsExternalDevice(genericPanel, byId));

        // And the other direction: an external monitor whose marketing name
        // contains a marker was silently hidden.
        var externalWithMarker = SnapshotComparerTests.Device(
            @"DISPLAY\GSM5B09\5&2b3c4d&0&UID4354", "Monitor", "LG UltraFine Integrated Hub Display",
            embeddedPanel: false);
        Assert.True(DeviceFilters.IsExternalDevice(externalWithMarker, byId));
    }

    [Fact]
    public void AMonitorWindowsDidNotReportOnFallsBackToTheNameRatherThanGuessing()
    {
        var byId = new Dictionary<string, DeviceNode>(StringComparer.OrdinalIgnoreCase);

        // QueryDisplayConfig covers active targets only, so a powered-off or
        // unplugged monitor is absent from it. Absent must not read as
        // "external" — it means no opinion, and the old heuristic answers.
        var unknownSurface = SnapshotComparerTests.Device(
            @"DISPLAY\SUR0001\4&1a2b3c&0&UID4355", "Monitor", "Surface Display");
        Assert.False(DeviceFilters.IsExternalDevice(unknownSurface, byId));

        var unknownExternal = SnapshotComparerTests.Device(
            @"DISPLAY\DEL4321\4&1a2b3c&0&UID4356", "Monitor", "DELL U2723QE");
        Assert.True(DeviceFilters.IsExternalDevice(unknownExternal, byId));
    }

    [Theory]
    // The device path and the instance id are the same three fields in
    // different punctuation.
    [InlineData(@"\\?\DISPLAY#GSM5B09#5&1a2b3c&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
                @"DISPLAY\GSM5B09\5&1a2b3c&0&UID4353")]
    [InlineData(@"DISPLAY#GPN0001#4&1a2b&0&UID256", @"DISPLAY\GPN0001\4&1a2b&0&UID256")]
    // Anything not that shape returns null — no opinion — rather than a
    // correlation that could hide someone's external monitor.
    [InlineData(@"\\?\DISPLAY#GSM5B09#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ADevicePathCorrelatesToAnInstanceIdOrToNothing(string? path, string? expected) =>
        Assert.Equal(expected, DisplayConfig.InstanceIdFromDevicePath(path));

    [Theory]
    [InlineData(0x80000000u, true)]   // INTERNAL
    [InlineData(4u, true)]            // DISPLAYPORT_EMBEDDED
    [InlineData(6u, true)]            // UDI_EMBEDDED
    [InlineData(5u, false)]           // UDI_EXTERNAL
    [InlineData(10u, false)]          // DISPLAYPORT_EXTERNAL
    public void OnlyEmbeddedOutputTechnologiesAreThePanel(uint technology, bool expected) =>
        Assert.Equal(expected, DisplayConfig.IsEmbedded(technology));

}
