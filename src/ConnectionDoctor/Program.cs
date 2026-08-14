namespace ConnectionDoctor;

internal static class Program
{
    [STAThread]
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
                "contract" => Contract(args.Skip(1).FirstOrDefault()),
                "serve" => Serve(args.Skip(1).ToArray()),
                "baseline" => Baseline(args.Skip(1).ToArray()),
                "diff" => Diff(args.Skip(1).FirstOrDefault()),
                "report" => Report(),
                "collect" or "watch" => Collect(),
                "status" => Status(),
                "install" => Install(),
                "uninstall" => Uninstall(),
                "ui" or "dashboard" => ContractServer.OpenDashboard(ContractServer.DefaultPort),
                "winui" => Dashboard(showImmediately: true),
                "tray" => Dashboard(showImmediately: false),
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
        Console.WriteLine(
            $"Power: {(snapshot.Power.LineOnline ? "AC" : "battery")}, " +
            $"{snapshot.Power.BatteryPercent}%, {FormatRate(snapshot.Power.BatteryRateMilliwatts)}");
        Console.WriteLine($"Present devices: {snapshot.Devices.Count}");
        Console.WriteLine();

        foreach (var device in DeviceFilters.ConnectionDevices(snapshot)
                     .OrderBy(device => device.ClassName)
                     .ThenBy(device => device.FriendlyName))
        {
            var id = device.VidPid is null ? string.Empty : $" [{device.VidPid}]";
            Console.WriteLine($"{device.ClassName,-12} {device.FriendlyName}{id}");
        }

        foreach (var finding in PowerDiagnosis.Analyze(snapshot.Power))
        {
            Console.WriteLine();
            Console.WriteLine($"{finding.Severity.ToUpperInvariant()}: {finding.Title}");
            Console.WriteLine($"  {finding.Explanation}");
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

    private static int Contract(string? path)
    {
        var json = ContractV1.Serialize(ContractV1.ToEnvelope(DeviceProbe.Capture()));
        if (path is null)
        {
            Console.WriteLine(json);
            return 0;
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, json);
        Console.WriteLine($"Saved Connection Contract v1 envelope to {fullPath}");
        return 0;
    }

    private static int Serve(string[] args)
    {
        var port = ContractServer.DefaultPort;
        var portArgument = args.FirstOrDefault(argument => !argument.StartsWith('-'));
        if (portArgument is not null &&
            (!int.TryParse(portArgument, out port) || port is < 1 or > 65535))
        {
            Console.Error.WriteLine(
                $"Usage: connectiondoctor serve [port] [--bind lan]   (default {ContractServer.DefaultPort})");
            return 1;
        }

        var lan = args.Contains("--bind", StringComparer.OrdinalIgnoreCase) &&
                  args.Contains("lan", StringComparer.OrdinalIgnoreCase);
        return ContractServer.Run(port, lan);
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

        WriteChanges("Missing", report.Missing);
        WriteChanges("Added", report.Added);
        return report.Findings.Any(finding => finding.Severity == "critical") ? 2 : 0;
    }

    private static int Collect()
    {
        return BackgroundCollector.Run();
    }

    private static int Report()
    {
        var incidents = IncidentStitcher.Stitch(BackgroundCollector.ReadEntries());
        if (incidents.Count == 0)
        {
            Console.WriteLine("No recorded connection incidents.");
            return 0;
        }

        Console.WriteLine($"ConnectionDoctor incidents ({incidents.Count})");
        foreach (var incident in incidents.OrderByDescending(item => item.Start))
        {
            var disappeared = incident.Events.Count(entry => entry.Kind == RecorderEntryKinds.DeviceDisappeared);
            var appeared = incident.Events.Count(entry => entry.Kind == RecorderEntryKinds.DeviceAppeared);
            Console.WriteLine(
                $"{incident.Start:yyyy-MM-dd HH:mm:ss}  " +
                $"{incident.Duration.TotalSeconds:F0}s  " +
                $"{disappeared} disappeared, {appeared} appeared");
            foreach (var entry in incident.Events.Where(entry => entry.Device is not null).Take(8))
            {
                Console.WriteLine($"  {entry.Kind}: {entry.Device!.FriendlyName} [{entry.Device.VidPid ?? entry.Device.ClassName}]");
            }
        }

        return 0;
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

    private static int Dashboard(bool showImmediately)
    {
        DashboardApplication.Run(showImmediately);
        return 0;
    }

    private static void WriteChanges(string title, IReadOnlyList<DeviceNode> devices)
    {
        if (devices.Count == 0)
        {
            return;
        }

        Console.WriteLine($"{title} ({devices.Count})");
        foreach (var device in devices.Take(30))
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
              contract [path]          Export state as a Connection Contract v1 envelope
              serve [port]             Serve /contract and /events for the dashboard
              baseline save [path]     Save a known-good state
              diff [baseline-path]     Compare current state with known-good
              report                   Summarize recorded incidents newest-first
              collect                  Continuously record connection state
              watch                    Alias for collect
              status                   Show background collector health
              install                  Start collecting now and at user login
              uninstall                Remove login startup registration
              ui                       Open the Connection Dashboard in a browser
              winui                    Open the legacy WinForms dashboard window
              tray                     Run the notification-area UI
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        return Help() == 0 ? 1 : 1;
    }

    private static string FormatRate(int? rateMilliwatts)
    {
        if (rateMilliwatts is null)
        {
            return "rate unavailable";
        }

        var direction = rateMilliwatts < 0 ? "discharging" : rateMilliwatts > 0 ? "charging" : "idle";
        return $"{direction} {Math.Abs(rateMilliwatts.Value) / 1000.0:F1} W";
    }
}
