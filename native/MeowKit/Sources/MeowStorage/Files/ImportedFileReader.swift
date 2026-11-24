import Foundation
#if canImport(Darwin)
import Darwin
#endif

/// Closed diagnostics: file-provider errors may contain a private path or URL.
public enum ImportedFileError: Error, Equatable, Sendable, LocalizedError {
    case invalidFile, unavailable, cancelled

    public var errorDescription: String? {
        switch self {
        case .invalidFile: return "文件无效"
        case .unavailable: return "文件不可读取"
        case .cancelled: return "已取消"
        }
    }
}

/// Read-only document-picker import. No bookmark, upload, persistent
/// copy, or source deletion is performed here. Parsing belongs to the caller.
///
/// File-provider coordination and disk I/O run off main.
/// Cancellation is cooperative; this does not claim to preempt a stalled
/// external file provider or provide a snapshot against hostile local writers.
public enum ImportedFileReader {
    public static func read(_ url: URL) async throws -> Data {
        guard url.isFileURL, url.user == nil, url.password == nil,
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
                result = Result { try readCoordinated(coordinatedURL) }
            }
            try checkCancellation()
            guard coordinationError == nil, let result else { throw ImportedFileError.unavailable }
            do { return try result.get() }
            catch let error as ImportedFileError { throw error }
            catch { throw ImportedFileError.unavailable }
            #else
            return try readCoordinated(url)
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

    private static func readCoordinated(_ url: URL) throws -> Data {
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
            guard before.st_size >= 0 else { throw ImportedFileError.invalidFile }
            #else
            let values = try url.resourceValues(forKeys: [.isRegularFileKey])
            guard values.isRegularFile == true else { throw ImportedFileError.invalidFile }
            #endif
            let bytes = try readChunks { count in
                try checkCancellation()
                return try handle.read(upToCount: count)
            }
            try checkCancellation()
            return bytes
        } catch let error as ImportedFileError { throw error }
        catch { throw ImportedFileError.unavailable }
    }

    private static func readChunks(read: (Int) throws -> Data?) throws -> Data {
        var output = Data()
        while true {
            try checkCancellation()
            let requested = 64 * 1024
            guard let chunk = try read(requested), !chunk.isEmpty else { return output }
            guard chunk.count <= requested else { throw ImportedFileError.invalidFile }
            output.append(chunk)
        }
    }
}
