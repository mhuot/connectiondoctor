namespace ConnectionDoctor.Tests;

/// <summary>
/// The Windows history engine over synthetic recordings — no disk, no hardware.
/// The rules under test are the ones that decide whether a reader may say
/// "nothing wrong": coverage, absent-vs-empty, and evidence for each finding.
/// </summary>
public sealed class WindowsAnalysisTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-17T12:00:00-05:00");
    private static ConnectionSnapshot Current(PowerState? power = null) =>
        SnapshotComparerTests.Snapshot() with { CapturedAt = Now, Power = power ?? new PowerState(true, 100, 0) };
    private static CollectorHeartbeat Beat(DateTimeOffset started, DateTimeOffset lastSample) =>
        new(1234, started, lastSample, "events.jsonl");
    private static WindowsAnalysis.Inputs Inputs(IReadOnlyList<RecorderEntry> entries, CollectorHeartbeat? beat, DateTimeOffset? trimmed = null) =>
        new(entries, beat, trimmed, new MemoryBaselineStore());

    [Fact]
    public void NoRecordingAtAllProducesNoAnalysisAtAll()
    {
        // Absent ≠ empty: a machine that never recorded says nothing, so the
        // envelope carries no analysis and a reader cannot mistake it for "clear".
        Assert.Null(WindowsAnalysis.Run(Inputs([], null), Current(), now: Now));
    }

    [Fact]
    public void RecorderRunningTheWholeWindowIsComplete()
    {
        var beat = Beat(Now.AddHours(-24), Now.AddSeconds(-3));
        var entries = new[] { new RecorderEntry(Now.AddHours(-3), RecorderEntryKinds.DeviceAppeared, SnapshotComparerTests.Device(@"USB\VID_046D&PID_C08A\M", "Mouse", "Mouse"), new PowerState(true, 100, 0), null) };
        var result = WindowsAnalysis.Run(Inputs(entries, beat), Current(), 6, Now)!;

        Assert.True(result.Complete);
        Assert.Empty(result.Reasons);
        Assert.Equal("unavailable", result.LinkEvents); // no kernel link events on Windows yet
        Assert.Equal("no-baseline", Assert.IsType<ContractBaselineState>(result.Baseline).State);
    }

    [Fact]
    public void RecorderStartedInsideTheWindowOrStoppedEarlyIsIncomplete()
    {
        var started = WindowsAnalysis.Run(Inputs(
            [new RecorderEntry(Now.AddMinutes(-30), RecorderEntryKinds.DeviceAppeared, null, new PowerState(true, 100, 0), null)],
            Beat(Now.AddMinutes(-31), Now.AddSeconds(-2))), Current(), 6, Now)!;
        Assert.False(started.Complete);
        Assert.Contains("recorder-started-inside-window", started.Reasons);

        var stopped = WindowsAnalysis.Run(Inputs(
            [new RecorderEntry(Now.AddHours(-2), RecorderEntryKinds.DeviceAppeared, null, new PowerState(true, 100, 0), null)],
            Beat(Now.AddHours(-24), Now.AddHours(-2))), Current(), 6, Now)!;
        Assert.False(stopped.Complete);
        Assert.Contains("gap", stopped.Reasons);
    }

    [Fact]
    public void RanButNotInsideTheWindowSaysSoWithTheLastEvidenceTime()
    {
        var lastEvidence = Now.AddDays(-2);
        var result = WindowsAnalysis.Run(Inputs(
            [new RecorderEntry(lastEvidence, RecorderEntryKinds.DeviceAppeared, null, new PowerState(true, 100, 0), null)],
            Beat(lastEvidence.AddHours(-1), lastEvidence)), Current(), 6, Now)!;

        Assert.False(result.Complete);
        Assert.Equal(["recorder-stopped-before-window"], result.Reasons);
        Assert.Equal(lastEvidence, result.Through);
        Assert.Empty(result.Findings);   // empty, but the window is not vouched for
    }

    [Fact]
    public void TrimInsideTheWindowIsACoverageReason()
    {
        var result = WindowsAnalysis.Run(Inputs(
            [new RecorderEntry(Now.AddHours(-1), RecorderEntryKinds.DeviceAppeared, null, new PowerState(true, 100, 0), null)],
            Beat(Now.AddHours(-24), Now.AddSeconds(-2)), trimmed: Now.AddHours(-2)), Current(), 6, Now)!;

        Assert.False(result.Complete);
        Assert.Contains("trimmed", result.Reasons);
    }

    [Fact]
    public void SustainedDeepDeficitIsCriticalWithEvidence()
    {
        var entries = new[]
        {
            new RecorderEntry(Now.AddMinutes(-10), RecorderEntryKinds.DeficitStarted, null, new PowerState(true, 96, -12000), null),
            new RecorderEntry(Now.AddMinutes(-9), RecorderEntryKinds.DeviceDisappeared, null, new PowerState(true, 95, -14500), null),
            new RecorderEntry(Now.AddMinutes(-8), RecorderEntryKinds.DeficitEnded, null, new PowerState(true, 94, -3000), null)
        };
        var finding = Assert.Single(WindowsAnalysis.SustainedDeficit(entries, Now));

        Assert.Equal("critical", finding.Severity);
        Assert.Equal("Power supply under-served", finding.Title);
        Assert.Contains(finding.Evidence, line => line.Contains("14.5 W"));
        Assert.NotEmpty(finding.Recommendation);
    }

    [Fact]
    public void ShallowOrBriefDeficitsAreNotFindings()
    {
        // Shallow: the -2 W event threshold fires, but nowhere near sustained depth.
        Assert.Empty(WindowsAnalysis.SustainedDeficit(
        [
            new RecorderEntry(Now.AddMinutes(-5), RecorderEntryKinds.DeficitStarted, null, new PowerState(true, 99, -2500), null),
            new RecorderEntry(Now.AddMinutes(-4), RecorderEntryKinds.DeficitEnded, null, new PowerState(true, 99, -2100), null)
        ], Now));

        // Deep but momentary: one sample, gone before the next.
        Assert.Empty(WindowsAnalysis.SustainedDeficit(
        [
            new RecorderEntry(Now.AddSeconds(-9), RecorderEntryKinds.DeficitStarted, null, new PowerState(true, 99, -30000), null),
            new RecorderEntry(Now.AddSeconds(-8), RecorderEntryKinds.DeficitEnded, null, new PowerState(true, 99, -100), null)
        ], Now));
    }

    [Fact]
    public void GroupedLossNamesTheSharedParentAndOnlyFiresWhenOneIsResolved()
    {
        var hub = SnapshotComparerTests.Device(@"USB\VID_043E&PID_9C04\HUB", "USB", "Generic USB Hub");
        var keyboard = SnapshotComparerTests.Device(@"HID\VID_046D&PID_C08A&MI_01\KB", "Keyboard", "HID Keyboard Device", hub.InstanceId);
        var mouse = SnapshotComparerTests.Device(@"HID\VID_046D&PID_C08A&MI_00\M", "Mouse", "HID-compliant mouse", hub.InstanceId);
        RecorderEntry[] recording =
        [
            RecorderEntry.FullSnapshot(SnapshotComparerTests.Snapshot(hub, keyboard, mouse) with { CapturedAt = Now.AddMinutes(-20) }),
            new RecorderEntry(Now.AddMinutes(-10), RecorderEntryKinds.DeviceDisappeared, keyboard, new PowerState(true, 100, 0), null),
            new RecorderEntry(Now.AddMinutes(-10).AddSeconds(1), RecorderEntryKinds.DeviceDisappeared, mouse, new PowerState(true, 100, 0), null)
        ];
        var incidents = IncidentStitcher.Stitch(recording).Select(i => ContractV1.ToIncident(i, recording)).ToList();

        var finding = Assert.Single(WindowsAnalysis.GroupedLoss(incidents, recording));
        Assert.Equal("warning", finding.Severity);
        Assert.Contains(finding.Evidence, line => line.Contains("Generic USB Hub"));

        // Unresolved parent → no grouped-loss claim.
        var unresolved = incidents.Select(incident => incident with { SharedParent = null }).ToList();
        Assert.Empty(WindowsAnalysis.GroupedLoss(unresolved, recording));
    }

    [Fact]
    public void AnalysisAttachesToTheEnvelopeAsTheContractShapes()
    {
        var beat = Beat(Now.AddHours(-24), Now.AddSeconds(-2));
        var result = WindowsAnalysis.Run(Inputs(
            [new RecorderEntry(Now.AddMinutes(-30), RecorderEntryKinds.DeficitStarted, null, new PowerState(true, 90, -15000), null),
             new RecorderEntry(Now.AddMinutes(-20), RecorderEntryKinds.DeficitEnded, null, new PowerState(true, 90, -1000), null)],
            beat), Current(), 6, Now)!;
        WindowsAnalysis.Attach(ContractV1.ToEnvelope(Current()), result, out var envelope);

        var json = System.Text.Json.Nodes.JsonNode.Parse(ContractV1.Serialize(envelope))!;
        Assert.Equal("critical", json["findings"]![0]!["severity"]!.GetValue<string>());
        Assert.NotEmpty(json["findings"]![0]!["evidence"]!.AsArray());
        Assert.NotNull(json["findings"]![0]!["recommendation"]);
        Assert.True(json["analysis"]!["coverage"]!["complete"]!.GetValue<bool>());
        Assert.Equal("no-baseline", json["analysis"]!["baseline"]!["state"]!.GetValue<string>());
        Assert.Equal("unavailable", json["analysis"]!["capabilities"]!["linkEvents"]!.GetValue<string>());
        Assert.NotNull(json["incidents"]);
    }

    [Theory]
    // Origin must equal the scheme+authority the request arrived on — these are
    // distinct browser origins, and a page on one must not drive the other.
    [InlineData("http://localhost:8787", "http://localhost:8787/baseline", true)]
    [InlineData("http://127.0.0.1:8787", "http://localhost:8787/baseline", false)]
    [InlineData("http://localhost:8787", "http://127.0.0.1:8787/baseline", false)]
    [InlineData("https://localhost:8787", "http://localhost:8787/baseline", false)]
    [InlineData("http://localhost:8788", "http://localhost:8787/baseline", false)]
    [InlineData("null", "http://localhost:8787/baseline", false)]
    [InlineData(null, "http://localhost:8787/baseline", false)]
    public void OriginMustMatchTheRequestOriginExactly(string? origin, string requestUrl, bool expected) =>
        Assert.Equal(expected, ContractServer.IsSameOrigin(origin, new Uri(requestUrl)));

    [Theory]
    // If-Match is exactly one strong ETag: a quoted timestamp.
    [InlineData("\"2026-08-16T09:00:00.0000000-05:00\"", true)]
    [InlineData("2026-08-16T09:00:00.0000000-05:00", false)]      // unquoted
    [InlineData("W/\"2026-08-16T09:00:00.0000000-05:00\"", false)] // weak
    [InlineData("*", false)]
    [InlineData("\"a\", \"b\"", false)]                            // multiple
    [InlineData("\"not-a-time\"", false)]
    public void IfMatchMustBeOneQuotedTimestamp(string header, bool parses) =>
        Assert.Equal(parses, ContractServer.ParseIfMatch(header) is not null);
}

public sealed class WindowsAnalysisIntegrityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-17T12:00:00-05:00");
    private static ConnectionSnapshot Current() =>
        SnapshotComparerTests.Snapshot() with { CapturedAt = Now, Power = new PowerState(true, 100, 0) };
    private static CollectorHeartbeat Beat(DateTimeOffset started, DateTimeOffset lastSample) =>
        new(1234, started, lastSample, "events.jsonl");
    private static readonly RecorderEntry[] OneChange =
        [new RecorderEntry(DateTimeOffset.Parse("2026-08-17T09:00:00-05:00"), RecorderEntryKinds.DeviceAppeared, null, new PowerState(true, 100, 0), null)];

    [Fact]
    public void ARecordedOutageInsideTheWindowMakesItIncomplete()
    {
        // The heartbeat looks healthy now — the collector recovered — but the
        // gap it recorded is durable, so the window cannot claim completeness.
        var inputs = new WindowsAnalysis.Inputs(OneChange, Beat(Now.AddHours(-24), Now.AddSeconds(-2)), null, new MemoryBaselineStore(),
            0, [new CollectorGap(Now.AddHours(-2), Now.AddHours(-1), "collector-not-running")]);
        var result = WindowsAnalysis.Run(inputs, Current(), 6, Now)!;

        Assert.False(result.Complete);
        Assert.Contains("gap", result.Reasons);
    }

    [Fact]
    public void AnOutageOutsideTheWindowDoesNotTaintIt()
    {
        var inputs = new WindowsAnalysis.Inputs(OneChange, Beat(Now.AddHours(-24), Now.AddSeconds(-2)), null, new MemoryBaselineStore(),
            0, [new CollectorGap(Now.AddHours(-20), Now.AddHours(-19), "collector-not-running")]);
        Assert.True(WindowsAnalysis.Run(inputs, Current(), 6, Now)!.Complete);
    }

    [Fact]
    public void CorruptEventLinesForceIncompleteCoverage()
    {
        var inputs = new WindowsAnalysis.Inputs(OneChange, Beat(Now.AddHours(-24), Now.AddSeconds(-2)), null, new MemoryBaselineStore(), 4);
        var result = WindowsAnalysis.Run(inputs, Current(), 6, Now)!;

        Assert.False(result.Complete);
        Assert.Contains("corrupt-lines", result.Reasons);
    }

    [Fact]
    public void ContinuousRecorderVouchesForTheWholeWindowEvenWhenNothingChanged()
    {
        // One change three hours ago; the recorder has been up for a day. The
        // window is complete and availableFrom is the window, not the change.
        var inputs = new WindowsAnalysis.Inputs(OneChange, Beat(Now.AddDays(-1), Now.AddSeconds(-2)), null, new MemoryBaselineStore());
        var result = WindowsAnalysis.Run(inputs, Current(), 6, Now)!;

        Assert.True(result.Complete);
        Assert.Equal(Now.AddHours(-6), result.AvailableFrom);
    }

    [Fact]
    public void ADeficitThatDeepensWithoutOtherTransitionsStillQualifies()
    {
        // -3 W start (below the sustained threshold) deepening to -20 W, with no
        // other transition until it ends: the deepening entries carry the evidence.
        var start = Now.AddMinutes(-10);
        var entries = new[]
        {
            new RecorderEntry(start, RecorderEntryKinds.DeficitStarted, null, new PowerState(true, 99, -3000), null),
            new RecorderEntry(start.AddSeconds(20), RecorderEntryKinds.DeficitDeepened, null, new PowerState(true, 98, -12000), null),
            new RecorderEntry(start.AddSeconds(40), RecorderEntryKinds.DeficitDeepened, null, new PowerState(true, 97, -20000), null),
            new RecorderEntry(start.AddMinutes(2), RecorderEntryKinds.DeficitEnded, null, new PowerState(true, 96, -500), null)
        };
        var finding = Assert.Single(WindowsAnalysis.SustainedDeficit(entries, Now));
        Assert.Contains(finding.Evidence, line => line.Contains("20.0 W"));
    }

    [Fact]
    public void ADeficitSlidingDownInSmallStepsStillLeavesEvidence()
    {
        // The blocker: -3 W to -20 W in ~0.5 W steps. Comparing adjacent
        // samples emits nothing; comparing against the deepest already
        // recorded emits a line for each further watt.
        var deviceless = SnapshotComparerTests.Snapshot();
        ConnectionSnapshot WithPower(int rate) => deviceless with { CapturedAt = Now, Power = new PowerState(true, 90, rate) };
        var tracker = new DeficitTracker();
        var previous = WithPower(-3000);
        tracker.ShouldRecord(previous.Power); // the deficitStarted entry's rate

        var deepened = 0;
        var deepest = -3000;
        for (var rate = -3500; rate >= -20000; rate -= 500)
        {
            var current = WithPower(rate);
            if (Recorder.DetectChanges(previous, current, tracker).Any(entry => entry.Kind == RecorderEntryKinds.DeficitDeepened))
            {
                deepened++;
                deepest = rate;
            }

            previous = current;
        }

        Assert.True(deepened >= 15, $"expected the slide to be recorded repeatedly, got {deepened} entries");
        Assert.Equal(-20000, deepest);
    }

    [Fact]
    public void JitterAndRecoveryDoNotEmitDeepeningEntries()
    {
        var deviceless = SnapshotComparerTests.Snapshot();
        ConnectionSnapshot WithPower(int rate) => deviceless with { CapturedAt = Now, Power = new PowerState(true, 90, rate) };
        var tracker = new DeficitTracker();
        tracker.ShouldRecord(WithPower(-5000).Power);

        Assert.DoesNotContain(Recorder.DetectChanges(WithPower(-5000), WithPower(-5200), tracker),
            entry => entry.Kind == RecorderEntryKinds.DeficitDeepened);
        Assert.DoesNotContain(Recorder.DetectChanges(WithPower(-5200), WithPower(-4000), tracker),
            entry => entry.Kind == RecorderEntryKinds.DeficitDeepened);
        // …and after recovering, a new dive past the old extreme still counts.
        Assert.Contains(Recorder.DetectChanges(WithPower(-4000), WithPower(-9000), tracker),
            entry => entry.Kind == RecorderEntryKinds.DeficitDeepened);
    }
}

