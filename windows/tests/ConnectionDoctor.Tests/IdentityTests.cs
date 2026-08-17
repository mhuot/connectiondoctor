using System.Security.Cryptography;

namespace ConnectionDoctor.Tests;

/// <summary>
/// Identity that survives a rename without becoming a tracking identifier:
/// random per installation, keyed serials, and honest silence when a device
/// reports no serial at all.
///
/// Every test here builds its own identity or its own directory. Nothing sets
/// <c>CONNECTIONDOCTOR_DIR</c> and nothing clears a static cache: those are
/// process-global, and xUnit runs test classes in parallel, so a fixture that
/// redirected the data directory pulled it out from under the baseline and
/// recorder tests running beside it. Injecting the identity is the same cure
/// <c>IBaselineStore</c> applied to the baseline: it removes the coupling
/// rather than scheduling around it.
/// </summary>
public sealed class IdentityTests
{
    private static Identity Fresh() => new(Guid.NewGuid().ToString("d"), RandomNumberGenerator.GetBytes(32));

    [Theory]
    // A serial the device actually reported: the last instance-id segment.
    [InlineData(@"USB\VID_045E&PID_0963\0123456789AB", "0123456789AB")]
    [InlineData(@"USB\VID_046D&PID_C08A\MX-VERT-0001", "MX-VERT-0001")]
    // Bus-generated paths describe where it is plugged in, not which unit:
    [InlineData(@"USB\VID_8087&PID_0024\6&1a2b3c4d&0&2", null)]
    [InlineData(@"USB\ROOT_HUB30\4&2b8c1f2&0", null)]
    // Not enough segments to carry one.
    [InlineData(@"HID\VID_046D&PID_C08A", null)]
    public void SerialIsReadOnlyWhenTheDeviceActuallyReportedOne(string instanceId, string? expected) =>
        Assert.Equal(expected, SnapshotComparerTests.Device(instanceId, "USB", "Device").Serial);

    [Fact]
    public void UnitKeyDistinguishesTwoUnitsOfTheSameModelAndIsAbsentWithoutASerial()
    {
        var identity = Fresh();
        var first = identity.UnitKey("SERIAL-A");

        Assert.NotNull(first);
        Assert.NotEqual(first, identity.UnitKey("SERIAL-B"));
        Assert.Equal(16, first!.Length);
        Assert.Matches("^[0-9a-f]{16}$", first);
        Assert.Equal(first, identity.UnitKey("SERIAL-A"));   // stable within an installation
        Assert.Null(identity.UnitKey(null));                 // "same model, unit unknown"
        Assert.Null(identity.UnitKey(string.Empty));
        // Keyed per installation: the same serial on another machine is a
        // different key, which is what stops it correlating across exports.
        Assert.NotEqual(first, Fresh().UnitKey("SERIAL-A"));
    }

    [Fact]
    public void UnitKeyIsNotTheSerialAndNotAPlainHashOfIt()
    {
        const string serial = "0123456789AB";
        var key = Fresh().UnitKey(serial)!;

        Assert.DoesNotContain(serial, key, StringComparison.OrdinalIgnoreCase);
        // A plain SHA-256 would be identical on every machine in the world;
        // this is keyed, so it is meaningful only beside this installation.
        var plain = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(serial)))[..16].ToLowerInvariant();
        Assert.NotEqual(plain, key);
    }

    [Fact]
    public void HostIdIsRandomAndNotDerivedFromTheMachine()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity.Resolve(directory.Path)!;

        Assert.True(Guid.TryParse(identity.HostId, out _));
        // Resolving again reads the same file rather than minting a new id.
        Assert.Equal(identity.HostId, Identity.Resolve(directory.Path)!.HostId);
        // Not the machine's own identifiers: a hash of MachineGuid would be
        // stable across every export forever, which is what we are avoiding.
        Assert.NotEqual(Environment.MachineName, identity.HostId);
        // Two installations are two identities, however alike the machines.
        using var other = new TemporaryDirectory();
        Assert.NotEqual(identity.HostId, Identity.Resolve(other.Path)!.HostId);
    }

    [Fact]
    public void TheEnvelopeCarriesHostIdAndKeepsSerialsToItself()
    {
        var identity = Fresh();
        var dock = SnapshotComparerTests.Device(@"USB\VID_045E&PID_0963\DOCKSERIAL42", "USB", "Surface Thunderbolt(TM) 4 Dock");
        var json = System.Text.Json.Nodes.JsonNode.Parse(ContractV1.Serialize(
            ContractV1.ToEnvelope(SnapshotComparerTests.Snapshot(dock), identity: new ResolvedIdentity(identity))))!;

        Assert.Equal(identity.HostId, json["host"]!["id"]!.GetValue<string>());
        var node = json["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>().Contains("DOCKSERIAL42"));
        Assert.Equal(identity.UnitKey("DOCKSERIAL42"), node!["unitKey"]!.GetValue<string>());
        // The serial itself never appears anywhere in the document.
        Assert.DoesNotContain("DOCKSERIAL42", ContractV1.Serialize(ContractV1.ToEnvelope(
            SnapshotComparerTests.Snapshot(dock), identity: new ResolvedIdentity(identity)) with { Nodes = [] }));
    }

    [Fact]
    public void OneDocumentCarriesOneIdentityOrNone()
    {
        var dock = SnapshotComparerTests.Device(@"USB\VID_045E&PID_0963\DOCKSERIAL42", "USB", "Dock");
        var mouse = SnapshotComparerTests.Device(@"USB\VID_046D&PID_C08A\MX-VERT-0001", "USB", "Mouse");
        var snapshot = SnapshotComparerTests.Snapshot(dock, mouse);

        // Identity is resolved once at the top of the document, so every keyed
        // node agrees with the host id beside it. Resolving per device would
        // let one envelope be half-keyed if identity appeared mid-serialization
        // — and would cost a file check per device on a machine that has none.
        var identity = Fresh();
        var keyed = ContractV1.ToEnvelope(snapshot, identity: new ResolvedIdentity(identity));
        Assert.Equal(identity.HostId, keyed.Host.Id);
        foreach (var node in keyed.Nodes.Where(n => n.UnitKey is not null))
        {
            Assert.Matches("^[0-9a-f]{16}$", node.UnitKey!);
        }

        Assert.Equal(2, keyed.Nodes.Count(n => n.UnitKey is not null));

        // And with no identity it is all-or-nothing the other way: no host id,
        // and not one keyed node.
        var anonymous = ContractV1.ToEnvelope(snapshot, identity: ResolvedIdentity.None);
        Assert.Null(anonymous.Host.Id);
        Assert.All(anonymous.Nodes, node => Assert.Null(node.UnitKey));
    }
}

