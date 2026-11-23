#if os(iOS)
import Foundation
import MeowStorage

/// Transient picker bytes only. Saving hands them to the runtime-owned source
/// store; neither this temporary picker URL nor script text enters settings.
struct FilmScriptFileSnapshot: Sendable {
    let fileName: String
    let source: String

    static func read(_ url: URL) async throws -> Self {
        guard url.isFileURL, url.pathExtension.lowercased() == "js" else { throw FileError.scriptRequired }
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }
        let values = try url.resourceValues(forKeys: [.isRegularFileKey])
        guard values.isRegularFile == true else { throw FileError.scriptRequired }
        let data = try await ImportedFileReader.read(url)
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
