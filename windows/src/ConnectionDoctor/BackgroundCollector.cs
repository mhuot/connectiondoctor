using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace ConnectionDoctor;

internal static class BackgroundCollector
{
    private const int SampleIntervalSeconds = 5;
    private const long MaximumSampleBytes = 24 * 1024 * 1024;
    private const string MutexName = @"Local\ConnectionDoctor.Collector";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConnectionDoctor");

    public static string SamplesPath => Path.Combine(DataDirectory, "samples.jsonl");
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
        Console.WriteLine($"ConnectionDoctor collector started. Samples: {SamplesPath}");

        while (!stopped.IsSet)
        {
            try
            {
                var snapshot = DeviceProbe.Capture();
                AppendSnapshot(snapshot);
                WriteHeartbeat(startedAt, snapshot.CapturedAt);
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

    private static void AppendSnapshot(ConnectionSnapshot snapshot)
    {
        var line = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.AppendAllText(SamplesPath, line + Environment.NewLine);
        TrimSamplesIfNeeded();
    }

    private static void WriteHeartbeat(DateTimeOffset startedAt, DateTimeOffset lastSampleAt)
    {
        var heartbeat = new CollectorHeartbeat(
            Environment.ProcessId,
            startedAt,
            lastSampleAt,
            SamplesPath);
        var temporaryPath = HeartbeatPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(heartbeat, JsonOptions));
        File.Move(temporaryPath, HeartbeatPath, true);
    }

    private static void TrimSamplesIfNeeded()
    {
        var file = new FileInfo(SamplesPath);
        if (file.Length <= MaximumSampleBytes)
        {
            return;
        }

        var bytes = File.ReadAllBytes(SamplesPath);
        var start = bytes.Length / 2;
        while (start < bytes.Length && bytes[start] != (byte)'\n')
        {
            start++;
        }

        if (start < bytes.Length)
        {
            start++;
        }

        File.WriteAllBytes(SamplesPath, bytes[start..]);
    }

    private static void RecordError(Exception exception)
    {
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
    string SamplesPath);

internal sealed record CollectorStatus(bool IsRunning, string Message);
