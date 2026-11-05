import Foundation
import Darwin

/// Closed errors without paths, identifiers or document bytes.
public enum DocumentStoreError: Error, Equatable, Sendable {
    case invalidNamespace
    case invalidKey
    case invalidBaseURL
    case unsafePath
    case documentTooLarge
    case revisionConflict
    case storageFailure
}

/// Ordinary local documents: the caller's exact bytes in `<base>/<namespace>/<key>.json`,
/// readable and editable outside the App. No encryption, integrity envelope, namespace
/// quota or inventory gate; JSON and business validity belong to the consumer.
///
/// Product assembly owns one instance per namespace under a dedicated, app-owned base
/// directory. Methods are synchronous and serialized by one lock. Independent writers or
/// processes are not supported; an external edit simply becomes the current content.
/// Same-process malicious filesystem mutation is not a security boundary.
public final class DocumentStore: @unchecked Sendable {
    private let base: String
    private let namespace: String
    private let maximumDocumentBytes: Int
    private let lock = NSLock()

    /// Passive: validates the namespace and URL, never creates a directory.
    public init(baseURL: URL, namespace: String, maximumDocumentBytes: Int = 8 * 1_024 * 1_024) throws {
        guard Self.validIdentifier(namespace) else { throw DocumentStoreError.invalidNamespace }
        guard baseURL.isFileURL, baseURL.path.hasPrefix("/"), !baseURL.path.utf8.contains(0) else {
            throw DocumentStoreError.invalidBaseURL
        }
        base = baseURL.path
        self.namespace = namespace
        self.maximumDocumentBytes = maximumDocumentBytes
    }

    /// nil means absent. An existing empty file is an empty document.
    public func read(key: String) throws -> Data? {
        let name = try fileName(key)
        lock.lock(); defer { lock.unlock() }
        guard let directory = try openDirectory(creating: false) else { return nil }
        defer { Darwin.close(directory) }
        return try contents(name, in: directory)
    }

    public func write(_ data: Data, key: String) throws {
        let name = try fileName(key)
        guard data.count <= maximumDocumentBytes else { throw DocumentStoreError.documentTooLarge }
        lock.lock(); defer { lock.unlock() }
        guard let directory = try openDirectory(creating: true) else { throw DocumentStoreError.storageFailure }
        defer { Darwin.close(directory) }
        try replace(name, with: data, in: directory)
    }

    /// Replaces the document only if its current content equals `expected` (nil means
    /// absent). This is a content comparison, not a monotonic revision.
    public func compareAndSwap(key: String, expected: Data?, replacement: Data) throws {
        let name = try fileName(key)
        guard replacement.count <= maximumDocumentBytes else { throw DocumentStoreError.documentTooLarge }
        lock.lock(); defer { lock.unlock() }
        guard let directory = try openDirectory(creating: expected == nil) else {
            throw DocumentStoreError.revisionConflict
        }
        defer { Darwin.close(directory) }
        guard try contents(name, in: directory) == expected else { throw DocumentStoreError.revisionConflict }
        try replace(name, with: replacement, in: directory)
    }

    /// Missing documents are a no-op. Only this store's directory entry is removed;
    /// a linked target is never followed.
    public func remove(key: String) throws {
        let name = try fileName(key)
        lock.lock(); defer { lock.unlock() }
        guard let directory = try openDirectory(creating: false) else { return }
        defer { Darwin.close(directory) }
        guard unlinkat(directory, name, 0) == 0 || errno == ENOENT else { throw DocumentStoreError.storageFailure }
    }

    private func fileName(_ key: String) throws -> String {
        guard Self.validIdentifier(key) else { throw DocumentStoreError.invalidKey }
        return key + ".json"
    }

