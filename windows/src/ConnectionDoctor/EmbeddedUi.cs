using System.Reflection;

namespace ConnectionDoctor;

/// <summary>
/// The Connection Dashboard bundle compiled into the exe, so a user needs one
/// download and no Node. Staged by scripts/build-ui.ps1; absent in a plain
/// source build, in which case the server says so instead of failing.
///
/// The route rules here are the shared ones TBDoctor implements on macOS —
/// see docs/embedding.md at the repo root — so the same bundle behaves
/// identically whichever producer is serving it.
/// </summary>
internal static class EmbeddedUi
{
    private const string Prefix = "ui/";
    private const string IndexPath = "index.html";

    private static readonly IReadOnlyDictionary<string, string> Assets = Discover();

    /// <summary>False in a source build with no staged bundle.</summary>
    public static bool IsPresent => Assets.Count > 0;

    public static int AssetCount => Assets.Count;

    /// <summary>
    /// Resolves a request path to a bundled asset. "/" serves index.html.
    /// Returns null when nothing matches, so the caller can 404 rather than
    /// silently serving the app shell for a mistyped asset URL.
    /// </summary>
    public static UiAsset? Find(string requestPath)
    {
        var relative = Normalize(requestPath);
        if (relative is null)
        {
            return null;
        }

        if (relative.Length == 0)
        {
            relative = IndexPath;
        }

        if (!Assets.TryGetValue(relative, out var resourceName))
        {
            return null;
        }

        using var stream = typeof(EmbeddedUi).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return new UiAsset(
            memory.ToArray(),
            ContentTypeFor(relative),
            // Vite fingerprints filenames under assets/, so those are safe to
            // cache forever; index.html must not be, or an updated exe would
            // keep serving the previous bundle's asset names.
            Immutable: relative.StartsWith("assets/", StringComparison.Ordinal));
    }

    /// <summary>Rejects absolute paths and any traversal outside the bundle.</summary>
    private static string? Normalize(string requestPath)
    {
        var relative = requestPath.Replace('\\', '/').TrimStart('/');
        relative = Uri.UnescapeDataString(relative);

        if (relative.Contains("..", StringComparison.Ordinal) ||
            relative.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        return relative;
    }

    private static IReadOnlyDictionary<string, string> Discover()
    {
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in typeof(EmbeddedUi).Assembly.GetManifestResourceNames())
        {
            // MSBuild writes the platform separator into LogicalName.
            var normalized = name.Replace('\\', '/');
            if (normalized.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                assets[normalized[Prefix.Length..]] = name;
            }
        }

        return assets;
    }

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" or ".map" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream"
        };
}

internal sealed record UiAsset(byte[] Bytes, string ContentType, bool Immutable);
