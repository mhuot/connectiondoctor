using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace ConnectionDoctor;

internal static class DeviceProbe
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpManufacturer = 0x0000000B;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint SpdrpClass = 0x00000007;
    private const uint SpdrpCompatibleIds = 0x00000002;
    private const uint CrSuccess = 0;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static ConnectionSnapshot Capture()
    {
        var devices = EnumeratePresentDevices();
        return new ConnectionSnapshot(
            DateTimeOffset.Now,
            Environment.MachineName,
            RuntimeInformation.OSArchitecture.ToString(),
            ReadPowerState(),
            devices);
    }

    private static IReadOnlyList<DeviceNode> EnumeratePresentDevices()
    {
        var deviceInfoSet = SetupDiGetClassDevsW(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfAllClasses);

        if (deviceInfoSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate present devices.");
        }

        try
        {
            var devices = new List<DeviceNode>();
            for (uint index = 0; ; index++)
            {
                var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref data))
                {
                    const int noMoreItems = 259;
                    var error = Marshal.GetLastWin32Error();
                    if (error == noMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error, "Could not enumerate a device.");
                }

                var instanceId = ReadInstanceId(deviceInfoSet, ref data);
                var name = ReadRegistryProperty(deviceInfoSet, ref data, SpdrpFriendlyName)
                    ?? ReadRegistryProperty(deviceInfoSet, ref data, SpdrpDeviceDesc)
                    ?? instanceId;
                var className = ReadRegistryProperty(deviceInfoSet, ref data, SpdrpClass) ?? "Unknown";
                var manufacturer = ReadRegistryProperty(deviceInfoSet, ref data, SpdrpManufacturer);
                var parent = ReadParentInstanceId(data.DeviceInstance);
                var compatibleIds = ReadRegistryMultiString(deviceInfoSet, ref data, SpdrpCompatibleIds);

                devices.Add(new DeviceNode(instanceId, className, name, manufacturer, parent, compatibleIds));
            }

            return devices;
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string ReadInstanceId(IntPtr deviceInfoSet, ref SpDevInfoData data)
    {
        var buffer = new StringBuilder(1024);
        if (!SetupDiGetDeviceInstanceIdW(deviceInfoSet, ref data, buffer, buffer.Capacity, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read a device instance ID.");
        }

        return buffer.ToString();
    }

    private static string? ReadRegistryProperty(IntPtr deviceInfoSet, ref SpDevInfoData data, uint property)
    {
        var buffer = new byte[4096];
        if (!SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet,
                ref data,
                property,
                out _,
                buffer,
                (uint)buffer.Length,
                out _))
        {
            return null;
        }

        var value = Encoding.Unicode.GetString(buffer);
        var terminator = value.IndexOf('\0');
        return terminator >= 0 ? value[..terminator] : value;
    }

    /// <summary>REG_MULTI_SZ property flattened to one semicolon-joined string.</summary>
    private static string? ReadRegistryMultiString(IntPtr deviceInfoSet, ref SpDevInfoData data, uint property)
    {
        var buffer = new byte[4096];
        if (!SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet,
                ref data,
                property,
                out _,
                buffer,
                (uint)buffer.Length,
                out var size))
        {
            return null;
        }

        var value = Encoding.Unicode.GetString(buffer, 0, Math.Min((int)size, buffer.Length));
        var parts = value.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : string.Join(';', parts);
    }

    private static string? ReadParentInstanceId(uint deviceInstance)
    {
        if (CM_Get_Parent(out var parent, deviceInstance, 0) != CrSuccess ||
            CM_Get_Device_ID_Size(out var length, parent, 0) != CrSuccess)
        {
            return null;
        }

        var buffer = new StringBuilder((int)length + 1);
        return CM_Get_Device_IDW(parent, buffer, buffer.Capacity, 0) == CrSuccess
            ? buffer.ToString()
            : null;
    }

    private static PowerState ReadPowerState()
    {
        var fallback = GetSystemPowerStatus(out var status)
            ? new PowerState(
                status.AcLineStatus == 1,
                status.BatteryLifePercent == 255 ? -1 : status.BatteryLifePercent,
                null)
            : new PowerState(false, -1, null);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT Active, PowerOnline, Charging, Discharging, ChargeRate, DischargeRate FROM BatteryStatus");
            using var results = searcher.Get();
            foreach (ManagementObject battery in results)
            {
                if (battery["Active"] is bool active && !active)
                {
                    continue;
                }

                var online = battery["PowerOnline"] is bool powerOnline
                    ? powerOnline
                    : fallback.LineOnline;
                var charging = battery["Charging"] is bool isCharging && isCharging;
                var discharging = battery["Discharging"] is bool isDischarging && isDischarging;
                var rate = charging
                    ? ReadRate(battery["ChargeRate"])
                    : discharging
                        ? -ReadRate(battery["DischargeRate"])
                        : 0;
                return fallback with { LineOnline = online, BatteryRateMilliwatts = rate };
            }
        }
        catch (ManagementException)
        {
            return fallback;
        }
        catch (UnauthorizedAccessException)
        {
            return fallback;
        }

        return fallback;
    }

    private static int ReadRate(object? value) =>
        value is null ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        [Out]
        byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parentDeviceInstance, uint deviceInstance, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Device_ID_Size(out uint length, uint deviceInstance, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint deviceInstance, StringBuilder buffer, int bufferLength, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);
}
