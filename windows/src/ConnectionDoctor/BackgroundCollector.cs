using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace ConnectionDoctor;

internal static class BackgroundCollector
{
    private const int SampleIntervalSeconds = 5;
    /// <summary>Three missed samples is a hole, not jitter (matches WindowsAnalysis.GapTolerance).</summary>
    private static readonly TimeSpan MaximumSampleInterval = TimeSpan.FromSeconds(SampleIntervalSeconds * 3);
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
    /// <summary>ISO time of the last events trim, so coverage can say `trimmed` (Contract v1 coverage reasons).</summary>
    public static string TrimMarkerPath => Path.Combine(DataDirectory, "events.trimmed-at");
    /// <summary>
    /// Durable record of every stretch the collector was NOT sampling — a
    /// failed probe, a stopped service, a sleeping machine. The heartbeat is
    /// overwritten by the next success, so without this an outage would leave
    /// no trace and a window containing it could claim to be complete.
    /// </summary>
    public static string GapsPath => Path.Combine(DataDirectory, "gaps.jsonl");

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
        // A previous run's last sample and this start bracket an outage: record
        // it before the first sample overwrites the heartbeat.
        var priorHeartbeat = ReadHeartbeat();
        if (priorHeartbeat is not null && startedAt - priorHeartbeat.LastSampleAt > MaximumSampleInterval)
        {
            RecordGap(priorHeartbeat.LastSampleAt, startedAt, "collector-not-running");
        }

        var lastSampleAt = startedAt;
        Console.WriteLine($"ConnectionDoctor collector started. Events: {EventsPath}");

        while (!stopped.IsSet)
        {
            try
            {
                var snapshot = DeviceProbe.Capture();
                // Any interval longer than the promised cadence is a hole in
                // the evidence, whatever caused it (failed probe, sleep, load).
                if (snapshot.CapturedAt - lastSampleAt > MaximumSampleInterval)
                {
                    RecordGap(lastSampleAt, snapshot.CapturedAt, "sampling-interrupted");
                }

                lastSampleAt = snapshot.CapturedAt;
                var entries = new List<RecorderEntry>();
                if (previous is not null)
                {
                    entries.AddRange(Recorder.DetectChanges(previous, snapshot));
                }

                if (snapshot.CapturedAt - lastFullSnapshotAt >= FullSnapshotInterval)
                {
                    // A sync point is a complete envelope: compute the analysis
                    // group now (hourly, over the log we are writing) and store
                    // it with the snapshot so readers do not have to.
                    EmbeddedAnalysis? embedded = null;
                    var analysis = WindowsAnalysis.Run(WindowsAnalysis.ReadInputs(), snapshot, now: snapshot.CapturedAt);
                    if (analysis is not null)
                    {
                        embedded = new EmbeddedAnalysis(
                            analysis.Findings.Select(ContractV1.ToFinding).ToList(),
                            analysis.Incidents,
                            WindowsAnalysis.ToAnalysis(analysis));
                    }

                    entries.Add(RecorderEntry.FullSnapshot(snapshot, embedded));
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

    public static IReadOnlyList<RecorderEntry> ReadEntries() => ReadEntriesWithIntegrity().Entries;

    /// <summary>
    /// Entries plus how many lines could not be parsed — corrupt evidence must
    /// reach coverage, not be silently dropped (a stream of unreadable lines
    /// would otherwise look like a quiet machine).
    /// </summary>
    public static IncrementalEventRead ReadEntriesWithIntegrity()
    {
        var cursor = new EventLogCursor();
        return ReadEntriesIncremental(EventsPath, cursor);
    }

    public static IncrementalEventRead ReadEntriesIncremental(string path, EventLogCursor cursor)
    {
        if (!File.Exists(path))
        {
            var resetMissing = cursor.Offset != 0 || cursor.PendingText.Length != 0;
            cursor.Reset();
            return new IncrementalEventRead([], resetMissing, 0);
        }

        var file = new FileInfo(path);
        var reset = file.Length < cursor.Offset;
        if (reset)
        {
            cursor.Reset();
        }

        if (file.Length == cursor.Offset)
        {
            return new IncrementalEventRead([], reset, 0);
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(cursor.Offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, leaveOpen: true);
        var appended = reader.ReadToEnd();
        cursor.Offset = stream.Position;

        var combined = cursor.PendingText + appended;
        var lines = combined.Split('\n');
        var hasCompleteFinalLine = combined.EndsWith('\n');
        cursor.PendingText = hasCompleteFinalLine ? string.Empty : lines[^1];
        var completeLineCount = hasCompleteFinalLine ? lines.Length : lines.Length - 1;
        var entries = new List<RecorderEntry>();
        var skipped = 0;
        for (var index = 0; index < completeLineCount; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<RecorderEntry>(line, JsonOptions);
                if (entry is not null)
                {
                    entries.Add(entry);
                    cursor.ParsedLineCount++;
                }
            }
            catch (JsonException exception)
            {
                skipped++;
                RecordError(exception);
            }
        }

        return new IncrementalEventRead(entries, reset, skipped);
    }

    public static ConnectionSnapshot? ReadCurrentSnapshot(string? path = null)
    {
        var source = path ?? CurrentSnapshotPath;
        if (!File.Exists(source))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ConnectionSnapshot>(
                File.ReadAllText(source),
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
        // Remember that history was cut here, so coverage can say `trimmed`
        // instead of the window silently looking short.
        File.WriteAllText(TrimMarkerPath, DateTimeOffset.Now.ToString("O"));
    }

    /// <summary>Append one durable outage record; best effort, never fatal to collection.</summary>
    public static void RecordGap(DateTimeOffset from, DateTimeOffset to, string reason)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(GapsPath, JsonSerializer.Serialize(new CollectorGap(from, to, reason), JsonOptions) + "\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Recorded outages overlapping [from, to]; unreadable lines are ignored but counted by the caller's integrity check.</summary>
    public static IReadOnlyList<CollectorGap> ReadGaps(DateTimeOffset since)
    {
        if (!File.Exists(GapsPath))
        {
            return [];
        }

        var gaps = new List<CollectorGap>();
        try
        {
            foreach (var line in File.ReadLines(GapsPath))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    var gap = JsonSerializer.Deserialize<CollectorGap>(line, JsonOptions);
                    if (gap is not null && gap.To >= since)
                    {
                        gaps.Add(gap);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
        }

        return gaps;
    }

    /// <summary>The collector's heartbeat, or null when none has been written or it is unreadable.</summary>
    public static CollectorHeartbeat? ReadHeartbeat()
    {
        if (!File.Exists(HeartbeatPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CollectorHeartbeat>(File.ReadAllText(HeartbeatPath), JsonOptions);
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

internal sealed class EventLogCursor
{
    public long Offset { get; set; }
    public string PendingText { get; set; } = string.Empty;
    public long ParsedLineCount { get; set; }

    public void Reset()
    {
        Offset = 0;
        PendingText = string.Empty;
    }
}

internal sealed record IncrementalEventRead(
    IReadOnlyList<RecorderEntry> Entries,
    bool Reset,
    int SkippedLines = 0);

/// <summary>A stretch the collector was not sampling — durable, unlike the heartbeat.</summary>
internal sealed record CollectorGap(DateTimeOffset From, DateTimeOffset To, string Reason);
