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
}
