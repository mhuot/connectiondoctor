using System.Runtime.InteropServices;

namespace ConnectionDoctor;

/// <summary>
/// Which monitors are the machine's own panel, asked of Windows rather than
/// guessed from the name.
///
/// The name heuristic it replaces read "Surface", "Internal", "Integrated" or
/// "Built-in" out of the friendly name. That is right on a Surface and wrong
/// on most other laptops, where the built-in panel enumerates as "Generic PnP
/// Monitor" and therefore looked external — and it is wrong in the other
/// direction for any external monitor with "Integrated" in its marketing
/// name, which would vanish from the view. `QueryDisplayConfig` reports the
/// output technology of each active target, and an embedded panel says so.
///
/// The answer is deliberately three-valued. A monitor Windows describes as
/// embedded is built-in, one it describes as anything else is external, and a
/// monitor this never saw is *unknown* — the display config only covers
/// active targets, so a powered-off or unplugged monitor is absent rather
/// than external. Unknown falls back to the old name markers, which is a
/// worse answer but a better one than inventing a verdict.
/// </summary>
internal static class DisplayConfig
{
    /// <summary>
    /// Instance id → is it an embedded panel. Absent means Windows did not
    /// report on it, not that it is external. Empty when the query fails:
    /// display-layer cosmetics must never take the app down, so every failure
    /// path here means "no opinion" and the caller falls back.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> EmbeddedPanels()
    {
        var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
            {
                return found;
            }

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            {
                return found;
            }

            for (var i = 0; i < pathCount; i++)
            {
                var request = new DisplayConfigTargetDeviceName
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = DisplayConfigDeviceInfoGetTargetName,
                        Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                        AdapterId = paths[i].TargetInfo.AdapterId,
                        Id = paths[i].TargetInfo.Id
                    }
                };

                if (DisplayConfigGetDeviceName(ref request) != 0)
                {
                    continue;
                }

                var instanceId = InstanceIdFromDevicePath(request.MonitorDevicePath);
                if (instanceId is not null)
                {
                    found[instanceId] = IsEmbedded(request.OutputTechnology);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                          and not StackOverflowException)
        {
            // Deliberately broad. This is display-layer cosmetics sitting in
            // the middle of device enumeration, so anything it throws — a
            // missing API on an older Windows, a marshalling mistake in the
            // struct layouts below, an unexpected shape from a driver — would
            // otherwise take out the whole snapshot and with it the diagnosis
            // this tool exists to produce. Losing the answer to "which panel
            // is built in" costs a checkbox; losing the device list costs
            // everything. Reported once so a wrong layout is findable rather
            // than silent.
            Report(exception);
        }

        return found;
    }

    /// <summary>
    /// The three output technologies that mean "this panel is part of the
    /// machine". Embedded DisplayPort and embedded UDI are how most laptops
    /// report; INTERNAL is the older/simpler answer.
    /// </summary>
    internal static bool IsEmbedded(uint outputTechnology) =>
        outputTechnology is OutputTechnologyInternal
            or OutputTechnologyDisplayPortEmbedded
            or OutputTechnologyUdiEmbedded;

    /// <summary>
    /// A monitor's device path and its device instance id are the same three
    /// fields in different punctuation:
    /// <c>\\?\DISPLAY#GSM5B09#5&amp;1a2b3c&amp;0&amp;UID4353#{guid}</c> is
    /// <c>DISPLAY\GSM5B09\5&amp;1a2b3c&amp;0&amp;UID4353</c>. Rebuilt rather than
    /// pattern-matched so a path shaped differently than expected returns null
    /// — "no opinion" — instead of a wrong correlation that would hide
    /// somebody's external monitor.
    /// </summary>
    internal static string? InstanceIdFromDevicePath(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath))
        {
            return null;
        }

        var trimmed = devicePath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? devicePath[4..]
            : devicePath;

        // Drop the interface GUID, which the instance id does not carry.
        var guid = trimmed.IndexOf('{');
        if (guid >= 0)
        {
            trimmed = trimmed[..guid].TrimEnd('#');
        }

        var segments = trimmed.Split('#');
        return segments.Length == 3 && segments.All(segment => segment.Length > 0)
            ? string.Join('\\', segments)
            : null;
    }

    private static bool reported;

    /// <summary>Say it once: a machine where this always fails should not fill the log with it.</summary>
    private static void Report(Exception exception)
    {
        if (reported)
        {
            return;
        }

        reported = true;
        try
        {
            Console.Error.WriteLine(
                $"ConnectionDoctor: could not read the display configuration ({exception.Message}) — "
                + "built-in panel detection falls back to device names");
        }
        catch (IOException)
        {
        }
    }

    private const uint QdcOnlyActivePaths = 2;
    private const uint DisplayConfigDeviceInfoGetTargetName = 2;
    private const uint OutputTechnologyInternal = 0x80000000;
    private const uint OutputTechnologyDisplayPortEmbedded = 4;
    private const uint OutputTechnologyUdiEmbedded = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public ulong RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] DisplayConfigPathInfo[] paths,
        ref uint modeCount,
        [Out] DisplayConfigModeInfo[] modes,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceName(ref DisplayConfigTargetDeviceName request);
}
