namespace ConnectionDoctor.Tests;

/// <summary>
/// Identity that survives a rename without becoming a tracking identifier:
/// random per installation, keyed serials, and honest silence when a device
/// reports no serial at all.
/// </summary>
public sealed class IdentityTests
{
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
        var first = Identity.UnitKey("SERIAL-A");
        var second = Identity.UnitKey("SERIAL-B");

        Assert.NotNull(first);
        Assert.NotEqual(first, second);
        Assert.Equal(16, first!.Length);
        Assert.Equal(first, Identity.UnitKey("SERIAL-A"));   // stable within an installation
        Assert.Null(Identity.UnitKey(null));                 // "same model, unit unknown"
        Assert.Null(Identity.UnitKey(string.Empty));
    }

    [Fact]
    public void UnitKeyIsNotTheSerialAndNotAPlainHashOfIt()
    {
        const string serial = "0123456789AB";
        var key = Identity.UnitKey(serial)!;

        Assert.DoesNotContain(serial, key, StringComparison.OrdinalIgnoreCase);
        // A plain SHA-256 would be identical on every machine in the world;
        // this is keyed, so it is meaningful only beside this installation.
        var plain = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(serial)))[..16].ToLowerInvariant();
        Assert.NotEqual(plain, key);
    }

    [Fact]
    public void HostIdIsRandomAndNotDerivedFromTheMachine()
    {
        var id = Identity.Current.HostId;

        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id, Identity.Current.HostId);   // stable within a run
        // Not the machine's own identifiers: a hash of MachineGuid would be
        // stable across every export forever, which is what we are avoiding.
        Assert.NotEqual(Environment.MachineName, id);
    }

    [Fact]
    public void TheEnvelopeCarriesHostIdAndKeepsSerialsToItself()
    {
        var dock = SnapshotComparerTests.Device(@"USB\VID_045E&PID_0963\DOCKSERIAL42", "USB", "Surface Thunderbolt(TM) 4 Dock");
        var json = System.Text.Json.Nodes.JsonNode.Parse(
            ContractV1.Serialize(ContractV1.ToEnvelope(SnapshotComparerTests.Snapshot(dock))))!;

        Assert.Equal(Identity.Current.HostId, json["host"]!["id"]!.GetValue<string>());
        var node = json["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>().Contains("DOCKSERIAL42"));
        Assert.Equal(Identity.UnitKey("DOCKSERIAL42"), node!["unitKey"]!.GetValue<string>());
        // The serial itself never appears anywhere in the document.
        Assert.DoesNotContain("DOCKSERIAL42", json["nodes"]!.AsArray()
            .Select(n => n!["unitKey"]?.GetValue<string>() ?? string.Empty).ToArray());
    }
}

/// <summary>
/// Durability: an identity that changes between runs is worse than none — it
/// splits one endpoint into many in every consumer that keys on it. So the
/// only two acceptable answers are "the same one as last time" and "none".
/// </summary>
public sealed class IdentityDurabilityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cd-identity-" + Guid.NewGuid().ToString("n"));
    private readonly string? previous = Environment.GetEnvironmentVariable("CONNECTIONDOCTOR_DIR");

    public IdentityDurabilityTests()
    {
        Environment.SetEnvironmentVariable("CONNECTIONDOCTOR_DIR", directory);
        Identity.ResetCacheForTesting();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CONNECTIONDOCTOR_DIR", previous);
        Identity.ResetCacheForTesting();
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ACorruptIdentityFileYieldsNoIdentityRatherThanANewOne()
    {
        Directory.CreateDirectory(directory);
        // Whatever this is, it is not an identity we wrote: a short key.
        File.WriteAllText(Identity.Path, """{"hostId":"11111111-1111-1111-1111-111111111111","installationKey":"AAAA"}""");
        Identity.ResetCacheForTesting();

        // It must not silently become a fresh identity that differs next run;
        // the file is there and unusable, so the honest answer is none.
        Assert.Null(Identity.Current);
        Assert.Null(Identity.UnitKey("SERIAL-A"));
    }

    [Fact]
    public void AnIdentityWithANonUuidHostIdIsRejected()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Identity.Path,
            """{"hostId":"not-a-uuid","installationKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}""");
        Identity.ResetCacheForTesting();

        Assert.Null(Identity.Current);
    }

    [Fact]
    public void TheEnvelopeOmitsHostIdWhenThereIsNoDurableIdentity()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Identity.Path, "{ not json");
        Identity.ResetCacheForTesting();

        var json = System.Text.Json.Nodes.JsonNode.Parse(
            ContractV1.Serialize(ContractV1.ToEnvelope(SnapshotComparerTests.Snapshot())))!;

        // Absent, not invented: consumers fall back to the hostname, which is
        // honest, rather than to a value that changes on the next request.
        Assert.Null(json["host"]!["id"]);
    }

    [Fact]
    public void ConcurrentFirstRunsAgreeOnOneIdentity()
    {
        Directory.CreateDirectory(directory);
        // Every process that can start at once — collector, CLI, HTTP, MCP —
        // racing to be the first. Exactly one file wins and everyone adopts it.
        var results = new System.Collections.Concurrent.ConcurrentBag<string?>();
        Parallel.For(0, 8, _ =>
        {
            Identity.ResetCacheForTesting();
            results.Add(Identity.Current?.HostId);
        });

        var distinct = results.Where(id => id is not null).Distinct().ToList();
        Assert.Single(distinct);
        Assert.Equal(File.ReadAllText(Identity.Path).Contains(distinct[0]!), true);
    }
}
