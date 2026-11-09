import Foundation

public enum FilmSearchProgram {
    public static func source() throws -> String {
        guard let url = Bundle.module.url(forResource: "film-search", withExtension: "js") else {
            throw CocoaError(.fileNoSuchFile)
        }
        return try String(contentsOf: url, encoding: .utf8)
    }
}
