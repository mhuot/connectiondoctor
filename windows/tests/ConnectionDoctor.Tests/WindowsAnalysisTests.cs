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
        new(entries, beat, trimmed, null, null);

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
        Assert.Equal("no-baseline", result.Baseline.State);
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
    // lan, origin, header, replace, ifMatch, existing → expected status (0 = allowed)
    [InlineData(true, "http://localhost:8787", "1", false, null, false, 403)]     // LAN binding
    [InlineData(false, null, "1", false, null, false, 403)]                        // no Origin (curl, or a simple cross-site POST)
    [InlineData(false, "https://evil.example", "1", false, null, false, 403)]      // foreign origin
    [InlineData(false, "http://localhost:8787", null, false, null, false, 403)]    // missing custom header
    [InlineData(false, "http://localhost:8787", "1", false, null, false, 0)]       // first capture
    [InlineData(false, "http://127.0.0.1:8787", "1", false, null, true, 409)]      // exists, no replace
    [InlineData(false, "http://localhost:8787", "1", true, "stale-time", true, 409)] // stale If-Match
    public void BaselineMutationRules(bool lan, string? origin, string? header, bool replace, string? ifMatch, bool exists, int expected)
    {
        var captured = DateTimeOffset.Parse("2026-08-16T09:00:00-05:00");
        var match = ifMatch == "stale-time" ? captured.AddMinutes(-1).ToString("O") : ifMatch;
        var decision = ContractServer.BaselineDecision(lan, origin, 8787, header, replace, match, exists ? captured : null);

        if (expected == 0)
        {
            Assert.Null(decision);
        }
        else
        {
            Assert.Equal(expected, decision!.Status);
        }
    }

    [Fact]
    public void CorrectIfMatchAllowsTheReplacement()
    {
        var captured = DateTimeOffset.Parse("2026-08-16T09:00:00-05:00");
        Assert.Null(ContractServer.BaselineDecision(false, "http://localhost:8787", 8787, "1", true, captured.ToString("O"), captured));
        // Quoted ETag form too.
        Assert.Null(ContractServer.BaselineDecision(false, "http://localhost:8787", 8787, "1", true, $"\"{captured:O}\"", captured));
    }
}
