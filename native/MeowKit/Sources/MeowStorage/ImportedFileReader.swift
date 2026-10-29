import Foundation
#if canImport(Darwin)
import Darwin
#endif

/// Closed diagnostics: file-provider errors may contain a private path or URL.
public enum ImportedFileError: Error, Equatable, Sendable, LocalizedError {
    case invalidFile, unavailable, tooLarge, changed, cancelled

    public var errorDescription: String? {
        switch self {
        case .invalidFile: return "文件无效"
        case .unavailable: return "文件不可读取"
        case .tooLarge: return "文件超过大小限制"
        case .changed: return "文件已更改，请重新选择"
        case .cancelled: return "已取消"
        }
    }
}

/// Read-only, bounded document-picker import. No bookmark, upload, persistent
/// copy, or source deletion is performed here. Parsing belongs to the caller.
///
/// The limit applies to bytes actually read, not just a possibly stale URL
/// resource-size hint. File-provider coordination and disk I/O run off main.
/// Cancellation is cooperative; this does not claim to preempt a stalled
/// external file provider or provide a snapshot against hostile local writers.
public enum ImportedFileReader {
    public static let maximumSupportedBytes = 32 * 1024 * 1024

    public static func read(_ url: URL, maximumBytes: Int) async throws -> Data {
        guard maximumBytes > 0, maximumBytes <= maximumSupportedBytes,
              url.isFileURL, url.user == nil, url.password == nil,
              url.host == nil || url.host == "" || url.host == "localhost" else {
            throw ImportedFileError.invalidFile
        }
        try checkCancellation()
        let work = Task.detached(priority: .userInitiated) {
            try checkCancellation()
            #if canImport(Darwin)
            let access = url.startAccessingSecurityScopedResource()
            defer { if access { url.stopAccessingSecurityScopedResource() } }
            let coordinator = NSFileCoordinator()
            var coordinationError: NSError?
            var result: Result<Data, Error>?
            coordinator.coordinate(readingItemAt: url, options: .withoutChanges,
                                   error: &coordinationError) { coordinatedURL in
                result = Result { try readCoordinated(coordinatedURL, maximumBytes: maximumBytes) }
            }
            try checkCancellation()
            guard coordinationError == nil, let result else { throw ImportedFileError.unavailable }
            do { return try result.get() }
            catch let error as ImportedFileError { throw error }
            catch { throw ImportedFileError.unavailable }
            #else
            return try readCoordinated(url, maximumBytes: maximumBytes)
            #endif
        }
        return try await withTaskCancellationHandler {
            let bytes = try await work.value
            try checkCancellation()
            return bytes
        } onCancel: {
            work.cancel()
        }
    }

    private static func checkCancellation() throws {
        if Task.isCancelled { throw ImportedFileError.cancelled }
    }

    private static func readCoordinated(_ url: URL, maximumBytes: Int) throws -> Data {
        try checkCancellation()
        do {
            let handle = try FileHandle(forReadingFrom: url)
            defer { try? handle.close() }
            #if canImport(Darwin)
            var before = stat()
            guard fstat(handle.fileDescriptor, &before) == 0,
                  (before.st_mode & mode_t(S_IFMT)) == mode_t(S_IFREG) else {
                throw ImportedFileError.invalidFile
            }
            guard before.st_size >= 0, before.st_size <= Int64(maximumBytes) else {
                throw ImportedFileError.tooLarge
            }
            #else
            let values = try url.resourceValues(forKeys: [.isRegularFileKey])
            guard values.isRegularFile == true else { throw ImportedFileError.invalidFile }
            #endif
            let bytes = try readBounded(maximumBytes: maximumBytes) { count in
                try checkCancellation()
                return try handle.read(upToCount: count)
            }
            try checkCancellation()
            #if canImport(Darwin)
            var after = stat()
            guard fstat(handle.fileDescriptor, &after) == 0 else { throw ImportedFileError.unavailable }
            guard before.st_dev == after.st_dev, before.st_ino == after.st_ino,
                  before.st_size == after.st_size, Int64(bytes.count) == after.st_size,
                  before.st_mtimespec.tv_sec == after.st_mtimespec.tv_sec,
                  before.st_mtimespec.tv_nsec == after.st_mtimespec.tv_nsec else {
                throw ImportedFileError.changed
            }
            #endif
            return bytes
        } catch let error as ImportedFileError { throw error }
        catch { throw ImportedFileError.unavailable }
    }

    /// Internal deterministic test seam for short reads, growth and EOF. Read
    /// requests never exceed remaining budget + one overflow-detection byte.
    static func readBounded(maximumBytes: Int, read: (Int) throws -> Data?) throws -> Data {
        guard maximumBytes > 0, maximumBytes <= maximumSupportedBytes else {
            throw ImportedFileError.invalidFile
        }
        var output = Data()
        while true {
            try checkCancellation()
            let requested = min(64 * 1024, maximumBytes - output.count + 1)
            guard let chunk = try read(requested), !chunk.isEmpty else { return output }
            guard chunk.count <= requested, chunk.count <= maximumBytes - output.count else {
                throw ImportedFileError.tooLarge
            }
            output.append(chunk)
        }
    }
}
