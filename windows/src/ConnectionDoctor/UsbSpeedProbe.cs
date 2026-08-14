using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ConnectionDoctor;

/// <summary>Negotiated link speed, as the bus reports it — not as a name implies.</summary>
internal enum UsbLinkSpeed
{
    Unknown = -1,
    Low = 0,
    Full = 1,
    High = 2,
    Super = 3,
    SuperPlus = 4
}

/// <summary>
/// Asks each hub what speed its ports actually negotiated.
///
/// SetupAPI exposes no speed property, so the only honest source is the hub
/// itself: IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX per port, plus the V2
/// form to separate SuperSpeed from SuperSpeed+. A device's port number comes
/// from SPDRP_ADDRESS, so each child is matched to its own port rather than
/// inheriting a guess.
///
/// Hub interface handles open without elevation. Anything that fails stays
/// Unknown — a wrong speed is worse than an absent one.
/// </summary>
internal static class UsbSpeedProbe
{
    private static readonly Guid UsbHubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private const uint IoctlGetNodeInformation = 0x220408;
    private const uint IoctlGetNodeConnectionInformationEx = 0x220448;
    private const uint IoctlGetNodeConnectionInformationExV2 = 0x22045C;

    // USB_NODE_CONNECTION_INFORMATION_EX, packed: ConnectionIndex(4) then an
    // 18-byte device descriptor, config value, speed, hub flag, address...
    private const int SpeedOffset = 23;
    private const int ConnectionStatusOffset = 31;
    private const int ConnectionInfoSize = 512;
    private const uint DeviceConnected = 1;

    // USB_NODE_INFORMATION: NodeType(4) then a packed USB_HUB_DESCRIPTOR whose
    // third byte is the port count.
    private const int PortCountOffset = 6;
    private const int NodeInformationSize = 96;

    // USB_NODE_CONNECTION_INFORMATION_EX_V2_FLAGS
    private const uint OperatingAtSuperSpeedPlusOrHigher = 0x04;

    /// <summary>Returns the devices with LinkSpeed filled in where knowable.</summary>
    public static IReadOnlyList<DeviceNode> WithLinkSpeeds(IReadOnlyList<DeviceNode> devices)
    {
        Dictionary<string, string> hubs;
        try
        {
            hubs = EnumerateHubs();
        }
        catch (DllNotFoundException)
        {
            return devices;
        }

        if (hubs.Count == 0)
        {
            return devices;
        }

        var speeds = new Dictionary<string, UsbLinkSpeed>(StringComparer.OrdinalIgnoreCase);

        // One handle per hub, reused for every child hanging off it.
        foreach (var (hubInstanceId, hubPath) in hubs)
        {
            var children = devices
                .Where(device =>
                    device.Address is not null &&
                    hubInstanceId.Equals(device.ParentInstanceId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (children.Count == 0)
            {
                continue;
            }

            using var handle = CreateFileW(hubPath, GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                continue;
            }

            var portCount = ReadPortCount(handle);
            foreach (var child in children)
            {
                var port = child.Address!.Value;
                if (port < 1 || (portCount > 0 && port > portCount))
                {
                    continue;
                }

                var speed = ReadPortSpeed(handle, (uint)port);
                if (speed != UsbLinkSpeed.Unknown)
                {
                    speeds[child.InstanceId] = speed;
                }
            }
        }

        return devices
            .Select(device => device with { LinkSpeed = Resolve(device, devices, hubs, speeds) })
            .ToList();
    }

    /// <summary>
    /// A device's own port answer, or its parent's when the parent is not a hub:
    /// the interfaces of a composite device share that device's physical link.
    /// Never inherited across a hub, where a slow device on a fast hub is
    /// exactly the case that would produce a confident wrong answer.
    /// </summary>
    private static UsbLinkSpeed Resolve(
        DeviceNode device,
        IReadOnlyList<DeviceNode> devices,
        IReadOnlyDictionary<string, string> hubs,
        IReadOnlyDictionary<string, UsbLinkSpeed> speeds)
    {
        if (speeds.TryGetValue(device.InstanceId, out var own))
        {
            return own;
        }

        var byId = devices.ToDictionary(item => item.InstanceId, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentId = device.ParentInstanceId;

        while (parentId is not null && visited.Add(parentId) && !hubs.ContainsKey(parentId))
        {
            if (speeds.TryGetValue(parentId, out var inherited))
            {
                return inherited;
            }

            if (!byId.TryGetValue(parentId, out var parent))
            {
                break;
            }

            parentId = parent.ParentInstanceId;
        }

        return UsbLinkSpeed.Unknown;
    }

    private static Dictionary<string, string> EnumerateHubs()
    {
        var hubs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var guid = UsbHubInterface;
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1))
        {
            return hubs;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>()
                };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref interfaceData))
                {
                    break;
                }

