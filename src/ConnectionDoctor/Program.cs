namespace ConnectionDoctor;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ConnectionDoctor currently supports Windows only.");
            return 1;
        }

        try
        {
            var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "probe";
            return command switch
            {
                "probe" => Probe(),
                "tree" => Tree(),
                "snapshot" => Snapshot(args.Skip(1).FirstOrDefault()),
                "baseline" => Baseline(args.Skip(1).ToArray()),
                "diff" or "report" => Diff(args.Skip(1).FirstOrDefault()),
                "collect" => Collect(),
                "status" => Status(),
                "install" => Install(),
                "uninstall" => Uninstall(),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(command)
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ConnectionDoctor: {exception.Message}");
            return 1;
        }
    }

    private static int Probe()
    {
        var snapshot = DeviceProbe.Capture();
        Console.WriteLine($"ConnectionDoctor probe - {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine($"Host: {snapshot.HostName} ({snapshot.OperatingSystemArchitecture})");
        Console.WriteLine($"Power: {(snapshot.Power.LineOnline ? "AC" : "battery")}, {snapshot.Power.BatteryPercent}%");
        Console.WriteLine($"Present devices: {snapshot.Devices.Count}");
        Console.WriteLine();

        var byId = snapshot.Devices.ToDictionary(device => device.InstanceId, StringComparer.OrdinalIgnoreCase);
        foreach (var device in snapshot.Devices.Where(device => DeviceFilters.IsConnectionDevice(device, byId))
                     .OrderBy(device => device.ClassName)
                     .ThenBy(device => device.FriendlyName))
        {
            var id = device.VidPid is null ? string.Empty : $" [{device.VidPid}]";
            Console.WriteLine($"{device.ClassName,-12} {device.FriendlyName}{id}");
        }

        return 0;
    }

    private static int Tree()
    {
        TopologyRenderer.Write(DeviceProbe.Capture(), Console.Out);
        return 0;
    }

    private static int Snapshot(string? path)
    {
        var destination = path ?? $"connection-snapshot-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json";
        SnapshotStore.Save(DeviceProbe.Capture(), destination);
        Console.WriteLine($"Saved snapshot to {Path.GetFullPath(destination)}");
        return 0;
    }

    private static int Baseline(string[] args)
    {
        if (args.FirstOrDefault()?.Equals("save", StringComparison.OrdinalIgnoreCase) != true)
        {
            Console.Error.WriteLine("Usage: connectiondoctor baseline save [path]");
            return 1;
        }

        var destination = args.Skip(1).FirstOrDefault() ?? SnapshotStore.DefaultBaselinePath;
        SnapshotStore.Save(DeviceProbe.Capture(), destination);
        Console.WriteLine($"Saved known-good baseline to {Path.GetFullPath(destination)}");
        return 0;
    }

    private static int Diff(string? path)
    {
        var source = path ?? SnapshotStore.DefaultBaselinePath;
        if (!File.Exists(source))
        {
            Console.Error.WriteLine($"No baseline found at {Path.GetFullPath(source)}.");
            Console.Error.WriteLine("Create one while the setup works: connectiondoctor baseline save");
            return 1;
        }

        var baseline = SnapshotStore.Load(source);
        var current = DeviceProbe.Capture();
        var report = SnapshotComparer.Compare(baseline, current);

        Console.WriteLine($"Baseline: {baseline.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine($"Current:  {current.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine();

        if (report.Findings.Count == 0 && report.Missing.Count == 0 && report.Added.Count == 0)
        {
            Console.WriteLine("No connection changes detected.");
            return 0;
        }

        foreach (var finding in report.Findings)
        {
            Console.WriteLine($"{finding.Severity.ToUpperInvariant()}: {finding.Title}");
            Console.WriteLine($"  {finding.Explanation}");
            Console.WriteLine($"  Action: {finding.Recommendation}");
            Console.WriteLine();
        }

        WriteChanges("Missing", report.Missing, baseline.Devices);
        WriteChanges("Added", report.Added, current.Devices);
        return report.Findings.Any(finding => finding.Severity == "critical") ? 2 : 0;
    }

    private static int Collect()
    {
        return BackgroundCollector.Run();
    }

    private static int Status()
    {
        var status = BackgroundCollector.ReadStatus();
        Console.WriteLine(status.Message);
        return status.IsRunning ? 0 : 1;
    }

    private static int Install()
    {
        var result = StartupRegistration.Install();
        Console.WriteLine(result);
        return 0;
    }

    private static int Uninstall()
    {
        var result = StartupRegistration.Uninstall();
        Console.WriteLine(result);
        return 0;
    }

    private static void WriteChanges(string title, IReadOnlyList<DeviceNode> devices, IReadOnlyList<DeviceNode> allDevices)
    {
        if (devices.Count == 0)
        {
            return;
        }

        var allById = allDevices.ToDictionary(device => device.InstanceId, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"{title} ({devices.Count})");
        foreach (var device in devices.Where(device => DeviceFilters.IsConnectionDevice(device, allById)).Take(30))
        {
            var id = device.VidPid is null ? string.Empty : $" [{device.VidPid}]";
            Console.WriteLine($"  {device.ClassName,-12} {device.FriendlyName}{id}");
        }
        Console.WriteLine();
    }

    private static int Help()
    {
        Console.WriteLine("""
            ConnectionDoctor - Windows USB-C, USB4, display, hub, and peripheral diagnosis

              probe                    Show the current present-only connection state
              tree                     Draw the current parent-device topology
              snapshot [path]          Save the current state as JSON
              baseline save [path]     Save a known-good state
              diff [baseline-path]     Compare current state with known-good
              report [baseline-path]   Alias for diff
              collect                  Continuously record connection state
              status                   Show background collector health
              install                  Start collecting now and at user login
              uninstall                Remove login startup registration
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        return Help() == 0 ? 1 : 1;
    }
}
