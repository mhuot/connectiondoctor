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
        ContractBaselineState Baseline,
        string LinkEvents);

    /// <summary>What the analysis reads; injectable so tests need no disk.</summary>
    public sealed record Inputs(
        IReadOnlyList<RecorderEntry> Entries,
        CollectorHeartbeat? Heartbeat,
        DateTimeOffset? TrimmedAt,
        ConnectionSnapshot? Baseline,
        BaselineStateFile? BaselineHistory,
        /// <summary>Lines of the event log that could not be parsed — corrupt evidence, not absence.</summary>
        int SkippedLines = 0,
        /// <summary>Durable outages recorded by the collector (failed probes, sleep, not running).</summary>
        IReadOnlyList<CollectorGap>? Gaps = null,
        /// <summary>The gap log existed but could not be fully read — an unknown number of outages.</summary>
        bool GapEvidenceUnreadable = false,
        /// <summary>
        /// Where the baseline fault history lives. Defaults to the file beside
        /// the baseline, updated under the baseline lock; injectable so tests
        /// (and any future caller with its own storage) do not touch it.
        /// </summary>
        IBaselineStateStore? StateStore = null);

    public static Inputs ReadInputs(double windowHours = DefaultWindowHours, DateTimeOffset? now = null)
    {
        var read = File.Exists(BackgroundCollector.EventsPath)
            ? BackgroundCollector.ReadEntriesWithIntegrity()
            : new IncrementalEventRead([], false);
        var heartbeat = BackgroundCollector.ReadHeartbeat();
        var gaps = BackgroundCollector.ReadGaps((now ?? DateTimeOffset.Now).AddHours(-windowHours));
        DateTimeOffset? trimmedAt = null;
        if (File.Exists(BackgroundCollector.TrimMarkerPath) &&
            DateTimeOffset.TryParse(File.ReadAllText(BackgroundCollector.TrimMarkerPath).Trim(), out var t))
        {
            trimmedAt = t;
        }

        ConnectionSnapshot? baseline = null;
        if (File.Exists(SnapshotStore.DefaultBaselinePath))
        {
            try
            {
                baseline = SnapshotStore.Load(SnapshotStore.DefaultBaselinePath);
            }
            catch (InvalidDataException)
            {
                baseline = null;
            }
        }

        return new Inputs(read.Entries, heartbeat, trimmedAt, baseline, BaselineStateFile.Read(), read.SkippedLines,
            gaps.Gaps, gaps.Unreadable);
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

        if (all.Count == 0 && inputs.Heartbeat is null)
        {
            // Never recorded. Still say what the present shows: a fresh install
            // with a power deficit or a baseline mismatch has a real fault to
            // explain, and "no history" must not swallow it. With nothing to
            // say either, the envelope carries no analysis at all (absent ≠ empty).
            var live = LiveFindings(inputs, current);
            if (live.Count == 0)
            {
                return null;
            }

            return new Result(live.OrderBy(Rank).ToList(), [], windowHours, at, at, at, false, ["no-history"],
                BaselineState(inputs, current, at, windowStart), LinkEventsCapability);
        }

        var reasons = new List<string>();
        var heartbeat = inputs.Heartbeat;
        var lastSample = heartbeat?.LastSampleAt ?? all.LastOrDefault()?.At ?? at;
        // Missing or unreadable heartbeat: nothing proves the recorder was
        // running, so the window cannot be called complete even if events
        // happen to bracket it.
        if (heartbeat is null && all.Count > 0)
        {
            reasons.Add("no-heartbeat");
        }

        // Live diagnosis is about *now*: the power state in front of us and
        // the comparison against the known-good baseline. It does not depend on
        // the recording, so it is computed before any coverage decision — a
        // stale recorder must never suppress the fault the user is looking at
        // (found on a real Surface: active-fault with findings: []).
        var liveFindings = LiveFindings(inputs, current);

        // Ran, but not inside this window: say so, with the time of the last
        // evidence, so a consumer shows "unknown" for *history* — while the
        // live findings above still explain the present.
        if (inWindow.Count == 0 && (heartbeat is null || heartbeat.LastSampleAt < windowStart))
        {
            var last = all.LastOrDefault()?.At ?? lastSample;
            return new Result(liveFindings.OrderBy(Rank).ToList(), [], windowHours, at, last, last, false,
                ["recorder-stopped-before-window"],
                BaselineState(inputs, current, at, windowStart), LinkEventsCapability);
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

        if (inputs.TrimmedAt is { } trimmedAt && trimmedAt > windowStart)
        {
            reasons.Add("trimmed");
        }

        // Durable outages: any recorded stretch overlapping this window is a
        // hole, even if the heartbeat now looks healthy.
        if ((inputs.Gaps ?? []).Any(gap => gap.To >= windowStart && gap.From <= at))
        {
            reasons.Add("gap");
        }

        // Outage evidence we could not read is itself a reason: we cannot rule
        // out a gap we cannot see.
        if (inputs.GapEvidenceUnreadable)
        {
            reasons.Add("gap-evidence-unreadable");
        }

        // Corrupt lines are missing evidence, not quiet: never claim complete.
        if (inputs.SkippedLines > 0)
        {
            reasons.Add("corrupt-lines");
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
        var baselineState = BaselineState(inputs, current, at, windowStart);
        findings.AddRange(liveFindings.Where(finding => findings.All(existing => existing.Title != finding.Title)));

        var ranked = findings.OrderBy(Rank).ThenBy(finding => finding.Title, StringComparer.Ordinal).ToList();
        return new Result(ranked, incidents, windowHours, at, availableFrom, through,
            reasons.Count == 0, reasons.Distinct().Order().ToList(), baselineState, LinkEventsCapability);
    }

    /// <summary>
    /// Windows derives no link events yet: no ETW session, no USB4 router
    /// facts (change <c>windows-event-ingest</c>). This is an attribution
    /// capability, not a coverage reason — a gap-free device history stays
    /// <c>complete</c> and can still say "no device findings".
    /// </summary>
    public const string LinkEventsCapability = "unavailable";

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
    /// What can be said from the current state alone: the live power reading
    /// and the known-good comparison. Every condition that makes the baseline
    /// state `active-fault` produces a finding here, so a fault is never
    /// reported without evidence and a recommendation.
    /// </summary>
    public static IReadOnlyList<Finding> LiveFindings(Inputs inputs, ConnectionSnapshot current)
    {
        var findings = new List<Finding>(PowerDiagnosis.Analyze(current.Power));
        if (inputs.Baseline is null)
        {
            return findings;
        }

        var report = SnapshotComparer.Compare(inputs.Baseline, current);
        findings.AddRange(report.Findings.Where(finding => findings.All(existing => existing.Title != finding.Title)));

        // The comparer raises specific findings for the signatures it knows
        // (a display alive while its hub branch is gone). Anything else that
        // is missing still has to be said, or the baseline state would claim a
        // fault the panel cannot explain.
        if (report.Missing.Count > 0 && report.Findings.Count == 0)
        {
            var names = report.Missing.Select(device => device.VidPid is null
                ? device.FriendlyName
                : $"{device.FriendlyName} [{device.VidPid}]").Take(6).ToList();
            findings.Add(new Finding(
                "warning",
                "Devices from the known-good baseline are missing",
                $"{report.Missing.Count} device(s) present when the baseline was captured " +
                $"({inputs.Baseline.CapturedAt:yyyy-MM-dd HH:mm}) are not present now. That is the difference between " +
                "this desk working and not working, whatever caused it.",
                "Check the branch these sit behind — the cable, the hub, or the port they share — before suspecting the devices themselves.",
                [
                    $"Missing since the baseline: {string.Join(", ", names)}" + (report.Missing.Count > names.Count ? $" (+{report.Missing.Count - names.Count} more)" : string.Empty),
                    $"Baseline captured {inputs.Baseline.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}",
                    $"{report.Added.Count} device(s) present now that were not in the baseline"
                ],
                "high"));
        }

        return findings;
    }

    // MARK: - Baseline state

    /// <summary>
    /// no-baseline / healthy / active-fault / recovered, with the transition
    /// remembered on disk so "recovered since fault" survives across calls.
    /// Absence of a baseline is never health.
    /// </summary>
    public static ContractBaselineState BaselineState(Inputs inputs, ConnectionSnapshot current, DateTimeOffset now, DateTimeOffset windowStart)
    {
        if (inputs.Baseline is null)
        {
            return new ContractBaselineState { State = "no-baseline" };
        }

        var report = SnapshotComparer.Compare(inputs.Baseline, current);
        var faulted = report.Findings.Count > 0 || report.Missing.Count > 0;
        // Read and update the history under the baseline lock: a replacement
        // resets it, and an analysis that started before the replacement must
        // not write the old fault back afterwards.
        var store = inputs.StateStore ?? FileBaselineStateStore.Shared;
        var next = inputs.BaselineHistory ?? new BaselineStateFile();
        store.WithLock(() =>
        {
            var history = store.Read() ?? inputs.BaselineHistory ?? new BaselineStateFile();
            var updated = faulted
                ? history with { FaultSince = history.FaultSince ?? now, RecoveredAt = null }
                : history.FaultSince is not null
                    ? new BaselineStateFile(null, now, history.FaultSince)
                    : history;
            if (updated != history)
            {
                store.Write(updated);
            }

            next = updated;
        });

        var state = faulted ? "active-fault"
            : next.RecoveredAt is { } recoveredAt && recoveredAt >= windowStart ? "recovered"
            : "healthy";
        return new ContractBaselineState
        {
            State = state,
            CapturedAt = inputs.Baseline.CapturedAt,
            FaultSince = state == "active-fault" ? next.FaultSince : state == "recovered" ? next.LastFaultSince : null,
            RecoveredAt = state == "recovered" ? next.RecoveredAt : null
        };
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
        Capabilities = new ContractCapabilities { LinkEvents = result.LinkEvents }
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
internal interface IBaselineStateStore
{
    BaselineStateFile? Read();
    bool Write(BaselineStateFile state);
    /// <summary>Runs <paramref name="work"/> holding the same lock a baseline replacement takes.</summary>
    void WithLock(Action work);
}

internal sealed class FileBaselineStateStore : IBaselineStateStore
{
    public static readonly FileBaselineStateStore Shared = new();
    public BaselineStateFile? Read() => BaselineStateFile.Read();
    public bool Write(BaselineStateFile state) => BaselineStateFile.Write(state);
    public void WithLock(Action work) => BaselineTransaction.WithLock(work);
}

/// <summary>
/// The baseline comparison's transitions, remembered on disk so "recovered
/// since fault" is a state the collector can report rather than something a
/// single call would have to guess. Written only when the state changes.
/// </summary>
internal sealed record BaselineStateFile(
    DateTimeOffset? FaultSince = null,
    DateTimeOffset? RecoveredAt = null,
    DateTimeOffset? LastFaultSince = null)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static string Path => System.IO.Path.Combine(BackgroundCollector.DataDirectory, "baseline-state.json");

    public static BaselineStateFile? Read()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<BaselineStateFile>(File.ReadAllText(Path), Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the state was persisted; callers that depend on it (a baseline replacement) fail loudly.</summary>
    public static bool Write(BaselineStateFile state)
    {
        try
        {
            Directory.CreateDirectory(BackgroundCollector.DataDirectory);
            File.WriteAllText(Path, JsonSerializer.Serialize(state, Options));
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
