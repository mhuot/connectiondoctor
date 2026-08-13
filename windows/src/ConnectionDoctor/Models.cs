using System.Text.RegularExpressions;

namespace ConnectionDoctor;

internal sealed record ConnectionSnapshot(
    DateTimeOffset CapturedAt,
    string HostName,
    string OperatingSystemArchitecture,
    PowerState Power,
    IReadOnlyList<DeviceNode> Devices);

internal sealed record PowerState(bool LineOnline, int BatteryPercent, int? BatteryRateMilliwatts)
{
    public const int DeficitThresholdMilliwatts = 2000;
    public bool IsDeficit => LineOnline && BatteryRateMilliwatts <= -DeficitThresholdMilliwatts;
}

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
    private static readonly HashSet<string> DirectConnectionClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "USB", "USBDevice", "HIDClass", "Keyboard", "Mouse", "Monitor"
    };

    public static IReadOnlyList<DeviceNode> ConnectionDevices(ConnectionSnapshot snapshot)
    {
        var byId = snapshot.Devices.ToDictionary(device => device.InstanceId, StringComparer.OrdinalIgnoreCase);
        return snapshot.Devices.Where(device => IsConnectionDevice(device, byId)).ToList();
    }

    public static bool IsConnectionDevice(
        DeviceNode device,
        IReadOnlyDictionary<string, DeviceNode> devicesById)
    {
        if (DirectConnectionClasses.Contains(device.ClassName) ||
            HasConnectionName(device))
        {
            return true;
        }

        return (device.ClassName.Equals("Net", StringComparison.OrdinalIgnoreCase) ||
                device.ClassName.Equals("MEDIA", StringComparison.OrdinalIgnoreCase)) &&
               HasUsbAncestor(device, devicesById);
    }

    private static bool HasUsbAncestor(
        DeviceNode device,
        IReadOnlyDictionary<string, DeviceNode> devicesById)
    {
        var current = device;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.InstanceId))
        {
            if (current.InstanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
                current.ClassName.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                current.ClassName.Equals("USBDevice", StringComparison.OrdinalIgnoreCase) ||
                HasConnectionName(current))
            {
                return true;
            }

            if (current.ParentInstanceId is null ||
                !devicesById.TryGetValue(current.ParentInstanceId, out current!))
            {
                return false;
            }
        }

        return false;
    }

    private static bool HasConnectionName(DeviceNode device) =>
        device.FriendlyName.Contains("USB4", StringComparison.OrdinalIgnoreCase) ||
        device.FriendlyName.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase) ||
        device.FriendlyName.Contains("Type-C", StringComparison.OrdinalIgnoreCase);
}
