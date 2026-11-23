import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// Original media bytes only. Metadata and eligibility belong to repositories.
public final class RawResourceDirectory: @unchecked Sendable {
    private let directoryURL: URL
    private let lock: Int32

    public init(dataRoot: URL, namespace: String) throws {
        guard dataRoot.isFileURL, dataRoot.path.hasPrefix("/"), !dataRoot.path.utf8.contains(0),
              namespace.range(of: "^[a-z0-9-]+$", options: .regularExpression) != nil else {
            throw SQLiteStorageError.invalidPath
        }
        let target = dataRoot
            .appendingPathComponent("resources-v1", isDirectory: true)
            .appendingPathComponent(namespace, isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true,
                                                    attributes: [.posixPermissions: 0o700])
        } catch {
            throw SQLiteStorageError.io
        }
        let lockURL = target.appendingPathComponent(".lock")
        let locked = open(lockURL.path, O_RDWR | O_CREAT | O_CLOEXEC, 0o600)
        guard locked >= 0, flock(locked, LOCK_EX | LOCK_NB) == 0 else {
            if locked >= 0 { _ = close(locked) }
            throw SQLiteStorageError.busy
        }
        #if os(iOS)
        do {
            var values = URLResourceValues()
            values.isExcludedFromBackup = true
            var url = target
            try url.setResourceValues(values)
        } catch {
            _ = close(locked)
            throw SQLiteStorageError.io
        }
        #endif
        self.directoryURL = target
        self.lock = locked
    }

    private static func owned(_ name: String) -> Bool {
        let parts = name.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 2, ["bin", "part"].contains(String(parts[1])),
              let id = UUID(uuidString: String(parts[0])) else { return false }
        return id.uuidString.lowercased() == parts[0]
    }

    public func read(_ name: String, maximumBytes: Int) throws -> Data? {
        guard Self.owned(name), name.hasSuffix(".bin") else { throw SQLiteStorageError.invalidPath }
        let fileURL = directoryURL.appendingPathComponent(name)
        var info = stat()
        if stat(fileURL.path, &info) != 0 {
            if errno == ENOENT { return nil }
            throw SQLiteStorageError.io
        }
        guard info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG),
              info.st_size >= 0, info.st_size <= maximumBytes else {
            throw SQLiteStorageError.invalidValue
        }
        do { return try Data(contentsOf: fileURL) }
        catch { throw SQLiteStorageError.io }
    }

    public func write(_ data: Data) throws -> String {
        let final = UUID().uuidString.lowercased() + ".bin"
        do {
            try data.write(to: directoryURL.appendingPathComponent(final), options: .atomic)
            return final
        } catch {
            throw SQLiteStorageError.io
        }
    }

    public func remove(_ name: String) throws {
        guard Self.owned(name) else { throw SQLiteStorageError.invalidPath }
        let fileURL = directoryURL.appendingPathComponent(name)
        if unlink(fileURL.path) != 0 && errno != ENOENT {
            throw SQLiteStorageError.io
        }
    }

    public func recover(referenced: Set<String>) throws {
        let names: [String]
        do { names = try FileManager.default.contentsOfDirectory(atPath: directoryURL.path) }
        catch { throw SQLiteStorageError.io }
        for name in names where Self.owned(name) && !referenced.contains(name) {
            try remove(name)
        }
    }

    deinit { _ = close(lock) }
}
