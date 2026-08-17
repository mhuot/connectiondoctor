using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConnectionDoctor;

/// <summary>
/// Who this machine is, and how to tell two identical devices apart — without
/// becoming a tracking identifier.
///
/// Both values are random and generated here; neither is derived from hardware.
/// A hash of MachineGuid would be stable across every export forever, which is
/// pseudonymisation rather than privacy: anyone holding two unrelated bundles
/// could link them. Instead:
///
/// - <c>HostId</c> is a random UUID for *this installation*: it survives
///   hostname changes (the thing it exists to fix) and upgrades, and
///   regenerates only when the data directory is reset.
/// - <c>InstallationKey</c> is a random secret that never leaves the machine.
///   Device serials are keyed with it, so a unit key distinguishes two
///   identical docks *here* while meaning nothing anywhere else.
/// </summary>
internal sealed record Identity(string HostId, byte[] InstallationKey)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly object Gate = new();
    private static Identity? cached;

    public static string Path => System.IO.Path.Combine(BackgroundCollector.DataDirectory, "identity.json");

    public static Identity Current
    {
        get
        {
            lock (Gate)
            {
                if (cached is not null)
                {
                    return cached;
                }

                if (File.Exists(Path))
                {
                    try
                    {
                        var existing = JsonSerializer.Deserialize<Identity>(File.ReadAllText(Path), Options);
                        if (existing is not null && existing.HostId.Length > 0 && existing.InstallationKey.Length > 0)
                        {
                            cached = existing;
                            return existing;
                        }
                    }
                    catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                    {
                        // Unreadable identity: generate a fresh one rather than
                        // failing a probe. The consequence is a new host id,
                        // which reads as a new endpoint — honest, and visible.
                    }
                }

                var fresh = new Identity(Guid.NewGuid().ToString("d"), RandomNumberGenerator.GetBytes(32));
                Save(fresh);
                cached = fresh;
                return fresh;
            }
        }
    }

    private static void Save(Identity identity)
    {
        try
        {
            Directory.CreateDirectory(BackgroundCollector.DataDirectory);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(identity, Options));
            if (File.Exists(Path))
            {
                File.Replace(temporary, Path, null);
            }
            else
            {
                File.Move(temporary, Path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: an identity that cannot be persisted still works for
            // this process, and the next run generates another.
        }
    }

    /// <summary>
    /// A device's identity within this installation: HMAC of its serial under
    /// the installation key, truncated. Null when the device reports no serial —
    /// "same model, unit unknown" is a real answer and better than a guess.
    /// </summary>
    public static string? UnitKey(string? serial)
    {
        if (string.IsNullOrEmpty(serial))
        {
            return null;
        }

        var mac = HMACSHA256.HashData(Current.InstallationKey, Encoding.UTF8.GetBytes(serial));
        return Convert.ToHexString(mac)[..16].ToLowerInvariant();
    }

    /// <summary>Test seam: forget the cached identity so a fixture directory gets its own.</summary>
    public static void ResetCacheForTesting()
    {
        lock (Gate)
        {
            cached = null;
        }
    }
}
