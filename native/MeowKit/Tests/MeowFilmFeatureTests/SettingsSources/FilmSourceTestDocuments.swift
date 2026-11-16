import Foundation
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

/// Plain synthetic documents here exercise the domain. Protected persistence
/// and the trusted settings channel are tested at their own owner boundary.
func sourceTestSettings(_ base: URL,
                        cats: [RuntimeValue] = [SourcesRuntimeFixture.cat("a"), SourcesRuntimeFixture.cat("b")],
                        changed: @escaping @Sendable (String) -> Void = { _ in }) throws -> FilmSettingsDocument {
    let store = try DocumentStore(baseURL: base, namespace: FilmSettingsBlock.catSources.namespace)
    let document = FilmSettingsDocument(block: .catSources, storage: .plain({ store }), changed: changed)
    if try document.read() == nil {
        let inputs = cats.map { cat -> RuntimeValue in
            let local = cat.catField("sourceType") == .string("local")
            return .object([
                "installationId": cat.catField("installationId")!, "name": cat.catField("name")!,
                "sourceType": cat.catField("sourceType")!, "sourceUrl": cat.catField("sourceUrl") ?? .null,
                "localPath": .null, "localHash": local ? .string(String(repeating: "0", count: 64)) : .null,
                "enabled": cat.catField("enabled")!, "inputRevision": cat.catField("inputRevision") ?? .number(1)
            ])
        }
        _ = try document.update { current in
            current ?? .object(["version": .number(1), "port": .number(0), "providers": .object([:]),
                "mappings": .array([]), "scripts": .array(inputs)])
        }
    }
    return document
}
