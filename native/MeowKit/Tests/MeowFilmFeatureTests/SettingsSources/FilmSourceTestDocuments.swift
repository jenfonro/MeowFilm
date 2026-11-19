import Foundation
import MeowRuntime
import MeowStorage
import MeowStorageTestSupport
@testable import MeowFilmFeature

/// Synthetic source intent is projected by the actual current relation owner.
func sourceTestSettings(_ database: SQLiteDatabase,
                        cats: [RuntimeValue] = [SourcesRuntimeFixture.cat("a"), SourcesRuntimeFixture.cat("b")],
                        changed: @escaping @Sendable (String) -> Void = { _ in }) throws -> FilmSettingsDocument {
    let document = FilmSettingsDocument(block: .catSources, database: database, changed: changed)
    if try document.readForTest() == nil {
        let inputs = cats.map { cat -> RuntimeValue in
            let local = cat.catField("sourceType") == .string("local")
            return .object([
                "installationId": cat.catField("installationId")!, "name": cat.catField("name")!,
                "sourceType": cat.catField("sourceType")!, "sourceUrl": cat.catField("sourceUrl") ?? .null,
                "localPath": .null, "localHash": local ? .string(String(repeating: "0", count: 64)) : .null,
                "enabled": cat.catField("enabled")!, "inputRevision": cat.catField("inputRevision")!
            ])
        }
        _ = try storageTestAsync { try await document.update { current in
            current ?? .object(["version": .number(1), "port": .number(0), "providers": .object([:]),
                "mappings": .array([]), "scripts": .array(inputs)])
        } }
    }
    return document
}

func sourceTestRows(_ database: SQLiteDatabase) throws -> [String: [SQLiteRow]]? {
    try storageTestIO {
        try database.readSync { db in
            guard try db.query("SELECT revision FROM ios_film_source_state WHERE singleton=1")
                .first!.integer("revision") > 0 else { return nil }
            let tables = ["ios_film_source_state", "ios_film_source_probes", "ios_film_source_sites",
                "ios_film_source_actions", "ios_film_source_methods", "ios_film_video_intents",
                "ios_film_video_actions", "ios_film_video_methods", "ios_film_video_detections",
                "ios_film_video_probe_steps", "ios_film_source_deletions"]
            return try Dictionary(uniqueKeysWithValues: tables.map { ($0, try db.query("SELECT * FROM \($0) ORDER BY rowid")) })
        }
    }
}
