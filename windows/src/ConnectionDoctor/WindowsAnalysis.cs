using System.Text.Json;

namespace ConnectionDoctor;

/// <summary>
/// The contract's <c>findings[]</c>, <c>incidents[]</c> and <c>analysis{}</c> for the
/// Windows envelope, computed over the recorder's on-disk history and heartbeat
/// (Contract v1 § Envelope; change <c>contract-findings-incidents</c>). The
/// Windows twin of TBDoctor's <c>Analysis.swift</c>, working on what this
/// recorder actually keeps: change entries (not per-sample rates), hourly full
/// snapshots, and a heartbeat that says when the current run started and when
/// it last sampled.
///
/// One rule above all: <b>absent ≠ empty</b>. When the recorder has never run
/// there is nothing to say and the envelope carries no analysis; when it has,
/// <c>coverage</c> states exactly what the recording can vouch for, so an empty
/// stream, a recorder that started ten minutes ago, a stopped recorder and a
/// trimmed log stop looking alike.
/// </summary>
internal static class WindowsAnalysis
{
    public const double DefaultWindowHours = 6;
    /// <summary>The recorder's promised cadence; three missed samples is a hole.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GapTolerance = SampleInterval * 3;

    /// <summary>
    /// A deficit has to be sustained to be a finding, and it has to be deep:
    /// the same thresholds as TBDoctor's engine (≥10 W, ≥2 samples) so the
    /// same fault reads the same on both platforms. The event log itself
    /// keeps using the −2 W instantaneous rule for deficitStart/deficitEnd.
    /// </summary>
    public const int SustainedDeficitMilliwatts = 10_000;
    public static readonly TimeSpan SustainedDeficitMinimum = SampleInterval * 2;

    public sealed record Result(
        IReadOnlyList<Finding> Findings,
        IReadOnlyList<ContractIncident> Incidents,
        double WindowHours,
        DateTimeOffset GeneratedAt,
        DateTimeOffset AvailableFrom,
        DateTimeOffset Through,
        bool Complete,
        IReadOnlyList<string> Reasons,
        /// <summary>Null when the baseline could not be evaluated; the envelope then omits it.</summary>
        ContractBaselineState? Baseline,
        string LinkEvents,
        /// <summary>`available` or why not — busy, unreadable, history-unreadable, history-unwritable.</summary>
        string BaselineAvailability = "available");

    /// <summary>What the analysis reads; injectable so tests need no disk.</summary>
    public sealed record Inputs(
        IReadOnlyList<RecorderEntry> Entries,
        CollectorHeartbeat? Heartbeat,
        DateTimeOffset? TrimmedAt,
        /// <summary>
        /// Baseline and history are NOT read here: they are read inside the
        /// lock, together with the comparison, so a replacement cannot land
        /// between the read and the verdict (see EvaluateBaseline).
        /// </summary>
        IBaselineStore? Store,
        /// <summary>Lines of the event log that could not be parsed — corrupt evidence, not absence.</summary>
        int SkippedLines = 0,
        /// <summary>Durable outages recorded by the collector (failed probes, sleep, not running).</summary>
        IReadOnlyList<CollectorGap>? Gaps = null,
        /// <summary>The gap log existed but could not be fully read — an unknown number of outages.</summary>
        bool GapEvidenceUnreadable = false,
        /// <summary>Set when the events log or heartbeat could not be read at all.</summary>
        bool HeartbeatUnreadable = false);

