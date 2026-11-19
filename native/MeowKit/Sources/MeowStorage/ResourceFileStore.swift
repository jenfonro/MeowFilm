import Foundation
import Darwin

/// Ordinary raw byte leaves, with no persistent index or document envelope.
/// One owner serializes a namespace; enumeration reads file metadata, never content.
public final class ResourceFileStore: @unchecked Sendable {
    public struct Metadata: Sendable {
        public let key: String
        public let byteCount: Int
        public let modifiedAt: Date
        /// Non-nil only for an unpublished stage belonging to this resource key.
        public let stageName: String?
    }
    private let files: OrdinaryFileDirectory
    private let lock = NSLock()

    public init(baseURL: URL, namespace: String) throws {
        files = try OrdinaryFileDirectory(baseURL: baseURL, namespace: namespace)
    }

    public func inventory() throws -> [Metadata] {
        lock.lock(); defer { lock.unlock() }
        guard let directory = try files.openDirectory(creating: false) else { return [] }
        defer { Darwin.close(directory) }
        let copy = fcntl(directory, F_DUPFD_CLOEXEC, 0)
        guard copy >= 0 else { throw ResourceFileError.storageFailure }
        guard let stream = fdopendir(copy) else {
            Darwin.close(copy); throw ResourceFileError.storageFailure
        }
        defer { closedir(stream) }
        var result: [Metadata] = []
        while true {
            errno = 0
            guard let entry = readdir(stream) else {
                guard errno == 0 else { throw ResourceFileError.storageFailure }
                break
            }
            let name = withUnsafePointer(to: &entry.pointee.d_name) {
                $0.withMemoryRebound(to: CChar.self, capacity: Int(entry.pointee.d_namlen) + 1) {
                    String(cString: $0)
                }
            }
            let stageKey = Self.stageKey(name)
            let key = stageKey ?? (name.hasSuffix(".bytes") ? String(name.dropLast(6)) : "")
            guard OrdinaryFileDirectory.validIdentifier(key) else { continue }
            var info = stat()
            guard fstatat(directory, name, &info, AT_SYMLINK_NOFOLLOW) == 0,
                  Self.ordinary(info) else { continue }
            result.append(Self.metadata(key: key, info: info, stageName: stageKey == nil ? nil : name))
        }
        return result
    }

    public func read(key: String, maximumBytes: Int) throws -> Data? {
        let name = try fileName(key)
        lock.lock(); defer { lock.unlock() }
        guard let directory = try files.openDirectory(creating: false) else { return nil }
        defer { Darwin.close(directory) }
        return try files.contents(name, in: directory, maximumBytes: maximumBytes)
    }

    /// The commit callback fences only the atomic publication, not staging I/O.
    /// A failed stage deletion is reported to the owner for continued byte charging.
    public func write(_ data: Data, key: String, modifiedAt: Date,
                      retainedStage: (Metadata) -> Void = { _ in },
                      commit: (() throws -> Void) throws -> Void = { try $0() }) throws {
        let name = try fileName(key)
        lock.lock(); defer { lock.unlock() }
        guard let directory = try files.openDirectory(creating: true) else {
            throw ResourceFileError.storageFailure
        }
        defer { Darwin.close(directory) }
        try files.replace(name, with: data, in: directory, modifiedAt: modifiedAt, retainedStage: { stage, bytes in
            retainedStage(.init(key: key, byteCount: bytes, modifiedAt: modifiedAt, stageName: stage))
        }, commit: commit)
    }

    /// A real playback renewal changes mtime only; identical/older times do not write.
    public func touch(key: String, modifiedAt: Date,
                      commit: (() throws -> Void) throws -> Void = { try $0() }) throws -> Date? {
        let name = try fileName(key)
        lock.lock(); defer { lock.unlock() }
        guard let directory = try files.openDirectory(creating: false) else { return nil }
        defer { Darwin.close(directory) }
        let fd = openat(directory, name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else {
            if errno == ENOENT { return nil }
            throw errno == ELOOP ? ResourceFileError.unsafePath : ResourceFileError.storageFailure
        }
        defer { Darwin.close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0 else { throw ResourceFileError.storageFailure }
        guard Self.ordinary(info) else { throw ResourceFileError.unsafePath }
        let current = Self.metadata(key: key, info: info).modifiedAt
        guard modifiedAt > current else { return current }
        try commit { try OrdinaryFileDirectory.setModifiedAt(modifiedAt, fd: fd) }
        return modifiedAt
    }

    public func remove(key: String) throws {
        try removeFile(fileName(key))
    }

    public func removeStage(name: String) throws {
        guard Self.stageKey(name) != nil else { throw ResourceFileError.invalidKey }
        try removeFile(name)
    }

    private func removeFile(_ name: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let directory = try files.openDirectory(creating: false) else { return }
        defer { Darwin.close(directory) }
        guard unlinkat(directory, name, 0) == 0 || errno == ENOENT else {
            throw ResourceFileError.storageFailure
        }
    }

    private func fileName(_ key: String) throws -> String {
        guard OrdinaryFileDirectory.validIdentifier(key) else { throw ResourceFileError.invalidKey }
        return key + ".bytes"
    }
    private static func ordinary(_ info: stat) -> Bool {
        info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) && info.st_nlink == 1 && info.st_size >= 0
    }
    private static func stageKey(_ name: String) -> String? {
        guard name.hasPrefix("."), name.hasSuffix(".stage") else { return nil }
        let body = String(name.dropFirst().dropLast(6))
        guard let separator = body.range(of: ".bytes.", options: .backwards) else { return nil }
        let key = String(body[..<separator.lowerBound]), identity = String(body[separator.upperBound...])
        guard OrdinaryFileDirectory.validIdentifier(key), UUID(uuidString: identity)?.uuidString.lowercased() == identity else {
            return nil
        }
        return key
    }
    private static func metadata(key: String, info: stat, stageName: String? = nil) -> Metadata {
        .init(key: key, byteCount: Int(info.st_size), modifiedAt: Date(timeIntervalSince1970:
            Double(info.st_mtimespec.tv_sec) + Double(info.st_mtimespec.tv_nsec) / 1_000_000_000), stageName: stageName)
    }
}
