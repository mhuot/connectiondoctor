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
}