    public static Inputs ReadInputs(double windowHours = DefaultWindowHours, DateTimeOffset? now = null)
    {
        var read = File.Exists(BackgroundCollector.EventsPath)
            ? BackgroundCollector.ReadEntriesWithIntegrity()
            : new IncrementalEventRead([], false);
        var heartbeatUnreadable = File.Exists(BackgroundCollector.HeartbeatPath);
        var heartbeat = BackgroundCollector.ReadHeartbeat();
        heartbeatUnreadable = heartbeatUnreadable && heartbeat is null;
        var gaps = BackgroundCollector.ReadGaps((now ?? DateTimeOffset.Now).AddHours(-windowHours));
        DateTimeOffset? trimmedAt = null;
        try
        {
            if (File.Exists(BackgroundCollector.TrimMarkerPath))
            {
                trimmedAt = DateTimeOffset.TryParse(File.ReadAllText(BackgroundCollector.TrimMarkerPath).Trim(), out var t)
                    ? t
                    // A marker we cannot parse means a trim happened at an
                    // unknown time — treated as unreadable evidence, not none.
                    : DateTimeOffset.MaxValue;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unknown whether a trim happened; treated below like other
            // unreadable evidence rather than as "no trim".
            trimmedAt = DateTimeOffset.MaxValue;
        }

        return new Inputs(read.Entries, heartbeat, trimmedAt, FileBaselineStore.Shared, read.SkippedLines,
            gaps.Gaps, gaps.Unreadable, heartbeatUnreadable);
    }

    /// <summary>
    /// Runs the engines over the recorded history inside the window. Returns
    /// null when the recorder has never run here (no entries, no heartbeat) —
    /// the envelope then omits analysis, which is the "never recorded" signal.
    /// </summary>
    public static Result? Run(Inputs inputs, ConnectionSnapshot current, double windowHours = DefaultWindowHours, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var windowStart = at.AddHours(-windowHours);
        var all = inputs.Entries;
        var inWindow = all.Where(entry => entry.At >= windowStart).ToList();

        // The baseline verdict and the live findings first: they describe the
        // present, which no amount of missing history can make unknowable.
        var baseline = EvaluateBaseline(inputs, current, at, windowStart);
        var liveFindings = LiveFindings(current, baseline);

        // Integrity signals are about evidence we *have* but cannot trust.
        // They are collected before the never-recorded shortcut, because a log
        // full of corrupt lines is missing evidence, not an empty machine.
        var integrityReasons = new List<string>();
        if (inputs.SkippedLines > 0) integrityReasons.Add("corrupt-lines");
        if (inputs.GapEvidenceUnreadable) integrityReasons.Add("gap-evidence-unreadable");
        if (inputs.HeartbeatUnreadable) integrityReasons.Add("heartbeat-unreadable");
        // NOT a coverage reason: coverage is temporal (what the recording can
        // vouch for). Whether the baseline could be evaluated is an
        // availability fact, reported next to linkEvents — a complete history
        // with an unknown baseline is a real and sayable state.

        if (inputs.TrimmedAt == DateTimeOffset.MaxValue) integrityReasons.Add("trim-evidence-unreadable");
        var recordedGaps = (inputs.Gaps ?? []).Where(gap => gap.To >= windowStart && gap.From <= at).ToList();

        if (all.Count == 0 && inputs.Heartbeat is null)
        {
            // Never recorded — unless something unreadable says otherwise.
            var reasonsWithoutHistory = integrityReasons.Count > 0 || recordedGaps.Count > 0
                ? integrityReasons.Concat(recordedGaps.Count > 0 ? ["gap"] : []).Distinct().Order().ToList()
                : ["no-history"];

            // With nothing to say at all — no live fault, no unreadable
            // evidence — the envelope carries no analysis (absent ≠ empty).
            if (liveFindings.Count == 0 && integrityReasons.Count == 0 && recordedGaps.Count == 0)
            {
                return null;
            }

            return new Result(liveFindings.OrderBy(Rank).ToList(), [], windowHours, at, at, at, false,
                reasonsWithoutHistory, baseline.State, LinkEventsCapability, Availability(baseline));
        }

        var reasons = new List<string>(integrityReasons);
        var heartbeat = inputs.Heartbeat;
        var lastSample = heartbeat?.LastSampleAt ?? all.LastOrDefault()?.At ?? at;
        // Missing or unreadable heartbeat: nothing proves the recorder was
        // running, so the window cannot be called complete even if events
        // happen to bracket it.
        if (heartbeat is null && all.Count > 0)
        {
            reasons.Add("no-heartbeat");
        }

        // Ran, but not inside this window: say so, with the time of the last
        // evidence, so a consumer shows "unknown" for *history* — while the
        // live findings above still explain the present.
        if (inWindow.Count == 0 && (heartbeat is null || heartbeat.LastSampleAt < windowStart))
        {
            var last = all.LastOrDefault()?.At ?? lastSample;
            return new Result(liveFindings.OrderBy(Rank).ToList(), [], windowHours, at, last, last, false,
                reasons.Append("recorder-stopped-before-window").Distinct().Order().ToList(),
                baseline.State, LinkEventsCapability, Availability(baseline));
        }

        // Coverage: the recorder must have been running from the start of the
        // window to now. The heartbeat's StartedAt is this run's start; entries
        // before it belong to earlier runs, so a run that began inside the
        // window is a hole even if older entries exist.
        // What the recorder can vouch for is the run boundary, not the first
        // change it happened to write: a continuously running collector covers
        // the whole window even if nothing changed for hours.
        var runStart = heartbeat?.StartedAt;
        var availableFrom = runStart is { } start && start > windowStart
            ? start
            : runStart is not null ? windowStart : inWindow.FirstOrDefault()?.At ?? windowStart;
        if (runStart is { } inside && inside > windowStart + GapTolerance)
        {
            reasons.Add("recorder-started-inside-window");
        }

        if (at - lastSample > GapTolerance)
        {
            // Stopped (or hung) before now: the tail of the window is unobserved.
            reasons.Add("gap");
        }

        if (inputs.TrimmedAt is { } trimmedAt && trimmedAt != DateTimeOffset.MaxValue && trimmedAt > windowStart)
        {
            reasons.Add("trimmed");
        }

        // Durable outages: any recorded stretch overlapping this window is a
        // hole, even if the heartbeat now looks healthy.
        if (recordedGaps.Count > 0)
        {
            reasons.Add("gap");
        }

        var through = lastSample > at ? at : lastSample;

        var incidents = IncidentStitcher.Stitch(all)
            .Where(incident => incident.End >= windowStart)
            .OrderByDescending(incident => incident.Start)
            .Select(incident => ContractV1.ToIncident(incident, all))
            .ToList();

        var findings = new List<Finding>();
        findings.AddRange(SustainedDeficit(inWindow, at));
        findings.AddRange(GroupedLoss(incidents, all));
        findings.AddRange(liveFindings.Where(finding => findings.All(existing => existing.Title != finding.Title)));

        var ranked = findings.OrderBy(Rank).ThenBy(finding => finding.Title, StringComparer.Ordinal).ToList();
        return new Result(ranked, incidents, windowHours, at, availableFrom, through,
            reasons.Count == 0, reasons.Distinct().Order().ToList(), baseline.State, LinkEventsCapability, Availability(baseline));
    }

    /// <summary>
    /// Windows derives no link events yet: no ETW session, no USB4 router
    /// facts (change <c>windows-event-ingest</c>). This is an attribution
    /// capability, not a coverage reason — a gap-free device history stays
    /// <c>complete</c> and can still say "no device findings".
    /// </summary>
    public const string LinkEventsCapability = "unavailable";

    private static string Availability(BaselineEvaluation baseline) => baseline.UnavailableReason ?? "available";

    // MARK: - Engines

    /// <summary>
    /// A deficit period is deficitStarted → deficitEnded (or → now). It becomes
    /// a finding when it lasted at least two samples and reached at least the
    /// sustained threshold on any recorded power sample inside it. Windows
    /// records power only on change entries, so the peak is the deepest rate
    /// the recorder happened to write — stated as such in the evidence.
    /// </summary>
    public static IReadOnlyList<Finding> SustainedDeficit(IReadOnlyList<RecorderEntry> entries, DateTimeOffset now)
    {
        var findings = new List<Finding>();
        DateTimeOffset? start = null;
        var deepest = 0;
        void Close(DateTimeOffset end)
        {
            if (start is null)
            {
                return;
            }

            var duration = end - start.Value;
            if (duration >= SustainedDeficitMinimum && deepest <= -SustainedDeficitMilliwatts)
            {
                var watts = Math.Abs(deepest) / 1000.0;
                findings.Add(new Finding(
                    "critical",
                    "Power supply under-served",
                    "The battery covered part of the machine's demand while Windows reported AC power, for long enough " +
                    "that this is not a transient. When demand exceeds supply, USB-C power delivery renegotiates, and a " +
                    "renegotiation resets the port and everything behind it.",
                    "Use a higher-rated supply, or move power-hungry devices off the dock; if the dock is the supply, " +
                    "check its adapter rating against the laptop's demand.",
                    [
                        $"Deficit from {start.Value:HH:mm:ss} to {end:HH:mm:ss} ({duration.TotalSeconds:F0} s, ≥ {SustainedDeficitMinimum.TotalSeconds:F0} s required)",
                        $"Battery supplied up to {watts:F1} W on a recorded sample (threshold {SustainedDeficitMilliwatts / 1000.0:F0} W)",
                        "Windows reported AC power connected throughout"
                    ],
                    "high"));
            }

            start = null;
            deepest = 0;
        }

        foreach (var entry in entries.OrderBy(entry => entry.At))
        {
            var rate = entry.Power?.BatteryRateMilliwatts;
            switch (entry.Kind)
            {
                case RecorderEntryKinds.DeficitStarted:
                    start ??= entry.At;
                    if (rate is not null && rate < deepest) deepest = rate.Value;
                    break;
                case RecorderEntryKinds.DeficitEnded:
                    if (rate is not null && rate < deepest) deepest = rate.Value;
                    Close(entry.At);
                    break;
                case RecorderEntryKinds.DeficitDeepened:
                default:
                    if (start is not null && rate is not null && rate < deepest) deepest = rate.Value;
                    break;
            }
        }

        Close(now); // an open deficit runs to now
        return findings;
    }

    /// <summary>
    /// Several devices lost together behind one resolved shared parent is one
    /// upstream failure, not several device failures — the same finding
    /// TBDoctor raises, from the same evidence (the incident's sharedParent).
    /// </summary>
    public static IReadOnlyList<Finding> GroupedLoss(IReadOnlyList<ContractIncident> incidents, IReadOnlyList<RecorderEntry> recording)
    {
        var worst = incidents
            .Where(incident => incident.SharedParent is not null && incident.DevicesLost.Count >= 2)
            .OrderByDescending(incident => incident.DevicesLost.Count)
            .FirstOrDefault();
        if (worst is null)
        {
            return [];
        }

        var parentName = recording
            .SelectMany(entry => (entry.Snapshot?.Devices ?? Array.Empty<DeviceNode>()).Concat(entry.Device is null ? [] : [entry.Device]))
            .FirstOrDefault(device => worst.SharedParent!.EndsWith(device.InstanceId, StringComparison.OrdinalIgnoreCase))
            ?.FriendlyName ?? worst.SharedParent!;
        var names = string.Join(", ", worst.DevicesLost.Select(device => device.Name).Take(4));

        return
        [
            new Finding(
                "warning",
                "Devices lost as a group",
                "Several devices vanished at the same instant and they all sit behind a single hub. That is one " +
                "upstream failure, not several device failures — the individual devices are almost certainly innocent.",
                "Investigate the shared parent, not the individual devices.",
                [
                    $"{worst.DevicesLost.Count} devices lost together at {worst.Start:HH:mm:ss}: {names}",
                    $"All behind {parentName} ({worst.SharedParent})"
                ],
                "high")
        ];
    }

    /// <summary>
    /// What the live state alone says: the power reading now, plus the baseline
    /// verdict passed in (evaluated once, under the lock).
    /// </summary>
    public static IReadOnlyList<Finding> LiveFindings(ConnectionSnapshot current, BaselineEvaluation baseline)
    {
        var findings = new List<Finding>(PowerDiagnosis.Analyze(current.Power));
        findings.AddRange(baseline.Findings.Where(finding => findings.All(existing => existing.Title != finding.Title)));
        return findings;
    }

    /// <summary>
    /// The baseline verdict: the snapshot, the comparison against the current
    /// state, the findings that explain it, and the state after the history
    /// update — all decided inside ONE hold of the baseline lock. Reading the
    /// baseline outside the lock and writing the history inside it was the bug
    /// this shape removes: a replacement landing in between could have its
    /// fresh history overwritten by a fault derived from the discarded baseline.
    /// </summary>
    /// <summary>
    /// The baseline verdict, or why there is none. <see cref="State"/> is null
    /// when nothing can be said — the lock was busy, the file unreadable, or a
    /// transition could not be persisted — and the envelope then omits
    /// `analysis.baseline` entirely rather than publishing a state that is not
    /// backed by what is on disk.
    /// </summary>
    public sealed record BaselineEvaluation(
        ContractBaselineState? State,
        IReadOnlyList<Finding> Findings,
        string? UnavailableReason = null);

    public static BaselineEvaluation EvaluateBaseline(Inputs inputs, ConnectionSnapshot current, DateTimeOffset now, DateTimeOffset windowStart)
    {
        var store = inputs.Store ?? FileBaselineStore.Shared;
        var evaluation = new BaselineEvaluation(new ContractBaselineState { State = "no-baseline" }, []);

        var acquired = store.WithLock(() =>
        {
            var read = store.ReadBaseline();
            if (read.Unreadable)
            {
                // Unknown: not healthy, not absent. Publish no state at all.
                evaluation = new BaselineEvaluation(null, [], "unreadable");
                return;
            }

            if (read.Baseline is not { } baseline)
            {
                return;
            }

            var report = SnapshotComparer.Compare(baseline, current);
            var faulted = report.Findings.Count > 0 || report.Missing.Count > 0;

            var stored = store.ReadHistory();
            if (store.HistoryUnreadable)
            {
                evaluation = new BaselineEvaluation(null, BaselineFindings(baseline, report), "history-unreadable");
                return;
            }

            // A history that names a different baseline is left over from a
            // crash between the two writes: it describes a snapshot that is no
            // longer there, so it starts again rather than being reported.
            var history = stored is null || (stored.BaselineCapturedAt is { } stamp && stamp != baseline.CapturedAt)
                ? new BaselineStateFile(BaselineCapturedAt: baseline.CapturedAt)
                : stored with { BaselineCapturedAt = baseline.CapturedAt };

            var updated = faulted
                ? history with { FaultSince = history.FaultSince ?? now, RecoveredAt = null }
                : history.FaultSince is not null
                    ? new BaselineStateFile(null, now, history.FaultSince)
                    : history;
            if (updated != (stored ?? history) && !store.WriteHistory(updated))
            {
                // The transition could not be persisted: publishing it would
                // report a fault time (or a recovery) that vanishes on the next
                // request. The findings still stand — they come from the
                // comparison, not from the history.
                evaluation = new BaselineEvaluation(null, BaselineFindings(baseline, report), "history-unwritable");
                return;
            }

            var state = faulted ? "active-fault"
                : updated.RecoveredAt is { } recoveredAt && recoveredAt >= windowStart ? "recovered"
                : "healthy";

            evaluation = new BaselineEvaluation(
                new ContractBaselineState
                {
                    State = state,
                    CapturedAt = baseline.CapturedAt,
                    FaultSince = state == "active-fault" ? updated.FaultSince : state == "recovered" ? updated.LastFaultSince : null,
                    RecoveredAt = state == "recovered" ? updated.RecoveredAt : null
                },
                BaselineFindings(baseline, report));
        });

        // The lock is held by a replacement in progress: we cannot read the
        // pair consistently, so we say nothing about it rather than defaulting
        // to "no baseline".
        return acquired ? evaluation : new BaselineEvaluation(null, [], "busy");
    }

    /// <summary>
    /// Every condition that makes the state `active-fault` produces a finding,
    /// so a fault is never reported without evidence and a recommendation.
    /// </summary>
    private static IReadOnlyList<Finding> BaselineFindings(ConnectionSnapshot baseline, ComparisonReport report)
    {
        var findings = new List<Finding>(report.Findings);
        // Only the devices no existing finding accounts for. A power deficit
        // among the findings does not explain a vanished dock, so suppressing
        // on "any finding exists" would hide the fault the baseline state is
        // actually reporting.
        var unexplained = report.UnexplainedMissing;
        if (unexplained.Count == 0)
        {
            return findings;
        }

        var names = unexplained.Select(device => device.VidPid is null
            ? device.FriendlyName
            : $"{device.FriendlyName} [{device.VidPid}]").Take(6).ToList();
        findings.Add(new Finding(
            "warning",
            "Devices from the known-good baseline are missing",
            $"{unexplained.Count} device(s) present when the baseline was captured " +
            $"({baseline.CapturedAt:yyyy-MM-dd HH:mm}) are not present now. That is the difference between " +
            "this desk working and not working, whatever caused it.",
            "Check the branch these sit behind — the cable, the hub, or the port they share — before suspecting the devices themselves.",
            [
                $"Missing since the baseline: {string.Join(", ", names)}" + (unexplained.Count > names.Count ? $" (+{unexplained.Count - names.Count} more)" : string.Empty),
                $"Baseline captured {baseline.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}",
                $"{report.Added.Count} device(s) present now that were not in the baseline"
            ],
            "high"));
        return findings;
    }

    // MARK: - Contract shapes

    public static void Attach(ContractEnvelope envelope, Result? result, out ContractEnvelope withAnalysis)
    {
        withAnalysis = result is null
            ? envelope
            : envelope with
            {
                Findings = result.Findings.Select(ContractV1.ToFinding).ToList(),
                Incidents = result.Incidents,
                Analysis = ToAnalysis(result)
            };
    }

    public static ContractAnalysis ToAnalysis(Result result) => new()
    {
        WindowHours = result.WindowHours,
        GeneratedAt = result.GeneratedAt,
        Coverage = new ContractCoverage
        {
            AvailableFrom = result.AvailableFrom,
            Through = result.Through,
            Complete = result.Complete,
            Reasons = result.Reasons.Count == 0 ? null : result.Reasons
        },
        Baseline = result.Baseline,
        Capabilities = new ContractCapabilities
        {
            LinkEvents = result.LinkEvents,
            Baseline = result.BaselineAvailability
        }
    };

    private static int Rank(Finding finding) => finding.Severity switch
    {
        "critical" => 0,
        "warning" => 1,
        _ => 2
    };
}

/// <summary>
/// Where the baseline fault history is kept, and the lock that keeps it
/// consistent with the baseline itself. One implementation writes the file; a
/// test can supply its own so analysis never touches the real machine's state.
/// </summary>
/// <summary>
/// The baseline and the fault history that describes it, behind one lock. They
/// are one evidence boundary: comparing against a baseline that was replaced
/// mid-analysis, or writing a fault derived from a discarded baseline, are the
/// same bug. Injectable so tests never touch the machine's own state.
/// </summary>
internal interface IBaselineStore
{
    /// <summary>The known-good snapshot, or a reason it could not be read.</summary>
    BaselineRead ReadBaseline();
    /// <summary>The fault history, or null when there is none or it is unreadable (see HistoryUnreadable).</summary>
    BaselineStateFile? ReadHistory();
    bool HistoryUnreadable { get; }
    bool WriteHistory(BaselineStateFile state);
    /// <summary>Runs <paramref name="work"/> holding the same lock a baseline replacement takes; false if the lock could not be taken.</summary>
    bool WithLock(Action work);
}

/// <summary>The baseline, or why it is unavailable. Unreadable is not absent.</summary>
internal sealed record BaselineRead(ConnectionSnapshot? Baseline, bool Unreadable = false);

internal sealed class FileBaselineStore : IBaselineStore
{
    public static readonly FileBaselineStore Shared = new();

