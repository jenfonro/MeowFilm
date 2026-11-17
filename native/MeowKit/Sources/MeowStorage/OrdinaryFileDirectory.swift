import Foundation
import Darwin

/// Descriptor-relative ordinary files shared by documents and disposable byte resources.
/// Callers own serialization, file names, and business policy.
struct OrdinaryFileDirectory: Sendable {
    private let base: String
    private let namespace: String

    init(baseURL: URL, namespace: String) throws {
        guard DocumentStore.validIdentifier(namespace) else { throw DocumentStoreError.invalidNamespace }
        guard baseURL.isFileURL, baseURL.path.hasPrefix("/"), !baseURL.path.utf8.contains(0) else {
            throw DocumentStoreError.invalidBaseURL
        }
        base = baseURL.path; self.namespace = namespace
    }

    /// nil only when the namespace directory is absent and `creating` is false.
    /// The base and namespace components are opened without following a final link.
    func openDirectory(creating: Bool) throws -> Int32? {
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

    func contents(_ name: String, in directory: Int32, maximumBytes: Int) throws -> Data? {
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
        guard info.st_size <= off_t(maximumBytes) else { throw DocumentStoreError.documentTooLarge }
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
    func replace(_ name: String, with data: Data, in directory: Int32, modifiedAt: Date? = nil,
                 retainedStage: (String, Int) -> Void = { _, _ in },
                 commit: (() throws -> Void) throws -> Void = { try $0() }) throws {
        let stage = "." + name + "." + UUID().uuidString.lowercased() + ".stage"
        let fd = openat(directory, stage, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw DocumentStoreError.storageFailure }
        var committed = false, written = 0
        defer {
            if !committed, unlinkat(directory, stage, 0) != 0, errno != ENOENT {
                retainedStage(stage, written)
            }
        }
        do {
            defer { Darwin.close(fd) }
            try data.withUnsafeBytes { buffer in
                while written < buffer.count {
                    let length = Darwin.write(fd, buffer.baseAddress!.advanced(by: written), buffer.count - written)
                    if length < 0 && errno == EINTR { continue }
                    guard length > 0 else { throw DocumentStoreError.storageFailure }
                    written += length
                }
            }
            if let modifiedAt { try Self.setModifiedAt(modifiedAt, fd: fd) }
            guard fsync(fd) == 0 else { throw DocumentStoreError.storageFailure }
        }
        try commit {
            guard renameat(directory, stage, directory, name) == 0 else { throw DocumentStoreError.storageFailure }
            committed = true
        }
    }

    static func setModifiedAt(_ date: Date, fd: Int32) throws {
        let value = date.timeIntervalSince1970
        guard value.isFinite else { throw DocumentStoreError.storageFailure }
        let seconds = floor(value)
        var times = [timespec(tv_sec: 0, tv_nsec: Int(UTIME_OMIT)),
                     timespec(tv_sec: Int(seconds), tv_nsec: Int((value - seconds) * 1_000_000_000))]
        guard futimens(fd, &times) == 0 else { throw DocumentStoreError.storageFailure }
    }
}
