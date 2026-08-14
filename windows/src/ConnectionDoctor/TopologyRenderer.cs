using System.IO;

namespace ConnectionDoctor;

internal static class TopologyRenderer
{
    public static void Write(
        ConnectionSnapshot snapshot,
        TextWriter writer,
        bool includeBuiltIn = true)
    {
        writer.WriteLine($"ConnectionDoctor tree - {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}");
        writer.WriteLine($"{snapshot.HostName} [{(snapshot.Power.LineOnline ? "AC" : "battery")}, {snapshot.Power.BatteryPercent}%]");

        var included = DeviceFilters.TopologyDevices(snapshot, includeBuiltIn);
        var includedIds = new HashSet<string>(
            included.Select(device => device.InstanceId),
            StringComparer.OrdinalIgnoreCase);
        var children = included
            .Where(device => device.ParentInstanceId is not null && includedIds.Contains(device.ParentInstanceId))
            .GroupBy(device => device.ParentInstanceId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(child => child.FriendlyName).ToList(), StringComparer.OrdinalIgnoreCase);
        var roots = included
            .Where(device => device.ParentInstanceId is null || !includedIds.Contains(device.ParentInstanceId))
            .OrderBy(device => device.ClassName)
            .ThenBy(device => device.FriendlyName)
            .ToList();

        foreach (var root in roots)
        {
            WriteNode(root, string.Empty, true, true, children, writer);
        }
    }

    private static string Describe(UsbLinkSpeed speed) => speed switch
    {
        UsbLinkSpeed.Low => " 1.5Mb",
        UsbLinkSpeed.Full => " 12Mb",
        UsbLinkSpeed.High => " 480Mb",
        UsbLinkSpeed.Super => " 5Gb",
        UsbLinkSpeed.SuperPlus => " 10Gb",
        _ => string.Empty
    };

    private static void WriteNode(
        DeviceNode node,
        string prefix,
        bool isLast,
        bool isRoot,
        IReadOnlyDictionary<string, List<DeviceNode>> children,
        TextWriter writer)
    {
        var connector = isRoot ? string.Empty : isLast ? "└── " : "├── ";
        var id = node.VidPid is null ? string.Empty : $" [{node.VidPid}]";
        var speed = Describe(node.LinkSpeed);
        writer.WriteLine($"{prefix}{connector}{node.FriendlyName} ({node.ClassName}){id}{speed}");

        if (!children.TryGetValue(node.InstanceId, out var childNodes))
        {
            return;
        }

        var childPrefix = isRoot ? string.Empty : prefix + (isLast ? "    " : "│   ");
        for (var index = 0; index < childNodes.Count; index++)
        {
            WriteNode(childNodes[index], childPrefix, index == childNodes.Count - 1, false, children, writer);
        }
    }
}
