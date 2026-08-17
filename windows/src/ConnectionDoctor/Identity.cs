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
    private static string? cachedFor;
    private static Identity? cached;
    private static bool failureReported;

    public static string PathIn(string directory) => System.IO.Path.Combine(directory, "identity.json");

    public static string Path => PathIn(BackgroundCollector.DataDirectory);

    /// <summary>
    /// A device's identity within this installation: HMAC of its serial under
    /// the installation key, truncated. Null when the device reports no serial
    /// — "same model, unit unknown" is a real answer.
    /// </summary>
    public string? UnitKey(string? serial)
    {
        if (string.IsNullOrEmpty(serial))
        {
            return null;
        }

        var mac = HMACSHA256.HashData(InstallationKey, Encoding.UTF8.GetBytes(serial));
        return Convert.ToHexString(mac)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// The durable identity for this process's data directory, or null when
    /// there is none we can stand behind. Resolved at most once per directory:
    /// callers building a document take this once and thread it through, so a
    /// single envelope cannot be half-identified (see <see cref="ResolvedIdentity"/>).
    /// </summary>
    public static Identity? Current
    {
        get
        {
            var directory = BackgroundCollector.DataDirectory;
            lock (Gate)
            {
                if (cached is not null && cachedFor == directory)
                {
                    return cached;
                }

                var resolved = Resolve(directory);
                cachedFor = directory;
                cached = resolved;
                return resolved;
            }
        }
    }

    /// <summary>
    /// Read the identity in <paramref name="directory"/>, creating one if the
    /// directory has none. Takes no process-wide state, so a caller with its
    /// own directory — a test, or a future per-scope export — neither sees nor
    /// disturbs anyone else's.
    ///
    /// <paramref name="beforeCreate"/> is a seam for the one branch that is
    /// otherwise unreachable in a test: another process winning the creation
    /// race between our read and our move.
    /// </summary>
    internal static Identity? Resolve(string directory, Action? beforeCreate = null)
    {
        var path = PathIn(directory);
        if (Read(path) is { } existing)
        {
            return existing;
        }

        // Create exactly once across every process that might start together —
        // collector, CLI, HTTP and MCP can all be first. Move-without-overwrite
        // is atomic; whoever loses re-reads the winner's file rather than
        // caching a value it never persisted. The temp name is per-process so
        // two creators cannot fight over one path.
        var fresh = new Identity(Guid.NewGuid().ToString("d"), RandomNumberGenerator.GetBytes(KeyBytes));
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(fresh, Options));
            beforeCreate?.Invoke();
            // Move fails if another process already created it: that process
            // wins, and we adopt its identity below.
            File.Move(temporary, path, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            if (Read(path) is { } winner)
            {
                return winner;
            }

            Report($"no durable identity at {path} ({exception.Message}) — host.id and unitKey will be omitted");
            return null;
        }

        var readBack = Read(path);
        if (readBack is null)
        {
            Report($"could not persist an identity to {path} — host.id and unitKey will be omitted");
        }

        return readBack;
    }

    private static Identity? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var identity = JsonSerializer.Deserialize<Identity>(File.ReadAllText(path), Options);
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
        lock (Gate)
        {
            if (failureReported)
            {
                return;
            }

            failureReported = true;
        }

        try
        {
            Console.Error.WriteLine($"ConnectionDoctor: {message}");
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// One answer to "who is this machine", resolved once and carried through a
/// whole document.
///
/// Resolving per call would let a single envelope disagree with itself: the
/// first devices serialized before an identity existed would carry no
/// <c>unitKey</c>, and the ones after it would — a document half-keyed, and
/// one file-existence check plus a read per device on every machine that has
/// no durable identity at all. So the identity is resolved at the top of an
/// operation and passed down, and "there is none" is a resolved answer too.
/// </summary>
internal readonly record struct ResolvedIdentity(Identity? Value)
{
    /// <summary>No identity — omit host.id and every unitKey. A decision, not a failure to look.</summary>
    public static ResolvedIdentity None => new((Identity?)null);

    /// <summary>The identity of the process's data directory, resolved once.</summary>
    public static ResolvedIdentity ForThisProcess() => new(Identity.Current);

    public string? HostId => Value?.HostId;

    public string? UnitKey(string? serial) => Value?.UnitKey(serial);
}
