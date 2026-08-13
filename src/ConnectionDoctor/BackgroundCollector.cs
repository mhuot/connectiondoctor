using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace ConnectionDoctor;

internal static class BackgroundCollector
{
    private const int SampleIntervalSeconds = 5;
    private const long MaximumEventBytes = 24 * 1024 * 1024;
    private static readonly TimeSpan FullSnapshotInterval = TimeSpan.FromHours(1);
    private const string MutexName = @"Local\ConnectionDoctor.Collector";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConnectionDoctor");

    public static string EventsPath => Path.Combine(DataDirectory, "events.jsonl");
    public static string CurrentSnapshotPath => Path.Combine(DataDirectory, "current.json");
    public static string HeartbeatPath => Path.Combine(DataDirectory, "heartbeat.json");
    public static string ErrorPath => Path.Combine(DataDirectory, "collector-errors.log");

    public static int Run()
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Console.Error.WriteLine("ConnectionDoctor collector is already running.");
            return 1;
        }

        Directory.CreateDirectory(DataDirectory);
        using var stopped = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopped.Set();
        };

        var startedAt = DateTimeOffset.Now;
        var lastFullSnapshotAt = DateTimeOffset.MinValue;
        ConnectionSnapshot? previous = null;
        Console.WriteLine($"ConnectionDoctor collector started. Events: {EventsPath}");

        while (!stopped.IsSet)
        {
            try
            {
                var snapshot = DeviceProbe.Capture();
                var entries = new List<RecorderEntry>();
                if (previous is not null)
                {
                    entries.AddRange(Recorder.DetectChanges(previous, snapshot));
                }

                if (snapshot.CapturedAt - lastFullSnapshotAt >= FullSnapshotInterval)
                {
                    entries.Add(RecorderEntry.FullSnapshot(snapshot));
                    lastFullSnapshotAt = snapshot.CapturedAt;
                }

                AppendEntries(entries);
                SaveCurrent(snapshot);
                WriteHeartbeat(startedAt, snapshot.CapturedAt);
                PrintChanges(entries);
                previous = snapshot;
            }
            catch (Win32Exception exception)
            {
                RecordError(exception);
            }
            catch (IOException exception)
            {
                RecordError(exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                RecordError(exception);
            }

            stopped.Wait(TimeSpan.FromSeconds(SampleIntervalSeconds));
        }

        return 0;
    }

    public static IReadOnlyList<RecorderEntry> ReadEntries()
    {
        if (!File.Exists(EventsPath))
        {
            return [];
        }

        var entries = new List<RecorderEntry>();
        foreach (var line in File.ReadLines(EventsPath))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<RecorderEntry>(line, JsonOptions);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException exception)
            {
                RecordError(exception);
            }
        }

        return entries;
    }

    public static ConnectionSnapshot? ReadCurrentSnapshot()
    {
        if (!File.Exists(CurrentSnapshotPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ConnectionSnapshot>(
                File.ReadAllText(CurrentSnapshotPath),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static CollectorStatus ReadStatus()
    {
        if (!File.Exists(HeartbeatPath))
        {
            return new CollectorStatus(false, "ConnectionDoctor collector has not written a heartbeat.");
        }

        CollectorHeartbeat? heartbeat;
        try
        {
            heartbeat = JsonSerializer.Deserialize<CollectorHeartbeat>(
                File.ReadAllText(HeartbeatPath),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return new CollectorStatus(false, $"ConnectionDoctor heartbeat is invalid: {exception.Message}");
        }

        if (heartbeat is null)
        {
            return new CollectorStatus(false, "ConnectionDoctor heartbeat is empty.");
        }

        var age = DateTimeOffset.Now - heartbeat.LastSampleAt;
        var processRunning = IsProcessRunning(heartbeat.ProcessId);
        var healthy = processRunning && age <= TimeSpan.FromSeconds(SampleIntervalSeconds * 3);
        var message = healthy
            ? $"ConnectionDoctor collector is running (PID {heartbeat.ProcessId}); last sample {age.TotalSeconds:F0}s ago."
            : $"ConnectionDoctor collector is not healthy; PID {heartbeat.ProcessId}, last sample {age.TotalSeconds:F0}s ago.";
        return new CollectorStatus(healthy, message);
    }

    private static void AppendEntries(IReadOnlyList<RecorderEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        using (var writer = File.AppendText(EventsPath))
        {
            foreach (var entry in entries)
            {
                writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
            }
        }

        TrimEventsIfNeeded();
    }

    private static void SaveCurrent(ConnectionSnapshot snapshot)
    {
        var temporaryPath = CurrentSnapshotPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temporaryPath, CurrentSnapshotPath, true);
    }

    private static void WriteHeartbeat(DateTimeOffset startedAt, DateTimeOffset lastSampleAt)
    {
        var heartbeat = new CollectorHeartbeat(
            Environment.ProcessId,
            startedAt,
            lastSampleAt,
            EventsPath);
        var temporaryPath = HeartbeatPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(heartbeat, JsonOptions));
        File.Move(temporaryPath, HeartbeatPath, true);
    }

    private static void TrimEventsIfNeeded()
    {
        var file = new FileInfo(EventsPath);
        if (file.Length <= MaximumEventBytes)
        {
            return;
        }

        var bytes = File.ReadAllBytes(EventsPath);
        var start = bytes.Length / 2;
        while (start < bytes.Length && bytes[start] != (byte)'\n')
        {
            start++;
        }

        if (start < bytes.Length)
        {
            start++;
        }

        File.WriteAllBytes(EventsPath, bytes[start..]);
    }

    private static void PrintChanges(IEnumerable<RecorderEntry> entries)
    {
        foreach (var entry in entries.Where(item => item.Kind != RecorderEntryKinds.Snapshot))
        {
            var detail = entry.Device?.FriendlyName ?? entry.Power?.ToString() ?? string.Empty;
            Console.WriteLine($"{entry.At:HH:mm:ss} {entry.Kind} {detail}".TrimEnd());
        }
    }

    private static void RecordError(Exception exception)
    {
        Directory.CreateDirectory(DataDirectory);
        var entry = $"{DateTimeOffset.Now:O} {exception.GetType().Name}: {exception.Message}{Environment.NewLine}";
        File.AppendAllText(ErrorPath, entry);
        Console.Error.Write(entry);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

internal sealed record CollectorHeartbeat(
    int ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSampleAt,
    string EventsPath);

internal sealed record CollectorStatus(bool IsRunning, string Message);