/// <summary>
/// Durability: an identity that changes between runs is worse than none — it
/// splits one endpoint into many in every consumer that keys on it. So the
/// only two acceptable answers are "the same one as last time" and "none".
/// </summary>
public sealed class IdentityDurabilityTests
{
    [Fact]
    public void ACorruptIdentityFileYieldsNoIdentityRatherThanANewOne()
    {
        using var directory = new TemporaryDirectory();
        // Whatever this is, it is not an identity we wrote: a short key.
        File.WriteAllText(Identity.PathIn(directory.Path),
            """{"hostId":"11111111-1111-1111-1111-111111111111","installationKey":"AAAA"}""");

        // It must not silently become a fresh identity that differs next run;
        // the file is there and unusable, so the honest answer is none.
        Assert.Null(Identity.Resolve(directory.Path));
    }

    [Fact]
    public void AnIdentityWithANonUuidHostIdIsRejected()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Identity.PathIn(directory.Path),
            """{"hostId":"not-a-uuid","installationKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}""");

        Assert.Null(Identity.Resolve(directory.Path));
    }

    [Fact]
    public void TheEnvelopeOmitsHostIdWhenThereIsNoDurableIdentity()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Identity.PathIn(directory.Path), "{ not json");

        var json = System.Text.Json.Nodes.JsonNode.Parse(ContractV1.Serialize(ContractV1.ToEnvelope(
            SnapshotComparerTests.Snapshot(),
            identity: new ResolvedIdentity(Identity.Resolve(directory.Path)))))!;

        // Absent, not invented: consumers fall back to the hostname, which is
        // honest, rather than to a value that changes on the next request.
        Assert.Null(json["host"]!["id"]);
    }

    [Fact]
    public void AProcessThatLosesTheCreationRaceAdoptsTheWinnerRatherThanItsOwn()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        Identity? winner = null;

        // The branch a thread test cannot reach: another *process* creates the
        // file between our read (which found nothing) and our move. The move
        // then fails because the path exists, and the only correct response is
        // to adopt the identity that actually got persisted — caching the one
        // we minted would give this installation two identities forever.
        var resolved = Identity.Resolve(directory.Path, beforeCreate: () =>
            winner = Identity.Resolve(directory.Path));

        Assert.NotNull(winner);
        Assert.NotNull(resolved);
        Assert.Equal(winner!.HostId, resolved!.HostId);
        Assert.Equal(winner.InstallationKey, resolved.InstallationKey);
        // And nothing is left behind from the attempt that lost.
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void ThreadsInOneProcessAgreeOnOneIdentity()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);

        // Weaker than it looks, and named for what it proves: these are threads
        // in one process, not the cross-process race (that is the test above).
        // It still earns its place — it is what catches a resolver that mints
        // an identity per caller.
        var results = new System.Collections.Concurrent.ConcurrentBag<string?>();
        Parallel.For(0, 8, _ => results.Add(Identity.Resolve(directory.Path)?.HostId));

        var distinct = results.Where(id => id is not null).Distinct().ToList();
        Assert.Single(distinct);
        Assert.Contains(distinct[0]!, File.ReadAllText(Identity.PathIn(directory.Path)));
    }
}

/// <summary>A directory of its own, so a test never has to move anyone else's.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "cd-identity-" + Guid.NewGuid().ToString("n"));

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