/// <summary>A baseline and its history living in the test, not on the machine.</summary>
internal sealed class MemoryBaselineStore : IBaselineStore
{
    private BaselineStateFile? state;
    private readonly ConnectionSnapshot? baseline;
    private readonly bool unreadable;
    /// <summary>Runs before the locked body — used to simulate a replacement landing mid-analysis.</summary>
    public Action? OnLock { get; set; }

    public MemoryBaselineStore(ConnectionSnapshot? baseline = null, BaselineStateFile? initial = null, bool unreadable = false)
    {
        this.baseline = baseline;
        state = initial;
        this.unreadable = unreadable;
    }

    /// <summary>Simulate a replacement holding the lock, an unreadable history, or a failing write.</summary>
    public bool LockBusy { get; set; }
    public bool HistoryUnreadable { get; set; }
    public bool HistoryWritable { get; set; } = true;

    public BaselineRead ReadBaseline() => unreadable ? new BaselineRead(null, true) : new BaselineRead(baseline);
    public BaselineStateFile? ReadHistory() => HistoryUnreadable ? null : state;
    public bool WriteHistory(BaselineStateFile value)
    {
        if (!HistoryWritable)
        {
            return false;
        }

        state = value;
        return true;
    }

    public bool WithLock(Action work)
    {
        if (LockBusy)
        {
            return false;
        }

        OnLock?.Invoke();
        work();
        return true;
    }
}

