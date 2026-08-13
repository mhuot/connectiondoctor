using System.Text.Json;

namespace ConnectionDoctor.Tests;

public sealed class DashboardDataLoaderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void IncrementalReaderParsesOnlyAppendedLines()
    {
        using var files = TestFiles.Create();
        files.WriteEvents(EntryAt(0), EntryAt(1));
        var cursor = new EventLogCursor();

        var first = BackgroundCollector.ReadEntriesIncremental(files.Events, cursor);
        var offsetAfterFirstRead = cursor.Offset;
        var second = BackgroundCollector.ReadEntriesIncremental(files.Events, cursor);
        var offsetAfterSecondRead = cursor.Offset;
        files.AppendEvent(EntryAt(2));
        var third = BackgroundCollector.ReadEntriesIncremental(files.Events, cursor);

        Assert.Equal(2, first.Entries.Count);
        Assert.Empty(second.Entries);
        Assert.Equal(offsetAfterFirstRead, offsetAfterSecondRead);
        Assert.Single(third.Entries);
        Assert.Equal(3, cursor.ParsedLineCount);
    }

    [Fact]
    public void IncrementalReaderResetsWhenBoundedLogShrinks()
    {
        using var files = TestFiles.Create();
        files.WriteEvents(EntryAt(0), EntryAt(1), EntryAt(2));
        var cursor = new EventLogCursor();
        _ = BackgroundCollector.ReadEntriesIncremental(files.Events, cursor);

        files.WriteEvents(EntryAt(60));
        var read = BackgroundCollector.ReadEntriesIncremental(files.Events, cursor);

        Assert.True(read.Reset);
        var entry = Assert.Single(read.Entries);
        Assert.Equal(EntryAt(60).At, entry.At);
    }

    [Fact]
    public void LoaderCachesEventsIncidentsAndBaselineUntilFilesChange()
    {
        using var files = TestFiles.Create();
        var current = SnapshotComparerTests.Snapshot(
            SnapshotComparerTests.Device(@"USB\VID_1234&PID_5678\1", "USB", "USB Device"));
        SnapshotStore.Save(current, files.Current);
        SnapshotStore.Save(current, files.Baseline);
        files.WriteEvents(EntryAt(0), EntryAt(1));
        var loader = new DashboardDataLoader(files.Events, files.Current, files.Baseline);

        var first = loader.Load();
        var parsedAfterFirstLoad = loader.ParsedEventLineCount;
        var second = loader.Load();

        Assert.Equal(2, parsedAfterFirstLoad);
        Assert.Equal(parsedAfterFirstLoad, loader.ParsedEventLineCount);
        Assert.Equal(1, loader.BaselineLoadCount);
        Assert.Same(first.Incidents, second.Incidents);

        files.AppendEvent(EntryAt(120));
        var third = loader.Load();
        Assert.Equal(3, loader.ParsedEventLineCount);
        Assert.Equal(2, third.Incidents.Count);

        var changedBaseline = SnapshotComparerTests.Snapshot();
        SnapshotStore.Save(changedBaseline, files.Baseline);
        File.SetLastWriteTimeUtc(files.Baseline, DateTime.UtcNow.AddSeconds(2));
        _ = loader.Load();
        Assert.Equal(2, loader.BaselineLoadCount);
    }

    private static RecorderEntry EntryAt(int seconds) =>
        new(
            DateTimeOffset.Parse("2026-08-13T16:00:00-05:00").AddSeconds(seconds),
            RecorderEntryKinds.DeviceDisappeared,
            SnapshotComparerTests.Device($@"USB\VID_1234&PID_5678\{seconds}", "USB", $"Device {seconds}"),
            new PowerState(true, 100, 0),
            null);

    private sealed class TestFiles : IDisposable
    {
        private TestFiles(string directory)
        {
            Directory = directory;
            Events = Path.Combine(directory, "events.jsonl");
            Current = Path.Combine(directory, "current.json");
            Baseline = Path.Combine(directory, "baseline.json");
        }

        public string Directory { get; }
        public string Events { get; }
        public string Current { get; }
        public string Baseline { get; }

        public static TestFiles Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"connectiondoctor-tests-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            return new TestFiles(directory);
        }

        public void WriteEvents(params RecorderEntry[] entries)
        {
            var text = string.Join(
                Environment.NewLine,
                entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions))) + Environment.NewLine;
            File.WriteAllText(Events, text);
        }

        public void AppendEvent(RecorderEntry entry)
        {
            File.AppendAllText(
                Events,
                JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
