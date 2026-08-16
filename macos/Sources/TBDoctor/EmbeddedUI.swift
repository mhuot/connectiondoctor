import Foundation

/// The Connection Dashboard bundle compiled into TBDoctor, so a user needs one
/// download and no Node. Staged by `scripts/build-ui.sh`; absent in a plain
/// source build, in which case `--serve` says so instead of failing.
///
/// The route rules here are the shared ones ConnectionDoctor implements on
/// Windows — see `docs/embedding.md` at the repo root — so the same bundle
/// behaves identically whichever producer is serving it.
enum EmbeddedUI {

    struct Asset {
        let bytes: Data
        let contentType: String
        /// Vite fingerprints filenames under assets/, so those cache forever.
        let immutable: Bool
    }

    /// Root of the staged bundle, or nil when nothing was staged.
    private static let root: URL? = {
        guard let url = Bundle.module.url(forResource: "ui", withExtension: nil) else { return nil }
        let index = url.appendingPathComponent("index.html")
        return FileManager.default.fileExists(atPath: index.path) ? url : nil
    }()

    static var isPresent: Bool { root != nil }

    /// Resolves a request path to a bundled asset. "/" serves index.html.
    /// Returns nil when nothing matches, so the caller 404s rather than
    /// silently serving the app shell for a mistyped asset URL.
    static func find(_ requestPath: String) -> Asset? {
        guard let root, var relative = normalize(requestPath) else { return nil }
        if relative.isEmpty { relative = "index.html" }

        let base = root.standardizedFileURL.resolvingSymlinksInPath()
        let candidate = base.appendingPathComponent(relative)
            .standardizedFileURL.resolvingSymlinksInPath()

        // Belt and braces behind normalize(): confirm the resolved path is
        // still inside the bundle, since a symlink could otherwise escape it.
        guard candidate.path.hasPrefix(base.path + "/"),
              let bytes = try? Data(contentsOf: candidate) else { return nil }

        return Asset(bytes: bytes,
                     contentType: contentType(for: relative),
                     immutable: relative.hasPrefix("assets/"))
    }

    /// Rejects absolute paths and any traversal outside the bundle.
    private static func normalize(_ requestPath: String) -> String? {
        let decoded = requestPath.removingPercentEncoding ?? requestPath
        var relative = decoded.replacingOccurrences(of: "\\", with: "/")
        while relative.hasPrefix("/") { relative.removeFirst() }
        if relative.contains("..") || relative.contains(":") { return nil }
        return relative
    }

    private static func contentType(for path: String) -> String {
        switch (path as NSString).pathExtension.lowercased() {
        case "html":         return "text/html; charset=utf-8"
        case "js", "mjs":    return "text/javascript; charset=utf-8"
        case "css":          return "text/css; charset=utf-8"
        case "json", "map":  return "application/json; charset=utf-8"
        case "svg":          return "image/svg+xml"
        case "png":          return "image/png"
        case "jpg", "jpeg":  return "image/jpeg"
        case "webp":         return "image/webp"
        case "ico":          return "image/x-icon"
        case "woff2":        return "font/woff2"
        case "woff":         return "font/woff"
        case "ttf":          return "font/ttf"
        default:             return "application/octet-stream"
        }
    }
}