public sealed class BaselineFaultEvidenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-17T12:00:00-05:00");

    /// <summary>A dock branch present in the baseline and gone now.</summary>
    private static (ConnectionSnapshot Baseline, ConnectionSnapshot Current) DockMissing()
    {
        var dock = SnapshotComparerTests.Device(@"USB4\VID_045E&PID_0963\DOCK", "USB", "Surface Thunderbolt(TM) 4 Dock");
        var hub = SnapshotComparerTests.Device(@"USB\VID_043E&PID_9C04\HUB", "USB", "Generic USB Hub", dock.InstanceId);
        var mouse = SnapshotComparerTests.Device(@"USB\VID_046D&PID_C08A\MOUSE", "Mouse", "MX Vertical", hub.InstanceId);
        return (SnapshotComparerTests.Snapshot(dock, hub, mouse) with { CapturedAt = Now.AddDays(-1) },
                SnapshotComparerTests.Snapshot(dock) with { CapturedAt = Now });
    }

    [Fact]
    public void AStaleRecorderStillReportsTheLiveBaselineFaultWithEvidence()
    {
        // The real Surface case: the recorder stopped days ago, so history is
        // unknown — but the baseline mismatch in front of the user must still
        // be explained, not swallowed by the coverage early return.
        var (baseline, current) = DockMissing();
        var stale = new WindowsAnalysis.Inputs(
            [new RecorderEntry(Now.AddDays(-2), RecorderEntryKinds.DeviceDisappeared, null, new PowerState(true, 100, 0), null)],
            new CollectorHeartbeat(1, Now.AddDays(-2).AddHours(-1), Now.AddDays(-2), "events.jsonl"),
            null, new MemoryBaselineStore(baseline));

        var result = WindowsAnalysis.Run(stale, current, 6, Now)!;

        Assert.Equal(["recorder-stopped-before-window"], result.Reasons);
        Assert.False(result.Complete);
        Assert.Equal("active-fault", Assert.IsType<ContractBaselineState>(result.Baseline).State);
        var finding = Assert.Single(result.Findings);            // the fault is explained
        Assert.NotEmpty(finding.Evidence);
        Assert.NotEmpty(finding.Recommendation);
    }

    [Fact]
    public void EveryActiveFaultCarriesAFinding()
    {
        var (baseline, current) = DockMissing();
        var inputs = new WindowsAnalysis.Inputs([], new CollectorHeartbeat(1, Now.AddHours(-24), Now.AddSeconds(-2), "events.jsonl"),
            null, new MemoryBaselineStore(baseline));

        var result = WindowsAnalysis.Run(inputs, current, 6, Now)!;
        Assert.Equal("active-fault", Assert.IsType<ContractBaselineState>(result.Baseline).State);
        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, finding =>
        {
            Assert.NotEmpty(finding.Evidence);
            Assert.NotEmpty(finding.Recommendation);
        });
    }

    [Fact]
    public void AMatchingBaselineIsHealthyAndSaysNothing()
    {
        var (baseline, _) = DockMissing();
        var inputs = new WindowsAnalysis.Inputs([], new CollectorHeartbeat(1, Now.AddHours(-24), Now.AddSeconds(-2), "events.jsonl"),
            null, new MemoryBaselineStore(baseline));

        var result = WindowsAnalysis.Run(inputs, baseline with { CapturedAt = Now }, 6, Now)!;
        Assert.Equal("healthy", Assert.IsType<ContractBaselineState>(result.Baseline).State);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void AFreshInstallWithALiveFaultStillGetsAnalysisAndAFinding()
    {
        // Nothing recorded at all, but the power state in front of the user is
        // a deficit: "no history" must not swallow the present.
        var current = SnapshotComparerTests.Snapshot() with { CapturedAt = Now, Power = new PowerState(true, 88, -9000) };
        var result = WindowsAnalysis.Run(new WindowsAnalysis.Inputs([], null, null, new MemoryBaselineStore()), current, 6, Now)!;

        Assert.Equal(["no-history"], result.Reasons);
        Assert.False(result.Complete);
        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, finding => Assert.NotEmpty(finding.Evidence));
    }

    [Fact]
    public void AFreshInstallWithNothingWrongStillSaysNothing()
    {
        var quiet = SnapshotComparerTests.Snapshot() with { CapturedAt = Now, Power = new PowerState(true, 100, 0) };
        Assert.Null(WindowsAnalysis.Run(new WindowsAnalysis.Inputs([], null, null, new MemoryBaselineStore()), quiet, 6, Now));
    }

    [Fact]
    public void AMissingHeartbeatCannotProduceCompleteCoverage()
    {
        var entries = new[]
        {
            new RecorderEntry(Now.AddHours(-6), RecorderEntryKinds.DeviceAppeared, null, new PowerState(true, 100, 0), null),
            new RecorderEntry(Now.AddSeconds(-1), RecorderEntryKinds.DeviceAppeared, null, new PowerState(true, 100, 0), null)
        };
        var result = WindowsAnalysis.Run(new WindowsAnalysis.Inputs(entries, null, null, new MemoryBaselineStore()),
            SnapshotComparerTests.Snapshot() with { CapturedAt = Now }, 6, Now)!;

        Assert.False(result.Complete);
        Assert.Contains("no-heartbeat", result.Reasons);
    }

    [Fact]
    public void UnreadableOutageEvidenceMakesCoverageIncomplete()
    {
        var inputs = new WindowsAnalysis.Inputs(
            [new RecorderEntry(Now.AddHours(-1), RecorderEntryKinds.DeviceAppeared, null, new PowerState(true, 100, 0), null)],
            new CollectorHeartbeat(1, Now.AddDays(-1), Now.AddSeconds(-2), "events.jsonl"),
            null, new MemoryBaselineStore(), 0, [], GapEvidenceUnreadable: true);
        var result = WindowsAnalysis.Run(inputs, SnapshotComparerTests.Snapshot() with { CapturedAt = Now }, 6, Now)!;

        Assert.False(result.Complete);
        Assert.Contains("gap-evidence-unreadable", result.Reasons);
    }
}