    public BaselineRead ReadBaseline()
    {
        if (!File.Exists(SnapshotStore.DefaultBaselinePath))
        {
            return new BaselineRead(null);
        }

        try
        {
            return new BaselineRead(SnapshotStore.Load(SnapshotStore.DefaultBaselinePath));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            // A baseline we cannot read is unknown, not absent — and never an
            // exception out of a request handler.
            return new BaselineRead(null, Unreadable: true);
        }
    }

    public bool HistoryUnreadable { get; private set; }

    public BaselineStateFile? ReadHistory()
    {
        var read = BaselineStateFile.ReadOrFail();
        HistoryUnreadable = read.Unreadable;
        return read.State;
    }

    public bool WriteHistory(BaselineStateFile state) => BaselineStateFile.Write(state);
    public bool WithLock(Action work) => BaselineTransaction.WithLock(work);
}

/// <summary>
/// The baseline comparison's transitions, remembered on disk so "recovered
/// since fault" is a state the collector can report rather than something a
/// single call would have to guess. Written only when the state changes.
/// </summary>
internal sealed record BaselineStateFile(
    DateTimeOffset? FaultSince = null,
    DateTimeOffset? RecoveredAt = null,
    DateTimeOffset? LastFaultSince = null,
    /// <summary>
    /// The capture time of the baseline this history describes. Two files
    /// cannot be written in one atomic step, so instead of a journal the
    /// history *names its baseline*: after a crash between the two writes, a
    /// history whose stamp does not match the baseline on disk is recognisably
    /// stale and is discarded rather than reported as that baseline's history.
    /// </summary>
    DateTimeOffset? BaselineCapturedAt = null)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static string Path => System.IO.Path.Combine(BackgroundCollector.DataDirectory, "baseline-state.json");

    /// <summary>The history, plus whether it exists-but-could-not-be-read (which is not "none").</summary>
    public static (BaselineStateFile? State, bool Unreadable) ReadOrFail()
    {
        if (!File.Exists(Path))
        {
            return (null, false);
        }

        try
        {
            var state = JsonSerializer.Deserialize<BaselineStateFile>(File.ReadAllText(Path), Options);
            // A file containing `null` deserialises without throwing; it is a
            // history we cannot read, not the absence of one.
            return state is null ? (null, true) : (state, false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, true);
        }
    }

    public static BaselineStateFile? Read() => ReadOrFail().State;

    /// <summary>
    /// True when the state was persisted. Written atomically (temp then
    /// replace) so a crash or a full disk cannot leave a half-written history
    /// beside a committed baseline.
    /// </summary>
    public static bool Write(BaselineStateFile state)
    {
        try
        {
            Directory.CreateDirectory(BackgroundCollector.DataDirectory);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
            if (File.Exists(Path))
            {
                File.Replace(temporary, Path, null);
            }
            else
            {
                File.Move(temporary, Path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
