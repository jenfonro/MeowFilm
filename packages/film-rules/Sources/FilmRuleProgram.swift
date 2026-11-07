import Foundation

public enum FilmRuleProgram {
    public static func load() throws -> String {
        guard let url = Bundle.module.url(forResource: "film-rules", withExtension: "js") else {
            throw CocoaError(.fileNoSuchFile)
        }
        return try String(contentsOf: url, encoding: .utf8)
    }
}