    /// Lowercase ASCII only, so a case-insensitive volume cannot alias two identifiers
    /// and a readable name cannot traverse, hide or collide with a staging file.
    static func validIdentifier(_ value: String) -> Bool {
        let bytes = Array(value.utf8)
        func alphanumeric(_ byte: UInt8) -> Bool { (48...57).contains(byte) || (97...122).contains(byte) }
        guard let first = bytes.first, bytes.count <= 128, alphanumeric(first), !value.contains("..") else {
            return false
        }
        return bytes.allSatisfy { alphanumeric($0) || $0 == 45 || $0 == 46 || $0 == 95 }
    }

    /// nil only when the namespace directory is absent and `creating` is false.
    /// The base and namespace components are opened without following a final link.
    private func openDirectory(creating: Bool) throws -> Int32? {
        let flags = O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC
        let parent = Darwin.open(base, flags)
        guard parent >= 0 else {
            if errno == ENOENT && !creating { return nil }
            throw Self.pathFailure(errno)
        }
        defer { Darwin.close(parent) }
        var fd = openat(parent, namespace, flags)
        if fd < 0 && errno == ENOENT {
            guard creating else { return nil }
            guard mkdirat(parent, namespace, 0o700) == 0 || errno == EEXIST else {
                throw DocumentStoreError.storageFailure
            }
            fd = openat(parent, namespace, flags)
        }
        guard fd >= 0 else { throw Self.pathFailure(errno) }
        return fd
    }

    private static func pathFailure(_ code: Int32) -> DocumentStoreError {
        code == ELOOP || code == ENOTDIR ? .unsafePath : .storageFailure
    }

    private func contents(_ name: String, in directory: Int32) throws -> Data? {
        let fd = openat(directory, name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else {
            if errno == ENOENT { return nil }
            throw errno == ELOOP ? DocumentStoreError.unsafePath : DocumentStoreError.storageFailure
        }
        defer { Darwin.close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0 else { throw DocumentStoreError.storageFailure }
        // Directories, FIFOs and hard-linked aliases are not this store's documents.
        guard info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG), info.st_nlink == 1 else {
            throw DocumentStoreError.unsafePath
        }
        guard info.st_size <= off_t(maximumDocumentBytes) else { throw DocumentStoreError.documentTooLarge }
        // Bound the allocation before reading; reject short reads and growth.
        let count = Int(info.st_size)
        var bytes = Data(count: count)
        var offset = 0
        while offset < count {
            let length = bytes.withUnsafeMutableBytes { buffer in
                Darwin.read(fd, buffer.baseAddress!.advanced(by: offset), count - offset)
            }
            if length < 0 && errno == EINTR { continue }
            guard length > 0 else { throw DocumentStoreError.storageFailure }
            offset += length
        }
        var extra: UInt8 = 0
        var extraCount: Int
        repeat { extraCount = Darwin.read(fd, &extra, 1) } while extraCount < 0 && errno == EINTR
        guard extraCount == 0 else { throw DocumentStoreError.storageFailure }
        return bytes
    }

    /// A uniquely named stage in the same directory is synced, then renamed over the
    /// destination. A failed operation removes only its own stage.
    private func replace(_ name: String, with data: Data, in directory: Int32) throws {
        let stage = "." + name + "." + UUID().uuidString.lowercased() + ".stage"
        let fd = openat(directory, stage, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw DocumentStoreError.storageFailure }
        var committed = false
        defer { if !committed { _ = unlinkat(directory, stage, 0) } }
        do {
            defer { Darwin.close(fd) }
            try data.withUnsafeBytes { buffer in
                var offset = 0
                while offset < buffer.count {
                    let length = Darwin.write(fd, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
                    if length < 0 && errno == EINTR { continue }
                    guard length > 0 else { throw DocumentStoreError.storageFailure }
                    offset += length
                }
            }
            guard fsync(fd) == 0 else { throw DocumentStoreError.storageFailure }
        }
        guard renameat(directory, stage, directory, name) == 0 else { throw DocumentStoreError.storageFailure }
        committed = true
    }
}
