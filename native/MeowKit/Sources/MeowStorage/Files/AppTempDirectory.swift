import Foundation

/// App-owned ephemeral staging directory under `<dataRoot>/tmp`.
/// Swept automatically on startup so interrupted exports never linger outside `--data-root`.
public enum AppTempDirectory {
    private static let lock = NSLock()
    private static var configuredRoot: URL?

    public static func configure(dataRoot: URL) throws -> URL {
        let root = dataRoot.standardizedFileURL.appendingPathComponent("tmp", isDirectory: true)
        let fm = FileManager.default
        if fm.fileExists(atPath: root.path) {
            // Sweep leftover export/staging artifacts from previous runs.
            if let children = try? fm.contentsOfDirectory(atPath: root.path) {
                for child in children {
                    try? fm.removeItem(at: root.appendingPathComponent(child))
                }
            }
        } else {
            try fm.createDirectory(at: root, withIntermediateDirectories: true)
        }
        #if os(iOS)
        var mutable = root
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? mutable.setResourceValues(values)
        #endif
        lock.withLock { configuredRoot = root }
        return root
    }

    public static func rootURL() -> URL {
        if let root = lock.withLock({ configuredRoot }) {
            return root
        }
        return FileManager.default.temporaryDirectory.appendingPathComponent("meowfilm", isDirectory: true)
    }

    public static func sanitizedFilename(_ raw: String, fallback: String = "export.txt") -> String {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        var cleaned = trimmed
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "\\", with: "_")
            .replacingOccurrences(of: "..", with: "_")
            .filter { scalar in !scalar.unicodeScalars.contains(where: { $0.value < 0x20 || $0.value == 0x7f }) }
        while cleaned.hasPrefix(".") { cleaned.removeFirst() }
        if cleaned.isEmpty || cleaned == "." || cleaned == ".." || cleaned.allSatisfy({ $0 == "." || $0 == "_" }) {
            return fallback
        }
        return cleaned
    }
}
