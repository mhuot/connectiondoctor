using System.Text.Json;

namespace ConnectionDoctor;

/// <summary>
/// Every writer of the default baseline goes through here: the CLI's
/// <c>baseline save</c> and the dashboard's <c>POST /baseline</c> can run at
/// the same moment, so read → decide → write is one transaction under one
/// named cross-process lock, and the write itself is atomic (temp file, then
/// replace). Fail-closed: an unreadable existing baseline is an error, never
/// "absent" — treating it as absent would let a replace bypass the CAS check
/// and silently discard a known-good state.
/// </summary>
internal static class BaselineTransaction
{
    private const string MutexName = @"Local\ConnectionDoctor.Baseline";

    /// <summary>Put the previous baseline back (or remove a first capture) after a failed commit.</summary>
    private static bool Restore(ConnectionSnapshot? previous, string path)
    {
        try
        {
            if (previous is null)
            {
                File.Delete(path);
            }
            else
            {
                SnapshotStore.SaveAtomic(previous, path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    internal enum Outcome
    {
        Captured,
        Replaced,
        Exists,
        Stale,
        Unreadable,
        Busy,
        WriteFailed
    }

    internal sealed record Result(
        Outcome Outcome,
        DateTimeOffset? CapturedAt = null,
        int Nodes = 0,
        DateTimeOffset? CurrentCapturedAt = null,
        string? Detail = null)
    {
        public bool Ok => Outcome is Outcome.Captured or Outcome.Replaced;
    }

    /// <summary>
    /// Capture, or replace, the default baseline.
    /// </summary>
    /// <param name="replace">Replacement was requested; without it an existing baseline is left alone.</param>
    /// <param name="expectedCapturedAt">
    /// The capture time the caller was shown. Required for HTTP replacement (If-Match);
    /// null from the CLI, which is an explicit local action by the person at the keyboard.
    /// </param>
    /// <param name="capture">Takes the snapshot — inside the lock, so what is written is what was decided on.</param>
    /// <summary>
    /// Run <paramref name="work"/> holding the baseline lock — for readers that
    /// must not interleave with a replacement (the analysis reads the baseline
    /// and its fault history together). Returns false if the lock is busy.
    /// </summary>
    public static bool WithLock(Action work)
    {
        using var mutex = new Mutex(false, MutexName);
        var held = false;
        try
        {
            try
            {
                held = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }

            if (held)
            {
                work();
            }

            return held;
        }
        finally
        {
            if (held)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    public static Result Run(bool replace, DateTimeOffset? expectedCapturedAt, Func<ConnectionSnapshot> capture, bool requireExpectedOnReplace)
    {
        using var mutex = new Mutex(false, MutexName);
        var held = false;
        try
        {
            try
            {
                held = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                // The previous holder died mid-transaction; the file is either
                // the old one or the new one (writes are atomic), so continue.
                held = true;
            }

            if (!held)
            {
                return new Result(Outcome.Busy, Detail: "another baseline write is in progress");
            }

            var path = SnapshotStore.DefaultBaselinePath;
            ConnectionSnapshot? existing = null;
            if (File.Exists(path))
            {
                try
                {
                    existing = SnapshotStore.Load(path);
                }
                catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
                {
                    // Fail closed: we cannot prove what we would be discarding.
                    // (Access denied included — an unreadable baseline is a
                    // structured error, never a crashed request loop.)
                    return new Result(Outcome.Unreadable, Detail: exception.Message);
                }
            }

            if (existing is not null)
            {
                if (!replace)
                {
                    return new Result(Outcome.Exists, CurrentCapturedAt: existing.CapturedAt);
                }

                if (requireExpectedOnReplace &&
                    (expectedCapturedAt is null || expectedCapturedAt != existing.CapturedAt))
                {
                    return new Result(Outcome.Stale, CurrentCapturedAt: existing.CapturedAt);
                }
            }

            // Keep the history as it was, so a failed commit can put the pair
            // back exactly as it was found.
            var previousHistory = BaselineStateFile.Read();

            var snapshot = capture();
            try
            {
                SnapshotStore.SaveAtomic(snapshot, path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new Result(Outcome.WriteFailed, Detail: exception.Message);
            }

            // The fault/recovery history described the baseline we just
            // discarded; every writer resets it, not just the HTTP one. If the
            // reset cannot be written, the pair would be inconsistent — a new
            // baseline carrying the old baseline's fault. Roll the baseline
            // back so the caller's next attempt sees the state it expects
            // (and its old ETag still matches).
            // The history names the baseline it belongs to, so a crash between
            // these two writes leaves a recognisably stale pair rather than a
            // silently mismatched one (the reader discards it).
            if (!BaselineStateFile.Write(new BaselineStateFile(BaselineCapturedAt: snapshot.CapturedAt)))
            {
                var baselineRestored = Restore(existing, path);
                var historyRestored = previousHistory is null || BaselineStateFile.Write(previousHistory);
                return new Result(Outcome.WriteFailed, Detail: baselineRestored && historyRestored
                    ? "the baseline's fault history could not be reset; nothing was changed"
                    : $"the baseline's fault history could not be reset, and the previous state could not be fully restored " +
                      $"(baseline {(baselineRestored ? "restored" : "MAY HAVE CHANGED")}, " +
                      $"history {(historyRestored ? "restored" : "MAY HAVE CHANGED")}) — check `connectiondoctor diff` before relying on it");
            }

            var nodes = DeviceFilters.TopologyDevices(snapshot, includeBuiltIn: true).Count;
            return new Result(existing is null ? Outcome.Captured : Outcome.Replaced, snapshot.CapturedAt, nodes, existing?.CapturedAt);
        }
        finally
        {
            if (held)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
