using System.Globalization;
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
    string? ParentInstanceId,
    string? CompatibleIds = null,
    int? Address = null,
    UsbLinkSpeed LinkSpeed = UsbLinkSpeed.Unknown)
{
    /// <summary>bDeviceClass 9 - a hub even when the friendly name says nothing.</summary>
    public const int UsbHubClass = 9;

    /// <summary>
    /// The device's own serial, when it reports one. Windows puts it in the
    /// third segment of the instance id — `USB\VID_045E&amp;PID_0963\0123456789` —
    /// but only when the device actually supplies one; otherwise that segment
    /// is a bus-generated path such as `6&amp;1a2b3c4d&amp;0&amp;2`, which describes where
    /// it is plugged in rather than which unit it is. A leading '&' (or an
    /// embedded '&' path form) means generated, so it is not a serial.
    /// </summary>
    public string? Serial
    {
        get
        {
            var segments = InstanceId.Split('\\');
            if (segments.Length < 3)
            {
                return null;
            }

            var candidate = segments[^1];
            return candidate.Length == 0 || candidate.Contains('&') ? null : candidate;
        }
    }

    private static readonly Regex VidPidPattern = new(
        @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Windows reports bDeviceClass in the compatible IDs: "USB\Class_09&..."
    // for a plain device, "USB\DevClass_00&..." for a composite one.
    private static readonly Regex UsbClassPattern = new(
        @"(?:Dev)?Class_([0-9A-F]{2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Windows describes hubs as "USB\USB20_HUB" / "USB\USB30_HUB" rather than
    // "USB\Class_09", but a hub's bDeviceClass is 9 by specification.
    private static readonly Regex HubCompatiblePattern = new(
        @"USB[0-9]{2}_HUB|ROOT_HUB",
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

    /// <summary>bDeviceClass as reported by the bus, or null when unknown.</summary>
    public int? UsbClass
    {
        get
        {
            if (CompatibleIds is null)
            {
                return null;
            }

            var match = UsbClassPattern.Match(CompatibleIds);
            if (match.Success &&
                int.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return value;
            }

            return HubCompatiblePattern.IsMatch(CompatibleIds) ? UsbHubClass : null;
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

/// <summary>
/// A diagnosis with the evidence that produced it. Evidence is mandatory and
/// non-empty by contract (docs/schema-v1.md): a verdict you cannot audit is
/// an opinion. Severity is one of info | warning | critical.
/// </summary>
internal sealed record Finding(
    string Severity,
    string Title,
    string Explanation,
    string Recommendation,
    IReadOnlyList<string> Evidence,
    string? Confidence = null);

internal sealed record ComparisonReport(
    IReadOnlyList<DeviceNode> Missing,
    IReadOnlyList<DeviceNode> Added,
    IReadOnlyList<Finding> Findings,
    /// <summary>
    /// Instance ids of missing devices that the findings above actually
    /// account for. Anything missing and not in here still needs saying —
    /// a power finding does not explain a vanished dock.
    /// </summary>
    IReadOnlySet<string>? ExplainedMissing = null)
{
    public IReadOnlyList<DeviceNode> UnexplainedMissing =>
        Missing.Where(device => ExplainedMissing is null || !ExplainedMissing.Contains(device.InstanceId)).ToList();
}

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

    public static IReadOnlyList<DeviceNode> VisibleConnectionDevices(
        ConnectionSnapshot snapshot,
        bool includeBuiltIn)
    {
        var byId = snapshot.Devices.ToDictionary(device => device.InstanceId, StringComparer.OrdinalIgnoreCase);
        return snapshot.Devices
            .Where(device =>
                IsConnectionDevice(device, byId) &&
                (includeBuiltIn || IsExternalDevice(device, byId)))
            .ToList();
    }

    /// <summary>
    /// The devices that make up the topology: everything visible, plus every
    /// ancestor needed to connect them. `tree` and the Connection Contract v1
    /// export share this so their hierarchies cannot drift apart.
    /// </summary>
    public static IReadOnlyList<DeviceNode> TopologyDevices(
        ConnectionSnapshot snapshot,
        bool includeBuiltIn)
    {
        var byId = snapshot.Devices.ToDictionary(device => device.InstanceId, StringComparer.OrdinalIgnoreCase);
        var visible = VisibleConnectionDevices(snapshot, includeBuiltIn);
        var includedIds = new HashSet<string>(
            visible.Select(device => device.InstanceId),
            StringComparer.OrdinalIgnoreCase);

        foreach (var device in visible)
        {
            var parentId = device.ParentInstanceId;
            while (parentId is not null && byId.TryGetValue(parentId, out var parent))
            {
                if (IsConnectionDevice(parent, byId) &&
                    (includeBuiltIn || IsExternalDevice(parent, byId)))
                {
                    includedIds.Add(parent.InstanceId);
                }

                parentId = parent.ParentInstanceId;
            }
        }

        return snapshot.Devices.Where(device => includedIds.Contains(device.InstanceId)).ToList();
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

    public static bool IsExternalDevice(
        DeviceNode device,
        IReadOnlyDictionary<string, DeviceNode> devicesById)
    {
        if (device.ClassName.Equals("Monitor", StringComparison.OrdinalIgnoreCase))
        {
            return !LooksLikeBuiltInDisplay(device);
        }

        var current = device;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.InstanceId))
        {
            if (IsExternalBusNode(current.InstanceId))
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

    private static bool IsExternalBusNode(string instanceId) =>
        instanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) ||
        instanceId.StartsWith(@"USB4\VID_", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeBuiltInDisplay(DeviceNode device)
    {
        string[] markers = ["Surface", "Internal", "Integrated", "Built-in"];
        return markers.Any(marker =>
            device.FriendlyName.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
