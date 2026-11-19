import Foundation
import XCTest
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature
@testable import Meowfilm

/// Isolated current database rows for component fixtures. Real App-hosted
/// settings tests use resources.filmSettings and the shared App database.
final class FilmRuntimeTestSettings {
    let database: SQLiteDatabase
    let documents: [FilmSettingsBlock: FilmSettingsDocument]
    init(database: SQLiteDatabase) {
        self.database = database
        documents = Dictionary(uniqueKeysWithValues: FilmSettingsBlock.allCases.map { block in
            (block, FilmSettingsDocument(block: block, database: database))
        })
    }
    subscript(_ block: FilmSettingsBlock) -> FilmSettingsDocument { documents[block]! }

    @MainActor func seedCats(_ cats: [FilmCatSource]) throws {
        let inputs: [RuntimeValue] = cats.map { cat in
            .object(["installationId": .string(cat.installationId), "name": .string(cat.name),
                     "sourceType": .string(cat.sourceType), "sourceUrl": cat.sourceUrl.map(RuntimeValue.string) ?? .null,
                     "localPath": .null, "localHash": cat.sourceType == "local" ? .string(String(repeating: "a", count: 64)) : .null,
                     "enabled": .bool(cat.enabled), "inputRevision": .number(Double(max(1, cat.inputRevision)))])
        }
        _ = try appStorageTestWait { try await self[.catSources].update { _ in .object([
            "version": .number(1), "port": .number(0), "providers": .object([:]),
            "mappings": .array([]), "scripts": .array(inputs)
        ]) } }
    }
}

enum NativeFilmFixture {
    @MainActor
    static func removeCat(_ id: String, resources: MeowfilmResources) async throws {
        try await resources.filmSourceSettings.initialize()
        let snapshot = await resources.filmSourceSettings.snapshot
        if snapshot.cats.contains(where: { $0.installationId == id }) {
            try await resources.filmSourceSettings.deleteCat(id)
        }
        let client = try await resources.scriptSession.connection()
        let deadline = Date().addingTimeInterval(15)
        repeat {
            let status = try await client.call(module: .cat, operation: "status").filmRequiredObject()
            if try !status.filmArray("instances").contains(where: {
                $0.filmObject?["installationId"] == .string(id)
            }) { return }
            try await Task.sleep(nanoseconds: 20_000_000)
        } while Date() < deadline
        XCTFail("The actual runtime did not retire the deleted Cat input")
        throw RuntimeHTTPError.timeout
    }

    @MainActor
    static func preferences(_ value: RuntimeValue, resources: MeowfilmResources) async throws {
        _ = try await resources.filmSettings[.preferences].update { current in
            var root = current?.filmObject ?? ["version": .number(1)]
            root["preferences"] = value
            return .object(root)
        }
        try await waitForNativeRuntimeSettings(.preferences, resources: resources)
    }

    @MainActor
    static func waitForNativeRuntimeSettings(_ block: FilmSettingsBlock, resources: MeowfilmResources) async throws {
        let client = try await resources.scriptSession.connection()
        guard case .object(let document)? = try await resources.filmSettings[block].read() else {
            throw FilmSettingsStorageError.invalidDocument
        }
        let expected: RuntimeValue, module: RuntimeModule, operation: String
        switch block {
        case .tmdb: expected = document["settings"]!; module = .tmdb; operation = "settings.get"
        case .douban: expected = document["settings"]!; module = .douban; operation = "settings.get"
        case .rules: expected = document["rules"]!; module = .film; operation = "rules.get"
        case .preferences: expected = document["preferences"]!; module = .film; operation = "settings.get"
        default: throw RuntimeHTTPError.invalidRequest
        }
        let deadline = Date().addingTimeInterval(10)
        repeat {
            do {
                let response = try await client.call(module: module, operation: operation)
                var actual = block == .rules ? response.filmObject?["rules"] : response
                if block == .tmdb || block == .douban {
                    guard case .object(var fields) = response,
                          let revision = fields["revision"]?.filmText,
                          revision.utf8.count == 64,
                          revision.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else {
                        throw FilmSearchError.invalidResponse
                    }
                    fields["revision"] = nil
                    actual = .object(fields)
                }
                if actual == expected { return }
            } catch RuntimeHTTPError.remote(let code) where code == "CANCELLED" {
                // Applying settings retires reads of the previous revision.
            }
            try await Task.sleep(nanoseconds: 20_000_000)
        } while Date() < deadline
        XCTFail("The actual runtime did not apply the committed settings block: \(block.rawValue)")
        throw RuntimeHTTPError.timeout
    }

    @MainActor
    static func waitForNativeCat(_ id: String, service: FilmSourceService) async throws -> FilmCatSource {
        let deadline = Date().addingTimeInterval(15)
        repeat {
            try await service.refresh()
            let snapshot = await service.snapshot
            if let cat = snapshot.cats.first(where: { $0.installationId == id }) {
                if cat.canDetect || !cat.enabled && cat.state == "disabled" { return cat }
                if cat.state == "failed" { throw FilmSourceError(code: cat.error ?? "IMPORT_FAILED") }
            }
            try await Task.sleep(nanoseconds: 20_000_000)
        } while Date() < deadline
        XCTFail("The actual runtime did not publish the saved Cat input")
        throw RuntimeHTTPError.timeout
    }
}

extension XCTestCase {
    @MainActor
    func waitForNativeRuntimeSettings(_ block: FilmSettingsBlock, resources: MeowfilmResources) async throws {
        try await NativeFilmFixture.waitForNativeRuntimeSettings(block, resources: resources)
    }
    @MainActor
    func waitForNativeCat(_ id: String, service: FilmSourceService) async throws -> FilmCatSource {
        try await NativeFilmFixture.waitForNativeCat(id, service: service)
    }
}
