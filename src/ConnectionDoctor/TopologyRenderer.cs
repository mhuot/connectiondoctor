namespace ConnectionDoctor;

internal static class TopologyRenderer
{
    public static void Write(ConnectionSnapshot snapshot, TextWriter writer)
    {
        writer.WriteLine($"ConnectionDoctor tree - {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}");
        writer.WriteLine($"{snapshot.HostName} [{(snapshot.Power.LineOnline ? "AC" : "battery")}, {snapshot.Power.BatteryPercent}%]");

        var interesting = snapshot.Devices.Where(DeviceFilters.IsConnectionDevice).ToList();
        var byId = snapshot.Devices.ToDictionary(device => device.InstanceId, StringComparer.OrdinalIgnoreCase);
        var includedIds = new HashSet<string>(interesting.Select(device => device.InstanceId), StringComparer.OrdinalIgnoreCase);

        foreach (var device in interesting)
        {
            var parentId = device.ParentInstanceId;
            while (parentId is not null && byId.TryGetValue(parentId, out var parent))
            {
                if (DeviceFilters.IsConnectionDevice(parent))
                {
                    includedIds.Add(parent.InstanceId);
                }
                parentId = parent.ParentInstanceId;
            }
        }

        var included = snapshot.Devices.Where(device => includedIds.Contains(device.InstanceId)).ToList();
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
        writer.WriteLine($"{prefix}{connector}{node.FriendlyName} ({node.ClassName}){id}");

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