                var info = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                var buffer = new byte[1024];

                // cbSize is the struct's own size (8 on x64, 6 on x86), while the
                // path always begins at offset 4.
                BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailW(
                        set, ref interfaceData, buffer, (uint)buffer.Length, out _, ref info))
                {
                    continue;
                }

                var path = ReadPath(buffer);
                if (path.Length == 0)
                {
                    continue;
                }

                var instanceId = new StringBuilder(1024);
                if (SetupDiGetDeviceInstanceIdW(set, ref info, instanceId, instanceId.Capacity, out _))
                {
                    hubs[instanceId.ToString()] = path;
                }
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(set);
        }

        return hubs;
    }

    private static string ReadPath(byte[] buffer)
    {
        var text = Encoding.Unicode.GetString(buffer, 4, buffer.Length - 4);
        var terminator = text.IndexOf('\0');
        return terminator >= 0 ? text[..terminator] : text;
    }

    private static int ReadPortCount(SafeFileHandle handle)
    {
        var buffer = new byte[NodeInformationSize];
        return DeviceIoControl(
            handle, IoctlGetNodeInformation, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero)
            ? buffer[PortCountOffset]
            : 0;
    }

    private static UsbLinkSpeed ReadPortSpeed(SafeFileHandle handle, uint port)
    {
        var buffer = new byte[ConnectionInfoSize];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), port);

        if (!DeviceIoControl(
                handle, IoctlGetNodeConnectionInformationEx,
                buffer, buffer.Length, buffer, buffer.Length, out var returned, IntPtr.Zero) ||
            returned < ConnectionStatusOffset + 4)
        {
            return UsbLinkSpeed.Unknown;
        }

        if (BitConverter.ToUInt32(buffer, ConnectionStatusOffset) != DeviceConnected)
        {
            return UsbLinkSpeed.Unknown;
        }

        var speed = buffer[SpeedOffset] switch
        {
            0 => UsbLinkSpeed.Low,
            1 => UsbLinkSpeed.Full,
            2 => UsbLinkSpeed.High,
            3 => UsbLinkSpeed.Super,
            _ => UsbLinkSpeed.Unknown
        };

        // The base IOCTL reports everything above high speed as "super"; only V2
        // separates 5 Gbps from 10 and above.
        return speed == UsbLinkSpeed.Super ? RefineSuperSpeed(handle, port) : speed;
    }

    private static UsbLinkSpeed RefineSuperSpeed(SafeFileHandle handle, uint port)
    {
        // USB_NODE_CONNECTION_INFORMATION_EX_V2: index, length, protocols, flags.
        var buffer = new byte[16];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), port);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), buffer.Length);

        if (!DeviceIoControl(
                handle, IoctlGetNodeConnectionInformationExV2,
                buffer, buffer.Length, buffer, buffer.Length, out var returned, IntPtr.Zero) ||
            returned < buffer.Length)
        {
            return UsbLinkSpeed.Super;
        }

        var flags = BitConverter.ToUInt32(buffer, 12);
        return (flags & OperatingAtSuperSpeedPlusOrHigher) != 0
            ? UsbLinkSpeed.SuperPlus
            : UsbLinkSpeed.Super;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid, IntPtr enumerator, IntPtr parentWindow, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        [Out] byte[] detailData,
        uint detailDataSize,
        out uint requiredSize,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        [In] byte[] inBuffer,
        int inBufferSize,
        [Out] byte[] outBuffer,
        int outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
