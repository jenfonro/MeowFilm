#if os(iOS)
import Foundation
import MeowStorage

/// The picker URL is consumed under its grant and never passed to the runtime.
struct FilmScriptFileSnapshot: Sendable {
    let fileName: String
    let source: String

    static func read(_ url: URL) async throws -> Self {
        guard url.isFileURL, url.pathExtension.lowercased() == "js" else { throw FileError.scriptRequired }
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }
        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        guard values.isRegularFile == true, values.isSymbolicLink != true else { throw FileError.scriptRequired }
        // Share the same file-provider coordination, actual-byte resource bound,
        // cancellation and change detection as the service's fileURL entry.
        var data = try await ImportedFileReader.read(url, maximumBytes: ImportedFileReader.maximumSupportedBytes)
        defer { data.resetBytes(in: 0..<data.count) }
        guard let source = String(data: data, encoding: .utf8) else { throw FileError.invalidUTF8 }
        return Self(fileName: url.lastPathComponent, source: source)
    }
    enum FileError: Error, LocalizedError {
        case scriptRequired, invalidUTF8
        var errorDescription: String? {
            switch self {
            case .scriptRequired: return "请选择 .js 文件"
            case .invalidUTF8: return "文件不是有效的 UTF-8"
            }
        }
    }
}
#endif
