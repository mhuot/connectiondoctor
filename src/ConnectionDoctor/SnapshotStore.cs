using System.Text.Json;

namespace ConnectionDoctor;

internal static class SnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DefaultBaselinePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConnectionDoctor",
        "baseline.json");

    public static void Save(ConnectionSnapshot snapshot, string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(snapshot, Options));
    }

    public static ConnectionSnapshot Load(string path)
    {
        var snapshot = JsonSerializer.Deserialize<ConnectionSnapshot>(File.ReadAllText(path), Options);
        return snapshot ?? throw new InvalidDataException($"Snapshot is invalid: {path}");
    }
}
