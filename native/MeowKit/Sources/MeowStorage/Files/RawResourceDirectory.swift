import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// Local atomic file directory for evictable media/image cache payloads under `<dataRoot>/resources-v1/<namespace>`.
/// All writes stage to a `.part` file, fsync, and atomically rename into place.
public final class RawResourceDirectory: @unchecked Sendable {
    public let directoryURL: URL

    public init(dataRoot: URL, namespace: String) throws {
        guard dataRoot.isFileURL, !dataRoot.path.isEmpty,
              namespace.range(of: "^[a-z0-9-]+$", options: .regularExpression) != nil else {
            throw SQLiteStorageError.invalidPath
        }
        let resourcesRoot = dataRoot.standardizedFileURL.appendingPathComponent("resources-v1", isDirectory: true)
        let leaf = resourcesRoot.appendingPathComponent(namespace, isDirectory: true)
        let fm = FileManager.default
        do {
            try fm.createDirectory(at: leaf, withIntermediateDirectories: true)
        } catch {
            throw SQLiteStorageError.io
        }
        #if os(iOS)
        Self.excludeFromBackup(resourcesRoot)
        Self.excludeFromBackup(leaf)
        #endif
        self.directoryURL = leaf
    }

    public func read(_ name: String, maximumBytes: Int) throws -> Data? {
        guard Self.isCommittedName(name), maximumBytes > 0 else { return nil }
        let fileURL = directoryURL.appendingPathComponent(name, isDirectory: false)
        let fm = FileManager.default
        guard fm.fileExists(atPath: fileURL.path) else { return nil }
        do {
            let attrs = try fm.attributesOfItem(atPath: fileURL.path)
            if let size = (attrs[.size] as? NSNumber)?.intValue, size <= 0 || size > maximumBytes {
                try? fm.removeItem(at: fileURL)
                return nil
            }
            let data = try Data(contentsOf: fileURL)
            guard !data.isEmpty, data.count <= maximumBytes else {
                try? fm.removeItem(at: fileURL)
                return nil
            }
            return data
        } catch {
            try? fm.removeItem(at: fileURL)
            return nil
        }
    }

    public func write(_ data: Data) throws -> String {
        let fm = FileManager.default
        if !fm.fileExists(atPath: directoryURL.path) {
            try fm.createDirectory(at: directoryURL, withIntermediateDirectories: true)
            #if os(iOS)
            Self.excludeFromBackup(directoryURL)
            #endif
        }
        let id = UUID().uuidString.lowercased()
        let tempName = id + ".part"
        let finalName = id + ".bin"
        let tempURL = directoryURL.appendingPathComponent(tempName, isDirectory: false)
        let finalURL = directoryURL.appendingPathComponent(finalName, isDirectory: false)

        let fd = open(tempURL.path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw SQLiteStorageError.io }
        var committed = false
        defer {
            _ = close(fd)
            if !committed { _ = unlink(tempURL.path) }
        }
        try Self.writeAll(data, to: fd)
        guard fsync(fd) == 0, rename(tempURL.path, finalURL.path) == 0 else {
            throw SQLiteStorageError.io
        }
        committed = true
        return finalName
    }

    public func remove(_ name: String) throws {
        guard Self.isOwnedName(name) else { return }
        let fileURL = directoryURL.appendingPathComponent(name, isDirectory: false)
        if unlink(fileURL.path) != 0 && errno != ENOENT {
            throw SQLiteStorageError.io
        }
    }

    /// Removes unreferenced `.bin` files as well as interrupted `.part` and stray leftover files.
    public func recover(referenced: Set<String>, strict: Bool = false) throws {
        let fm = FileManager.default
        guard fm.fileExists(atPath: directoryURL.path) else { return }
        let names: [String]
        do {
            names = try fm.contentsOfDirectory(atPath: directoryURL.path)
        } catch {
            throw SQLiteStorageError.io
        }
        for name in names {
            if Self.isCommittedName(name) && referenced.contains(name) { continue }
            let target = directoryURL.appendingPathComponent(name, isDirectory: false)
            if strict {
                do {
                    try fm.removeItem(at: target)
                } catch CocoaError.fileNoSuchFile {
                    continue
                } catch {
                    throw SQLiteStorageError.io
                }
            } else {
                try? fm.removeItem(at: target)
            }
        }
    }

    public func diskStats() -> (fileCount: Int, totalBytes: Int64) {
        let fm = FileManager.default
        guard let names = try? fm.contentsOfDirectory(atPath: directoryURL.path) else { return (0, 0) }
        var count = 0
        var total: Int64 = 0
        for name in names where Self.isCommittedName(name) {
            let path = directoryURL.appendingPathComponent(name, isDirectory: false).path
            if let attrs = try? fm.attributesOfItem(atPath: path),
               let size = (attrs[.size] as? NSNumber)?.int64Value, size >= 0 {
                count += 1
                total += size
            }
        }
        return (count, total)
    }

    private static func isCommittedName(_ name: String) -> Bool {
        let parts = name.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 2, parts[1] == "bin",
              let id = UUID(uuidString: String(parts[0])) else { return false }
        return id.uuidString.lowercased() == parts[0]
    }

    private static func isOwnedName(_ name: String) -> Bool {
        let parts = name.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 2, parts[1] == "bin" || parts[1] == "part",
              let id = UUID(uuidString: String(parts[0])) else { return false }
        return id.uuidString.lowercased() == parts[0]
    }

    private static func writeAll(_ data: Data, to fd: Int32) throws {
        try data.withUnsafeBytes { bytes in
            guard let base = bytes.baseAddress else { return }
            var offset = 0
            while offset < bytes.count {
                let written = pwrite(fd, base.advanced(by: offset), bytes.count - offset, off_t(offset))
                if written < 0 && errno == EINTR { continue }
                guard written > 0 else { throw SQLiteStorageError.io }
                offset += written
            }
        }
    }

    #if os(iOS)
    private static func excludeFromBackup(_ url: URL) {
        var mutable = url
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? mutable.setResourceValues(values)
    }
    #endif
}
