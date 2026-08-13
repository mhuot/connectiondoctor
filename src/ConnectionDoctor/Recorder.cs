using System.Text.Json;

namespace ConnectionDoctor;

/// <summary>
/// Runs the always-on watch loop: polls every N seconds, emits change-only
/// JSONL events to <c>%LOCALAPPDATA%\ConnectionDoctor\events.jsonl</c>,
/// and writes hourly full-snapshot sync points.
/// </summary>
internal static class Recorder
{
    private const int PollIntervalSeconds = 5;
    private const int HourlySnapshotIntervalMinutes = 60;
    private const long MaxEventLogBytes = 24 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DataDirectory => BackgroundCollector.DataDirectory;
    public static string EventLogPath => Path.Combine(DataDirectory, "events.jsonl");
    public static string HourlySnapshotPath => Path.Combine(DataDirectory, "hourly-snapshot.json");

    public static int Run(int intervalSeconds = PollIntervalSeconds)
    {
        Directory.CreateDirectory(DataDirectory);
        using var stopped = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopped.Set();
        };

        Console.WriteLine($"ConnectionDoctor watch started (interval: {intervalSeconds}s). Events: {EventLogPath}");
        Console.WriteLine("Press Ctrl+C to stop.");

        ConnectionSnapshot? previous = null;
        var lastHourlySnapshot = DateTimeOffset.MinValue;

        while (!stopped.IsSet)
        {
            try
            {
                var current = DeviceProbe.Capture();

                // Hourly full-snapshot sync point
                if ((current.CapturedAt - lastHourlySnapshot).TotalMinutes >= HourlySnapshotIntervalMinutes)
                {
                    WriteEvent(new WatchEvent(
                        current.CapturedAt,
                        "snapshot",
                        current.Devices,
                        Array.Empty<DeviceNode>(),
                        current.Power));
                    SnapshotStore.Save(current, HourlySnapshotPath);
                    lastHourlySnapshot = current.CapturedAt;
                }

                if (previous is not null)
                {
                    var added = Difference(current.Devices, previous.Devices);
                    var removed = Difference(previous.Devices, current.Devices);

                    if (added.Count > 0 || removed.Count > 0)
                    {
                        var evt = new WatchEvent(
                            current.CapturedAt,
                            "change",
                            added,
                            removed,
                            current.Power);
                        WriteEvent(evt);

                        // Print one-line summary to console
                        var parts = new List<string>();
                        if (removed.Count > 0)
                        {
                            parts.Add($"-{removed.Count} removed");
                        }
                        if (added.Count > 0)
                        {
                            parts.Add($"+{added.Count} added");
                        }
                        Console.WriteLine($"{current.CapturedAt:HH:mm:ss}  {string.Join(", ", parts)}  [{(current.Power.LineOnline ? "AC" : "battery")} {current.Power.BatteryPercent}%]");
                    }
                }

                previous = current;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{DateTimeOffset.Now:HH:mm:ss}  ERROR: {ex.Message}");
            }

            stopped.Wait(TimeSpan.FromSeconds(intervalSeconds));
        }

        return 0;
    }

    /// <summary>
    /// Reads all change events from the event log.
    /// Skips malformed lines gracefully.
    /// </summary>
    public static IReadOnlyList<WatchEvent> ReadEvents()
    {
        if (!File.Exists(EventLogPath))
        {
            return Array.Empty<WatchEvent>();
        }

        var events = new List<WatchEvent>();
        foreach (var line in File.ReadLines(EventLogPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var evt = JsonSerializer.Deserialize<WatchEvent>(line, JsonOptions);
                if (evt is not null)
                {
                    events.Add(evt);
                }
            }
            catch (JsonException)
            {
                // Skip malformed lines
            }
        }

        return events;
    }

    private static void WriteEvent(WatchEvent evt)
    {
        var line = JsonSerializer.Serialize(evt, JsonOptions);
        File.AppendAllText(EventLogPath, line + Environment.NewLine);
        TrimIfNeeded();
    }

    private static void TrimIfNeeded()
    {
        var file = new FileInfo(EventLogPath);
        if (file.Length <= MaxEventLogBytes)
        {
            return;
        }

        var bytes = File.ReadAllBytes(EventLogPath);
        var start = bytes.Length / 2;
        while (start < bytes.Length && bytes[start] != (byte)'\n')
        {
            start++;
        }
        if (start < bytes.Length)
        {
            start++;
        }
        File.WriteAllBytes(EventLogPath, bytes[start..]);
    }

    private static IReadOnlyList<DeviceNode> Difference(
        IReadOnlyList<DeviceNode> source,
        IReadOnlyList<DeviceNode> other)
    {
        var otherCounts = other
            .GroupBy(d => d.StableId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new List<DeviceNode>();
        foreach (var device in source)
        {
            if (otherCounts.TryGetValue(device.StableId, out var count) && count > 0)
            {
                otherCounts[device.StableId] = count - 1;
            }
            else
            {
                result.Add(device);
            }
        }
        return result;
    }
}
