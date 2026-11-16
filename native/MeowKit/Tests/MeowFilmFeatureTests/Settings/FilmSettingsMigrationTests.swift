import Foundation
import XCTest
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

final class FilmSettingsMigrationTests: XCTestCase {
    private struct Fixture {
        let root: URL
        let documents: [FilmSettingsBlock: FilmSettingsDocument]
        func write(_ path: String, _ value: RuntimeValue) throws {
            let file = root.appendingPathComponent(path)
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try RuntimeJSON.encode(value).write(to: file)
        }
        func read(_ path: String) throws -> RuntimeValue? {
            let file = root.appendingPathComponent(path)
            guard FileManager.default.fileExists(atPath: file.path) else { return nil }
            return try RuntimeJSON.decode(Data(contentsOf: file))
        }
        func migrate() throws { try FilmSettingsMigration.apply(runtimeRoot: root, documents: documents) }
    }

    private func fixture() throws -> Fixture {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString).resolvingSymlinksInPath()
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: base) }
        let documents = try Dictionary(uniqueKeysWithValues: FilmSettingsBlock.allCases.map { block in
            let store = try DocumentStore(baseURL: base, namespace: block.namespace)
            return (block, FilmSettingsDocument(block: block, storage: .plain { store }))
        })
        return .init(root: base, documents: documents)
    }

    private var credentials: RuntimeValue {
        .object(["version": .number(1), "accounts": .object([
            "quark": .object(["revision": .number(3), "credentials": .object(["cookie": .string("synthetic-cookie")])])
        ])])
    }

    func testAdoptsLegacySettingsOnceAndDoesNotOverwriteANativeEdit() throws {
        let f = try fixture()
        try f.write("pan/accounts.json", credentials)
        try f.migrate()
        XCTAssertEqual(try f.documents[.panAccounts]!.read(), credentials)
        XCTAssertNil(try f.read("pan/accounts.json"))
        let edited: RuntimeValue = .object(["version": .number(1), "accounts": .object([:])])
        _ = try f.documents[.panAccounts]!.update { _ in edited }
        try f.write("pan/accounts.json", credentials)
        try f.migrate()
        XCTAssertEqual(try f.documents[.panAccounts]!.read(), edited)
        XCTAssertEqual(try f.read("pan/accounts.json"), credentials, "different legacy input must not be destroyed")
    }

    func testSplitsPreferencesWithoutChangingRuntimeBusinessRecords() throws {
        let f = try fixture()
        let preferences: RuntimeValue = .object([
            "ruleOrder": .array([.string("quality"), .string("pan"), .string("keyword")]),
            "panOrder": .array([.string("quark")]), "keywords": .array([]),
            "siteCleanKeywords": .array([.string("synthetic")]), "iosAutoPrepare": .bool(false)
        ])
        let history: RuntimeValue = .array([.object(["untouched": .number(2)])])
        var fields: RuntimeObject = ["version": .number(1), "preferences": preferences, "history": history,
                                     "manual": .array([]), "blocks": .array([]), "previewTargets": .array([])]
        try f.write("film-playback/library.json", .object(fields))
        try f.migrate()
        XCTAssertEqual(try f.documents[.preferences]!.read(), .object(["version": .number(1), "preferences": preferences]))
        fields["preferences"] = nil
        XCTAssertEqual(try f.read("film-playback/library.json"), .object(fields))
        try f.migrate()
        XCTAssertEqual(try f.read("film-playback/library.json"), .object(fields))
    }

    func testMetadataAndRulesAdoptTheirExistingFormatsAndRetireOnlyKnownLegacyFiles() throws {
        let f = try fixture()
        let rules = try RuntimeJSON.decode(JSONEncoder().encode(FilmRuleSettings.initial))
        let tmdb: RuntimeValue = .object(["version": .number(1), "settings": FilmTMDBSettings.defaultValue])
        let douban: RuntimeValue = .object(["version": .number(1), "settings": FilmDoubanSettings.defaultValue])
        try f.write("tmdb/settings.json", tmdb)
        try f.write("douban/settings.json", douban)
        try f.write("film-rules/rules.json", .object(["rules": rules]))
        try f.write("tmdb/unrelated.json", .object(["kept": .bool(true)]))
        try f.migrate()
        XCTAssertEqual(try f.documents[.tmdb]!.read(), tmdb)
        XCTAssertEqual(try f.documents[.douban]!.read(), douban)
        XCTAssertEqual(try f.documents[.rules]!.read(), .object(["version": .number(1), "rules": rules]))
        for path in ["tmdb/settings.json", "douban/settings.json", "film-rules/rules.json"] {
            XCTAssertNil(try f.read(path))
        }
        XCTAssertEqual(try f.read("tmdb/unrelated.json"), .object(["kept": .bool(true)]))
        try f.migrate()
        XCTAssertEqual(try f.documents[.tmdb]!.read(), tmdb)
    }

    func testCatMigrationSeparatesIntentAndFactsAndResumesAfterANativeEdit() throws {
        let f = try fixture(), hash = String(repeating: "a", count: 64)
        let script: RuntimeValue = .object([
            "installationId": .string("cat-test"), "sourceHash": .string(hash), "name": .string("Synthetic"),
            "sourceType": .string("local"), "sourceUrl": .null, "enabled": .bool(false),
            "version": .string("1.0.0"), "config": .object([:]), "sites": .array([])
        ])
        try f.write("cat/settings.json", .object([
            "version": .number(1), "port": .number(0), "providers": .object([:]),
            "mappings": .array([]), "scripts": .array([script])
        ]))
        // Simulate adoption committed before legacy retirement, followed by an edit.
        let edited: RuntimeValue = .object(["version": .number(1), "port": .number(0),
            "providers": .object([:]), "mappings": .array([]), "scripts": .array([])])
        _ = try f.documents[.catSources]!.update { _ in edited }
        try f.migrate()
        XCTAssertEqual(try f.documents[.catSources]!.read(), edited)
        guard case .object(let facts)? = try f.read("cat/settings.json"),
              case .array(let scripts)? = facts["scripts"],
              case .object(let fact)? = scripts.first else { return XCTFail("facts missing") }
        XCTAssertEqual(facts["version"], .number(2))
        XCTAssertEqual(fact["loadedIdentity"], .string("local:1:" + hash))
        XCTAssertNil(fact["name"]); XCTAssertNil(fact["enabled"]); XCTAssertNil(fact["sourceType"])
        try f.migrate()
        XCTAssertEqual(try f.documents[.catSources]!.read(), edited)
    }

    func testCorruptLegacyAndSymlinkNeverBecomeEmptyNativeSettings() throws {
        let f = try fixture()
        try f.write("pan/accounts.json", .object(["version": .number(1), "accounts": .array([])]))
        XCTAssertThrowsError(try f.migrate())
        XCTAssertNil(try f.documents[.panAccounts]!.read())
        XCTAssertNotNil(try f.read("pan/accounts.json"))
        try FileManager.default.removeItem(at: f.root.appendingPathComponent("pan"))
        let outside = f.root.appendingPathComponent("outside")
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: f.root.appendingPathComponent("pan"), withDestinationURL: outside)
        XCTAssertThrowsError(try f.migrate())
        XCTAssertNil(try f.documents[.panAccounts]!.read())
    }
}
