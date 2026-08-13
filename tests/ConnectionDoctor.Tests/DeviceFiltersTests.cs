namespace ConnectionDoctor.Tests;

public sealed class DeviceFiltersTests
{
    [Fact]
    public void NetDeviceWithUsbAncestorIsIncluded()
    {
        var usbHub = Device(@"USB\VID_045E&PID_0900\HUB", "USB", "USB Hub");
        var nic = Device(@"USB\VID_045E&PID_085C\NIC", "Net", "USB 10/100/1G/2.5G LAN", usbHub.InstanceId);
        var allById = AllById(usbHub, nic);

        Assert.True(DeviceFilters.IsConnectionDevice(nic, allById));
    }

    [Fact]
    public void NetDeviceWithPciAncestorIsExcluded()
    {
        var pciRoot = Device(@"PCI\VEN_8086&DEV_0001\ROOT", "System", "PCI Root");
        var wifi = Device(@"PCI\VEN_8086&DEV_0002\WIFI", "Net", "Intel Wi-Fi 6E", pciRoot.InstanceId);
        var allById = AllById(pciRoot, wifi);

        Assert.False(DeviceFilters.IsConnectionDevice(wifi, allById));
    }

    [Fact]
    public void NetDeviceWithNoAncestorIsExcluded()
    {
        var nic = Device(@"NET\VID_045E&PID_085C\0", "Net", "Some NIC");
        var allById = AllById(nic);

        Assert.False(DeviceFilters.IsConnectionDevice(nic, allById));
    }

    [Fact]
    public void MediaDeviceWithUsbAncestorIsIncluded()
    {
        var usbHub = Device(@"USB\VID_045E&PID_0900\HUB", "USB", "USB Hub");
        var audio = Device(@"USB\VID_045E&PID_085B\AUDIO", "MEDIA", "Surface Thunderbolt 4 Dock Audio", usbHub.InstanceId);
        var allById = AllById(usbHub, audio);

        Assert.True(DeviceFilters.IsConnectionDevice(audio, allById));
    }

    [Fact]
    public void MediaDeviceWithPciAncestorIsExcluded()
    {
        var pciRoot = Device(@"PCI\VEN_8086&DEV_0001\ROOT", "System", "PCI Root");
        var onboardAudio = Device(@"PCI\VEN_8086&DEV_0003\AUDIO", "MEDIA", "High Definition Audio", pciRoot.InstanceId);
        var allById = AllById(pciRoot, onboardAudio);

        Assert.False(DeviceFilters.IsConnectionDevice(onboardAudio, allById));
    }

    [Fact]
    public void NetDeviceWithUsbAncestorTwoLevelsUpIsIncluded()
    {
        var usbHub = Device(@"USB\VID_045E&PID_0900\HUB", "USB", "USB Hub");
        var usbDevice = Device(@"USB\VID_045E&PID_0901\DEV", "USBDevice", "USB Composite Device", usbHub.InstanceId);
        var nic = Device(@"USB\VID_045E&PID_085C\NIC", "Net", "USB 2.5G LAN", usbDevice.InstanceId);
        var allById = AllById(usbHub, usbDevice, nic);

        Assert.True(DeviceFilters.IsConnectionDevice(nic, allById));
    }

    private static IReadOnlyDictionary<string, DeviceNode> AllById(params DeviceNode[] devices) =>
        devices.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);

    private static DeviceNode Device(string id, string className, string name, string? parentId = null) =>
        new(id, className, name, null, parentId);
}
