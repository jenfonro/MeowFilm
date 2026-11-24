import Foundation
import CryptoKit

/// Prepares an immutable input in the runtime's existing source store. This
/// neither installs a script nor creates another source/configuration owner.
struct CatLocalSourceInput: Sendable {
    let runtimeRoot: URL

    func prepare(_ source: String) throws -> String {
        try Task.checkCancellation()
        guard !CatSourceSettings.trim(source).isEmpty else { throw FilmSourceError(code: "INVALID_SOURCE") }
        guard runtimeRoot.isFileURL, runtimeRoot.path.hasPrefix("/") else {
            throw FilmSourceError(code: "STORAGE_ERROR")
        }
        let bytes = Data(source.utf8)
        let hash = SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
        let root = runtimeRoot.resolvingSymlinksInPath().appendingPathComponent("sources", isDirectory: true)
        let destination = root.appendingPathComponent(hash + ".js")
        do {
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            if FileManager.default.fileExists(atPath: destination.path) {
                guard (try Data(contentsOf: destination)) == bytes else {
                    throw FilmSourceError(code: "CORRUPT_STORAGE")
                }
                return hash
            }
            try Task.checkCancellation()
            try bytes.write(to: destination, options: .atomic)
            return hash
        } catch is CancellationError { throw CancellationError() }
        catch let error as FilmSourceError { throw error }
        catch { throw FilmSourceError(code: "STORAGE_ERROR") }
    }
}
