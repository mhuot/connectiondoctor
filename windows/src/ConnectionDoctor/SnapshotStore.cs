using System.Text.Json;

namespace ConnectionDoctor;

internal static class SnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Beside the recorded history, so CONNECTIONDOCTOR_DIR moves both together.</summary>
    public static string DefaultBaselinePath => Path.Combine(BackgroundCollector.DataDirectory, "baseline.json");

    public static void Save(ConnectionSnapshot snapshot, string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(snapshot, Options));
    }

    /// <summary>
    /// Write via a temp file and replace, so a reader never sees half a
    /// baseline and a crash mid-write cannot destroy the old one.
    /// </summary>
    public static void SaveAtomic(ConnectionSnapshot snapshot, string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Options));
        if (File.Exists(fullPath))
        {
            File.Replace(temporary, fullPath, null);
        }
        else
        {
            File.Move(temporary, fullPath);
        }
    }

    public static ConnectionSnapshot Load(string path)
    {
        var snapshot = JsonSerializer.Deserialize<ConnectionSnapshot>(File.ReadAllText(path), Options);
        return snapshot ?? throw new InvalidDataException($"Snapshot is invalid: {path}");
    }
}
