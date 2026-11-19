import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// Original media bytes only. Metadata and eligibility belong to repositories.
/// All leaf operations use a pinned, exclusively owned descriptor; unknown
/// contents and links are refused, never adopted or recursively removed.
public final class RawResourceDirectory: @unchecked Sendable {
    private let parent: Int32
    private let descriptor: Int32
    private let lock: Int32
    private let namespace: String
    private let device: UInt64
    private let inode: UInt64
    private static let marker = Data("meowfilm.raw-resources.v1\n".utf8)

    public init(dataRoot: URL, namespace: String) throws {
        guard dataRoot.isFileURL, dataRoot.path.hasPrefix("/"), !dataRoot.path.utf8.contains(0),
              namespace.range(of: "^[a-z0-9-]+$", options: .regularExpression) != nil else {
            throw SQLiteStorageError.invalidPath
        }
        // The database owner supplies the canonical App root. iOS grants access
        // to that container, not to each ancestor outside its sandbox.
        var fd = open(dataRoot.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw SQLiteStorageError.io }
        do {
            if mkdirat(fd, "resources-v1", 0o700) != 0 && errno != EEXIST { throw SQLiteStorageError.io }
            let root = openat(fd, "resources-v1", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard root >= 0 else { throw SQLiteStorageError.invalidPath }
            _ = close(fd); fd = root
            if mkdirat(fd, namespace, 0o700) != 0 && errno != EEXIST { throw SQLiteStorageError.io }
            let leaf = openat(fd, namespace, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard leaf >= 0 else { throw SQLiteStorageError.invalidPath }
            var locked: Int32 = -1
            do {
                var info = stat()
                guard fstat(leaf, &info) == 0, info.st_uid == geteuid(), info.st_mode & 0o077 == 0 else {
                    throw SQLiteStorageError.invalidPath
                }
                let names = try Self.names(leaf)
                if !names.contains(".owner") {
                    guard names.isEmpty else { throw SQLiteStorageError.invalidPath }
                    let owner = openat(leaf, ".owner", O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
                    guard owner >= 0 else { throw SQLiteStorageError.io }
                    defer { _ = close(owner) }
                    try Self.write(Self.marker, descriptor: owner)
                    guard fsync(owner) == 0 else { throw SQLiteStorageError.io }
                }
                guard try Self.read(".owner", at: leaf, maximum: 64) == Self.marker else { throw SQLiteStorageError.invalidPath }
                locked = openat(leaf, ".lock", O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0o600)
                guard locked >= 0, flock(locked, LOCK_EX | LOCK_NB) == 0 else { throw SQLiteStorageError.busy }
                _ = try Self.regular(locked)
                #if os(iOS)
                var values = URLResourceValues(); values.isExcludedFromBackup = true
                var url = dataRoot.appendingPathComponent("resources-v1").appendingPathComponent(namespace)
                try url.setResourceValues(values)
                #endif
                parent = fd; descriptor = leaf; lock = locked
                device = UInt64(info.st_dev); inode = UInt64(info.st_ino); self.namespace = namespace
            } catch {
                if locked >= 0 { _ = close(locked) }
                _ = close(leaf); throw error
            }
        } catch { _ = close(fd); throw error }
    }
    private static func regular(_ fd: Int32) throws -> stat {
        var value = stat()
        guard fstat(fd, &value) == 0, value.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG),
              value.st_nlink == 1, value.st_uid == geteuid() else { throw SQLiteStorageError.invalidPath }
        return value
    }
    private func check() throws {
        var value = stat()
        guard fstatat(parent, namespace, &value, AT_SYMLINK_NOFOLLOW) == 0,
              value.st_mode & mode_t(S_IFMT) == mode_t(S_IFDIR),
              UInt64(value.st_dev) == device, UInt64(value.st_ino) == inode else { throw SQLiteStorageError.invalidPath }
    }
    private static func owned(_ name: String) -> Bool {
        let parts = name.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 2, ["bin", "part"].contains(String(parts[1])),
              let id = UUID(uuidString: String(parts[0])) else { return false }
        return id.uuidString.lowercased() == parts[0]
    }
    private static func read(_ name: String, at fd: Int32, maximum: Int) throws -> Data? {
        let file = openat(fd, name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        if file < 0 { if errno == ENOENT { return nil }; throw SQLiteStorageError.io }
        defer { _ = close(file) }
        let info = try regular(file)
        guard info.st_size >= 0, info.st_size <= maximum else { throw SQLiteStorageError.invalidValue }
        let length = Int(info.st_size)
        var result = Data(count: length), offset = 0
        try result.withUnsafeMutableBytes { bytes in
            while offset < length {
                let count = pread(file, bytes.baseAddress!.advanced(by: offset), length - offset, off_t(offset))
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw SQLiteStorageError.io }
                offset += count
            }
        }
        return result
    }
    public func read(_ name: String, maximumBytes: Int) throws -> Data? {
        try check()
        guard Self.owned(name), name.hasSuffix(".bin") else { throw SQLiteStorageError.invalidPath }
        return try Self.read(name, at: descriptor, maximum: maximumBytes)
    }
    private static func write(_ data: Data, descriptor fd: Int32) throws {
        try data.withUnsafeBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let count = pwrite(fd, bytes.baseAddress!.advanced(by: offset), bytes.count - offset, off_t(offset))
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw SQLiteStorageError.io }
                offset += count
            }
        }
    }
    public func write(_ data: Data) throws -> String {
        try check()
        let id = UUID().uuidString.lowercased(), temporary = id + ".part", final = id + ".bin"
        let file = openat(descriptor, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard file >= 0 else { throw SQLiteStorageError.io }
        defer { _ = close(file); _ = unlinkat(descriptor, temporary, 0) }
        try Self.write(data, descriptor: file)
        guard fsync(file) == 0, renameat(descriptor, temporary, descriptor, final) == 0 else { throw SQLiteStorageError.io }
        _ = fsync(descriptor)
        return final
    }
    public func remove(_ name: String) throws {
        try check()
        guard Self.owned(name) else { throw SQLiteStorageError.invalidPath }
        let file = openat(descriptor, name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        if file < 0 { if errno == ENOENT { return }; throw SQLiteStorageError.io }
        defer { _ = close(file) }
        _ = try Self.regular(file)
        guard unlinkat(descriptor, name, 0) == 0 else { throw SQLiteStorageError.io }
    }
    public func recover(referenced: Set<String>) throws {
        try check()
        let names = try Self.names(descriptor)
        guard names.allSatisfy({ $0 == ".owner" || $0 == ".lock" || Self.owned($0) }) else { throw SQLiteStorageError.invalidPath }
        for name in names where Self.owned(name) && !referenced.contains(name) { try remove(name) }
    }
    private static func names(_ fd: Int32) throws -> [String] {
        let duplicate = dup(fd)
        guard duplicate >= 0 else { throw SQLiteStorageError.io }
        guard let stream = fdopendir(duplicate) else { _ = close(duplicate); throw SQLiteStorageError.io }
        defer { closedir(stream) }
        rewinddir(stream)
        var names: [String] = []
        while let entry = readdir(stream) {
            let name = withUnsafePointer(to: &entry.pointee.d_name) {
                $0.withMemoryRebound(to: CChar.self, capacity: MemoryLayout.size(ofValue: entry.pointee.d_name)) { String(cString: $0) }
            }
            if name != "." && name != ".." { names.append(name) }
        }
        return names
    }
    deinit { _ = close(lock); _ = close(descriptor); _ = close(parent) }
}
