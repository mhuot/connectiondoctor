using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConnectionDoctor;

/// <summary>
/// Who this machine is, and how to tell two identical devices apart — without
/// becoming a tracking identifier, and without ever inventing an identity it
/// cannot keep.
///
/// Neither value is derived from hardware. A hash of MachineGuid would be
/// stable across every export forever, which is pseudonymisation rather than
/// privacy: anyone holding two unrelated bundles could link them.
///
/// The harder rule is <b>durability</b>: an identity that changes between runs
/// is worse than none, because it silently splits one endpoint into many in
/// every consumer that keys on it. So when the identity cannot be read or
/// created and persisted, this is null and the producer emits no identity at
/// all — consumers fall back to the hostname, which is honest, rather than to
/// a process-local random pretending to be an installation.
/// </summary>
internal sealed record Identity(string HostId, byte[] InstallationKey)
{
    /// <summary>A key shorter than this is not one we wrote; treat the file as corrupt.</summary>
    private const int KeyBytes = 32;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly object Gate = new();
    private static Identity? cached;
    private static bool failureReported;

    public static string Path => System.IO.Path.Combine(BackgroundCollector.DataDirectory, "identity.json");

    /// <summary>The durable identity, or null when there is none we can stand behind.</summary>
    public static Identity? Current
    {
        get
        {
            lock (Gate)
            {
                if (cached is not null)
                {
                    return cached;
                }

                if (Read() is { } existing)
                {
                    cached = existing;
                    return existing;
                }

                // Create exactly once across every process that might start
                // together — collector, CLI, HTTP and MCP can all be first.
                // CreateNew is atomic; whoever loses re-reads the winner's file
                // rather than caching a value it never persisted. The temp name
                // is per-process so two creators cannot fight over one path.
                var fresh = new Identity(Guid.NewGuid().ToString("d"), RandomNumberGenerator.GetBytes(KeyBytes));
                var temporary = Path + "." + Environment.ProcessId + ".tmp";
                try
                {
                    Directory.CreateDirectory(BackgroundCollector.DataDirectory);
                    File.WriteAllText(temporary, JsonSerializer.Serialize(fresh, Options));
                    // Move fails if another process already created it: that
                    // process wins, and we adopt its identity below.
                    File.Move(temporary, Path, overwrite: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    TryDelete(temporary);
                    if (Read() is { } winner)
                    {
                        cached = winner;
                        return winner;
                    }

                    Report($"no durable identity at {Path} ({exception.Message}) — host.id and unitKey will be omitted");
                    return null;
                }

                var readBack = Read();
                if (readBack is null)
                {
                    Report($"could not persist an identity to {Path} — host.id and unitKey will be omitted");
                    return null;
                }

                cached = readBack;
                return readBack;
            }
        }
    }

    /// <summary>
    /// A device's identity within this installation: HMAC of its serial under
    /// the installation key, truncated. Null when there is no durable identity
    /// or the device reports no serial — "same model, unit unknown" is a real
    /// answer, and so is "this machine has no identity to key it with".
    /// </summary>
    public static string? UnitKey(string? serial)
    {
        if (string.IsNullOrEmpty(serial) || Current is not { } identity)
        {
            return null;
        }

        var mac = HMACSHA256.HashData(identity.InstallationKey, Encoding.UTF8.GetBytes(serial));
        return Convert.ToHexString(mac)[..16].ToLowerInvariant();
    }

    private static Identity? Read()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            var identity = JsonSerializer.Deserialize<Identity>(File.ReadAllText(Path), Options);
            if (identity is null ||
                !Guid.TryParse(identity.HostId, out _) ||
                identity.InstallationKey.Length != KeyBytes)
            {
                return null;   // corrupt: not an identity we wrote
            }

            return identity;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Say it once: a machine with no identity should not fill the log with it.</summary>
    private static void Report(string message)
    {
        if (failureReported)
        {
            return;
        }

        failureReported = true;
        try
        {
            Console.Error.WriteLine($"ConnectionDoctor: {message}");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Test seam: forget the cached identity so a fixture directory gets its own.</summary>
    public static void ResetCacheForTesting()
    {
        lock (Gate)
        {
            cached = null;
            failureReported = false;
        }
    }
}
