using System.Text.RegularExpressions;

namespace ConnectionDoctor;

internal sealed record ConnectionSnapshot(
    DateTimeOffset CapturedAt,
    string HostName,
    string OperatingSystemArchitecture,
    PowerState Power,
    IReadOnlyList<DeviceNode> Devices);

internal sealed record PowerState(bool LineOnline, int BatteryPercent);

internal sealed record DeviceNode(
    string InstanceId,
    string ClassName,
    string FriendlyName,
    string? Manufacturer,
    string? ParentInstanceId)
{
    private static readonly Regex VidPidPattern = new(
        @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string? VidPid
    {
        get
        {
            var match = VidPidPattern.Match(InstanceId);
            return match.Success
                ? $"{match.Groups[1].Value.ToUpperInvariant()}:{match.Groups[2].Value.ToUpperInvariant()}"
                : null;
        }
    }

    public string HardwareId
    {
        get
        {
            var parts = InstanceId.Split('\\');
            return parts.Length >= 2 ? $"{parts[0]}\\{parts[1]}".ToUpperInvariant() : InstanceId.ToUpperInvariant();
        }
    }

    public string StableId => $"{ClassName}|{HardwareId}|{FriendlyName}".ToUpperInvariant();
}

internal sealed record Finding(string Severity, string Title, string Explanation, string Recommendation);

internal sealed record ComparisonReport(
    IReadOnlyList<DeviceNode> Missing,
    IReadOnlyList<DeviceNode> Added,
    IReadOnlyList<Finding> Findings);

internal static class DeviceFilters
{
    private static readonly HashSet<string> ConnectionClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "USB", "USBDevice", "HIDClass", "Keyboard", "Mouse", "Monitor", "Firmware"
    };

    private static readonly HashSet<string> UsbAncestorClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "USB", "USBDevice"
    };

    private static readonly HashSet<string> UsbAncestorRequiredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Net", "MEDIA"
    };

    public static bool IsConnectionDevice(DeviceNode device, IReadOnlyDictionary<string, DeviceNode> allById) =>
        ConnectionClasses.Contains(device.ClassName) ||
        HasUsbAncestorForClass(device, allById) ||
        device.FriendlyName.Contains("USB4", StringComparison.OrdinalIgnoreCase) ||
        device.FriendlyName.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase) ||
        device.FriendlyName.Contains("Type-C", StringComparison.OrdinalIgnoreCase);

    private static bool HasUsbAncestorForClass(DeviceNode device, IReadOnlyDictionary<string, DeviceNode> allById)
    {
        if (!UsbAncestorRequiredClasses.Contains(device.ClassName))
        {
            return false;
        }

        var parentId = device.ParentInstanceId;
        while (parentId is not null && allById.TryGetValue(parentId, out var parent))
        {
            if (UsbAncestorClasses.Contains(parent.ClassName))
            {
                return true;
            }

            parentId = parent.ParentInstanceId;
        }

        return false;
    }
}