public sealed class BaselineHistoryRaceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-17T12:00:00-05:00");

    [Fact]
    public void HistoryIsReadAndWrittenUnderTheLockSoAReplacementCannotBeUndone()
    {
        // A replacement resets the history while an analysis is in flight. The
        // analysis re-reads inside the lock, so it updates the *new* state
        // rather than writing the old fault back over it.
        var dock = SnapshotComparerTests.Device(@"USB4\VID_045E&PID_0963\DOCK", "USB", "Surface Thunderbolt(TM) 4 Dock");
        var hub = SnapshotComparerTests.Device(@"USB\VID_043E&PID_9C04\HUB", "USB", "Generic USB Hub", dock.InstanceId);
        var baseline = SnapshotComparerTests.Snapshot(dock, hub) with { CapturedAt = Now.AddDays(-1) };
        var current = SnapshotComparerTests.Snapshot(dock) with { CapturedAt = Now };

        // A replacement lands *while* this analysis is running: it resets the
        // history just before we take the lock. Because the baseline and the
        // history are both read inside the lock, the verdict is derived from
        // the new state, not from anything read earlier.
        var store = new MemoryBaselineStore(baseline, new BaselineStateFile(FaultSince: Now.AddHours(-5)));
        store.OnLock = () => store.WriteHistory(new BaselineStateFile());
        var inputs = new WindowsAnalysis.Inputs([], new CollectorHeartbeat(1, Now.AddDays(-1), Now.AddSeconds(-2), "events.jsonl"),
            null, store);

        var result = WindowsAnalysis.Run(inputs, current, 6, Now)!;

        Assert.Equal("active-fault", Assert.IsType<ContractBaselineState>(result.Baseline).State);
        // The fault is dated from the reset state, not the stale five-hour-old one.
        Assert.Equal(Now, result.Baseline!.FaultSince);
        Assert.Equal(Now, store.ReadHistory()!.FaultSince);
    }
}

