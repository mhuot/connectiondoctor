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

internal sealed record WatchEvent(
    DateTimeOffset Timestamp,
    string Kind,
    IReadOnlyList<DeviceNode> DevicesAdded,
    IReadOnlyList<DeviceNode> DevicesRemoved,
    PowerState Power);

internal sealed record Incident(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<DeviceNode> Lost,
    IReadOnlyList<DeviceNode> Gained,
    PowerState PowerAtStart)
{
    public TimeSpan Duration => EndedAt - StartedAt;
}

internal static class DeviceFilters
{
    private static readonly HashSet<string> ConnectionClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "USB", "USBDevice", "HIDClass", "Keyboard", "Mouse", "Monitor", "Firmware"
    };

    public static bool IsConnectionDevice(DeviceNode device) =>
        ConnectionClasses.Contains(device.ClassName) ||
        device.FriendlyName.Contains("USB4", StringComparison.OrdinalIgnoreCase) ||
        device.FriendlyName.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase) ||
        device.FriendlyName.Contains("Type-C", StringComparison.OrdinalIgnoreCase);
}
