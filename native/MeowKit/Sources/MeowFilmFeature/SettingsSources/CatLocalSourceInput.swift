import Foundation
import CryptoKit
import Darwin
import MeowStorage

/// Prepares an immutable input in the runtime's existing source store. This
/// neither installs a script nor creates another source/configuration owner.
struct CatLocalSourceInput: Sendable {
    let runtimeRoot: URL

    func prepare(_ source: String) throws -> String {
        try Task.checkCancellation()
        var bytes = Data(source.utf8)
        defer { bytes.resetBytes(in: 0..<bytes.count) }
        guard !CatSourceSettings.trim(source).isEmpty else { throw FilmSourceError(code: "INVALID_SOURCE") }
        guard runtimeRoot.isFileURL, runtimeRoot.path.hasPrefix("/"),
              !runtimeRoot.path.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }) else {
            throw FilmSourceError(code: "STORAGE_ERROR")
        }
        let root = runtimeRoot.resolvingSymlinksInPath().appendingPathComponent("sources", isDirectory: true)
        do {
            var attributes: [FileAttributeKey: Any] = [.posixPermissions: 0o700]
            #if os(iOS)
            attributes[.protectionKey] = FileProtectionType.completeUntilFirstUserAuthentication
            #endif
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: attributes)
            let directory = Darwin.open(root.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard directory >= 0 else { throw FilmSourceError(code: "STORAGE_ERROR") }
            defer { Darwin.close(directory) }
            let hash = SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
            let name = hash + ".js"
            if try existing(name, directory: directory, matches: bytes) { return hash }
            let temporary = "." + UUID().uuidString.lowercased() + ".input"
            let fd = openat(directory, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
            guard fd >= 0 else { throw FilmSourceError(code: "STORAGE_ERROR") }
            defer { Darwin.close(fd); unlinkat(directory, temporary, 0) }
            #if os(iOS)
            let stage = root.appendingPathComponent(temporary)
            try FileManager.default.setAttributes([.protectionKey: FileProtectionType.completeUntilFirstUserAuthentication],
                ofItemAtPath: stage.path)
            let protection = try FileManager.default.attributesOfItem(atPath: stage.path)[.protectionKey]
            guard NativeFileProtectionVerification.inspect(protection) != .rejected else {
                throw FilmSourceError(code: "STORAGE_ERROR")
            }
            #endif
            try bytes.withUnsafeBytes { buffer in
                var offset = 0
                while offset < buffer.count {
                    try Task.checkCancellation()
                    let count = Darwin.write(fd, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
                    if count < 0 && errno == EINTR { continue }
                    guard count > 0 else { throw FilmSourceError(code: "STORAGE_ERROR") }
                    offset += count
                }
            }
            guard fsync(fd) == 0 else { throw FilmSourceError(code: "STORAGE_ERROR") }
            try Task.checkCancellation()
            // Exclusive publication never overwrites a runtime-owned source.
            if linkat(directory, temporary, directory, name, 0) != 0 {
                guard errno == EEXIST, try existing(name, directory: directory, matches: bytes) else {
                    throw FilmSourceError(code: "STORAGE_ERROR")
                }
            }
            guard fsync(directory) == 0 else { throw FilmSourceError(code: "STORAGE_ERROR") }
            return hash
        } catch is CancellationError { throw CancellationError() }
        catch let error as FilmSourceError { throw error }
        catch { throw FilmSourceError(code: "STORAGE_ERROR") }
    }

    private func existing(_ name: String, directory: Int32, matches bytes: Data) throws -> Bool {
        let fd = openat(directory, name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else {
            if errno == ENOENT { return false }
            throw FilmSourceError(code: "STORAGE_ERROR")
        }
        defer { Darwin.close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG),
              info.st_size == bytes.count else { throw FilmSourceError(code: "CORRUPT_STORAGE") }
        var actual = Data(count: bytes.count)
        defer { actual.resetBytes(in: 0..<actual.count) }
        try actual.withUnsafeMutableBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                try Task.checkCancellation()
                let count = Darwin.read(fd, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw FilmSourceError(code: "CORRUPT_STORAGE") }
                offset += count
            }
        }
        var tail: UInt8 = 0
        guard Darwin.read(fd, &tail, 1) == 0, actual == bytes else { throw FilmSourceError(code: "CORRUPT_STORAGE") }
        return true
    }
}