public sealed class EvidenceBoundaryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-17T12:00:00-05:00");
    private static ConnectionSnapshot Quiet() =>
        SnapshotComparerTests.Snapshot() with { CapturedAt = Now, Power = new PowerState(true, 100, 0) };

    [Fact]
    public void AnUnreadableBaselineIsUnknownNotHealthyAndNeverThrows()
    {
        var inputs = new WindowsAnalysis.Inputs([], null, null, new MemoryBaselineStore(unreadable: true));
        var result = WindowsAnalysis.Run(inputs, Quiet(), 6, Now)!;

        Assert.Equal("unreadable", result.BaselineAvailability);
        // No state at all: unreadable is unknown. "no-baseline" would read as
        // "nothing to compare against", which is a different claim.
        Assert.Null(result.Baseline);
    }

    [Fact]
    public void AnUnreadableHeartbeatIsAReason()
    {
        var inputs = new WindowsAnalysis.Inputs(
            [new RecorderEntry(Now.AddHours(-1), RecorderEntryKinds.DeviceAppeared, null, new PowerState(true, 100, 0), null)],
            null, null, new MemoryBaselineStore(), HeartbeatUnreadable: true);
        var result = WindowsAnalysis.Run(inputs, Quiet(), 6, Now)!;

        Assert.Contains("heartbeat-unreadable", result.Reasons);
        Assert.False(result.Complete);
    }

    [Fact]
    public void EntirelyCorruptEvidenceIsIncompleteNotNoHistory()
    {
        // Every line unreadable: entries empty and no heartbeat, which used to
        // take the "never recorded" shortcut. Corrupt evidence is missing
        // evidence, not an empty machine.
        var inputs = new WindowsAnalysis.Inputs([], null, null, new MemoryBaselineStore(), SkippedLines: 40);
        var result = WindowsAnalysis.Run(inputs, Quiet(), 6, Now)!;

        Assert.Contains("corrupt-lines", result.Reasons);
        Assert.DoesNotContain("no-history", result.Reasons);
        Assert.False(result.Complete);
    }

    [Fact]
    public void ARecordedOutageWithoutEntriesIsStillAGapNotNoHistory()
    {
        var inputs = new WindowsAnalysis.Inputs([], null, null, new MemoryBaselineStore(), 0,
            [new CollectorGap(Now.AddHours(-2), Now.AddHours(-1), "collector-not-running")]);
        var result = WindowsAnalysis.Run(inputs, Quiet(), 6, Now)!;

        Assert.Contains("gap", result.Reasons);
        Assert.DoesNotContain("no-history", result.Reasons);
    }

    [Fact]
    public void AGenuinelyEmptyQuietMachineStillSaysNothing()
    {
        Assert.Null(WindowsAnalysis.Run(new WindowsAnalysis.Inputs([], null, null, new MemoryBaselineStore()), Quiet(), 6, Now));
    }

    [Fact]
    public void ASecondShallowerDeficitEpisodeIsStillRecorded()
    {
        // First episode reaches -20 W; the second only -8 W. Without resetting
        // at the episode boundary the second would have to beat -20 W before
        // anything was recorded, and its evidence would be lost.
        var deviceless = SnapshotComparerTests.Snapshot();
        ConnectionSnapshot WithPower(int rate) => deviceless with { CapturedAt = Now, Power = new PowerState(true, 90, rate) };
        var tracker = new DeficitTracker();

        Recorder.DetectChanges(WithPower(0), WithPower(-3000), tracker);        // deficit starts
        Recorder.DetectChanges(WithPower(-3000), WithPower(-20000), tracker);   // deepens
        Recorder.DetectChanges(WithPower(-20000), WithPower(0), tracker);       // ends

        Recorder.DetectChanges(WithPower(0), WithPower(-3000), tracker);        // second episode starts
        var deepened = Recorder.DetectChanges(WithPower(-3000), WithPower(-8000), tracker);

        Assert.Contains(deepened, entry => entry.Kind == RecorderEntryKinds.DeficitDeepened);
    }
}

public sealed class BaselineUnavailabilityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-17T12:00:00-05:00");
    private static (ConnectionSnapshot Baseline, ConnectionSnapshot Current) DockMissing()
    {
        var dock = SnapshotComparerTests.Device(@"USB4\VID_045E&PID_0963\DOCK", "USB", "Surface Thunderbolt(TM) 4 Dock");
        var hub = SnapshotComparerTests.Device(@"USB\VID_043E&PID_9C04\HUB", "USB", "Generic USB Hub", dock.InstanceId);
        return (SnapshotComparerTests.Snapshot(dock, hub) with { CapturedAt = Now.AddDays(-1) },
                SnapshotComparerTests.Snapshot(dock) with { CapturedAt = Now });
    }
    private static WindowsAnalysis.Inputs With(MemoryBaselineStore store) =>
        new([], new CollectorHeartbeat(1, Now.AddDays(-1), Now.AddSeconds(-2), "events.jsonl"), null, store);

    [Fact]
    public void ABusyLockPublishesNoBaselineStateAtAll()
    {
        // A replacement is in progress: we cannot read the pair consistently,
        // so we say nothing rather than defaulting to "no baseline".
        var (baseline, current) = DockMissing();
        var result = WindowsAnalysis.Run(With(new MemoryBaselineStore(baseline) { LockBusy = true }), current, 6, Now)!;

        Assert.Null(result.Baseline);
        Assert.Equal("busy", result.BaselineAvailability);
        Assert.DoesNotContain("busy", result.Reasons);   // coverage stays temporal
    }

    [Fact]
    public void AnUnwritableHistoryDoesNotPublishATransitionButKeepsTheFindings()
    {
        var (baseline, current) = DockMissing();
        var store = new MemoryBaselineStore(baseline) { HistoryWritable = false };
        var result = WindowsAnalysis.Run(With(store), current, 6, Now)!;

        Assert.Null(result.Baseline);                                   // no fault time we cannot back up
        Assert.Equal("history-unwritable", result.BaselineAvailability);
        Assert.NotEmpty(result.Findings);                               // the comparison still stands
    }

    [Fact]
    public void AnUnreadableHistoryIsUnavailableNotHealthy()
    {
        var (baseline, current) = DockMissing();
        var store = new MemoryBaselineStore(baseline) { HistoryUnreadable = true };
        var result = WindowsAnalysis.Run(With(store), current, 6, Now)!;

        Assert.Null(result.Baseline);
        Assert.Equal("history-unreadable", result.BaselineAvailability);
    }

    [Fact]
    public void APowerFindingDoesNotHideAMissingDock()
    {
        // A deficit and a vanished dock at once: the power finding must not
        // suppress the missing-device evidence the baseline state rests on.
        var (baseline, current) = DockMissing();
        var deficit = current with { Power = new PowerState(true, 80, -9000) };
        var result = WindowsAnalysis.Run(With(new MemoryBaselineStore(baseline)), deficit, 6, Now)!;

        Assert.Equal("active-fault", result.Baseline!.State);
        Assert.Contains(result.Findings, finding => finding.Title.Contains("missing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Findings, finding => finding.Title.Contains("battery", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class BaselineHistoryStampTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-17T12:00:00-05:00");

    [Fact]
    public void HistoryLeftOverFromAnotherBaselineIsDiscardedNotReported()
    {
        // Crash between the two writes: the new baseline is on disk with the
        // previous baseline's fault history beside it. The stamp makes that
        // pair recognisable, so the stale fault is not reported as this
        // baseline's.
        var dock = SnapshotComparerTests.Device(@"USB4\VID_045E&PID_0963\DOCK", "USB", "Surface Thunderbolt(TM) 4 Dock");
        var baseline = SnapshotComparerTests.Snapshot(dock) with { CapturedAt = Now.AddMinutes(-5) };
        var orphaned = new BaselineStateFile(FaultSince: Now.AddDays(-3), BaselineCapturedAt: Now.AddDays(-7));
        var store = new MemoryBaselineStore(baseline, orphaned);
        var inputs = new WindowsAnalysis.Inputs([], new CollectorHeartbeat(1, Now.AddDays(-1), Now.AddSeconds(-2), "events.jsonl"), null, store);

        // Current state matches the baseline, so: healthy, and no three-day-old fault.
        var result = WindowsAnalysis.Run(inputs, baseline with { CapturedAt = Now }, 6, Now)!;

        Assert.Equal("healthy", Assert.IsType<ContractBaselineState>(result.Baseline).State);
        Assert.Null(result.Baseline!.FaultSince);
        Assert.Equal(baseline.CapturedAt, store.ReadHistory()!.BaselineCapturedAt);
    }

    [Fact]
    public void HistoryForTheSameBaselineIsKept()
    {
        var dock = SnapshotComparerTests.Device(@"USB4\VID_045E&PID_0963\DOCK", "USB", "Surface Thunderbolt(TM) 4 Dock");
        var hub = SnapshotComparerTests.Device(@"USB\VID_043E&PID_9C04\HUB", "USB", "Generic USB Hub", dock.InstanceId);
        var baseline = SnapshotComparerTests.Snapshot(dock, hub) with { CapturedAt = Now.AddDays(-1) };
        var current = SnapshotComparerTests.Snapshot(dock) with { CapturedAt = Now };
        var faultedFiveHoursAgo = new BaselineStateFile(FaultSince: Now.AddHours(-5), BaselineCapturedAt: baseline.CapturedAt);
        var store = new MemoryBaselineStore(baseline, faultedFiveHoursAgo);
        var inputs = new WindowsAnalysis.Inputs([], new CollectorHeartbeat(1, Now.AddDays(-1), Now.AddSeconds(-2), "events.jsonl"), null, store);

        var result = WindowsAnalysis.Run(inputs, current, 6, Now)!;

        Assert.Equal("active-fault", Assert.IsType<ContractBaselineState>(result.Baseline).State);
        Assert.Equal(Now.AddHours(-5), result.Baseline!.FaultSince);   // the fault kept its start time
    }

    [Fact]
    public void AnUnknownBaselineDoesNotMakeACompleteHistoryIncomplete()
    {
        // The recorder covered the whole window; only the baseline is unknown.
        // Those are different questions and the answers must not contaminate.
        var entries = new[] { new RecorderEntry(Now.AddHours(-3), RecorderEntryKinds.DeviceAppeared, null, new PowerState(true, 100, 0), null) };
        var store = new MemoryBaselineStore { LockBusy = true };
        var inputs = new WindowsAnalysis.Inputs(entries, new CollectorHeartbeat(1, Now.AddDays(-1), Now.AddSeconds(-2), "events.jsonl"), null, store);

        var result = WindowsAnalysis.Run(inputs, SnapshotComparerTests.Snapshot() with { CapturedAt = Now }, 6, Now)!;

        Assert.True(result.Complete);                       // history is complete…
        Assert.Empty(result.Reasons);
        Assert.Null(result.Baseline);                       // …and the baseline is unknown
        Assert.Equal("busy", result.BaselineAvailability);
    }
}
