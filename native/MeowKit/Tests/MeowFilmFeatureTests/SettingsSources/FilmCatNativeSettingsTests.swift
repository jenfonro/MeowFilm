import Foundation
import CryptoKit
import XCTest
import MeowRuntime
import MeowStorage
import MeowStorageTestSupport
@testable import MeowFilmFeature

final class FilmCatNativeSettingsTests: XCTestCase {
    private func fixture() throws -> (FilmSourceService, FilmSettingsDocument, SourceChangeRecorder, URL, SQLiteDatabase) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try storageTestDatabase(self, root: base)
        let changes = SourceChangeRecorder()
        let document = try sourceTestSettings(store, cats: [], changed: { changes.record($0) })
        changes.reset()
        let owner = FilmSourceService(transport: unavailableCatTransport("UNEXPECTED_RUNTIME"),
            database: store, settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        return (owner, document, changes, base, store)
    }

    func testOfflineImportEditEnableDeleteAndReopenUseOnlyNativeIntent() async throws {
        let (owner, document, changes, base, store) = try fixture()
        try await owner.initialize()
        let local = try await owner.importLocal(name: "  中文  ", source: "// 中文\nmodule.exports = {}", installationId: "local")
        XCTAssertEqual(local.name, "中文")
        XCTAssertEqual(local.inputRevision, 1)
        XCTAssertEqual(local.state, "stopped")
        XCTAssertEqual(local.generation, 0)
        XCTAssertTrue(local.sites.isEmpty)
        _ = try await owner.importNetwork(name: "Network", sourceURL: "https://example.invalid/source.js", installationId: "network")
        _ = try await owner.editCat("network", name: "Renamed")
        _ = try await owner.enableCat("local", enabled: false)
        try await owner.deleteCat("network")
        XCTAssertEqual(changes.values, Array(repeating: "cat.sources", count: 5))
        XCTAssertNil(try sourceTestRows(store), "A settings commit does not require a runtime/display cache write")
        await owner.close()
        let reopened = FilmSourceService(transport: unavailableCatTransport("UNEXPECTED_RUNTIME"),
            database: store, settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await reopened.initialize()
        let snapshot = await reopened.snapshot
        XCTAssertEqual(snapshot.cats.map(\.installationId), ["local"])
        XCTAssertFalse(try XCTUnwrap(snapshot.cats.first).enabled)
        XCTAssertEqual(snapshot.cats.first?.state, "disabled")
        XCTAssertNil(snapshot.runtimeError)
        XCTAssertEqual(snapshot.cats.first?.inputRevision, 1)
        XCTAssertTrue(snapshot.videos.isEmpty)
        XCTAssertEqual(changes.values.count, 5, "Read/close/reopen must not send a change")
        await reopened.close()
    }

    func testSourceEditRejectsUndeclaredIntentAndPreservesCommittedFields() async throws {
        let (owner, document, _, _, _) = try fixture()
        _ = try await owner.importNetwork(name: "Healthy", sourceURL: "https://example.invalid/a.js", installationId: "a")
        let before = try document.readForTest()
        await storageTestThrows({
            try await document.update { current in
                var root = try XCTUnwrap(current?.filmObject)
                root["extra"] = .string("undeclared")
                return .object(root)
            }
        }) { XCTAssertEqual($0 as? FilmSettingsStorageError, .invalidDocument) }
        XCTAssertEqual(try document.readForTest(), before)
        _ = try await owner.editCat("a", name: "Renamed")
        let saved = try XCTUnwrap(document.readForTest())
        XCTAssertNil(saved.catField("extra"))
        XCTAssertEqual(saved.catField("scripts")?.catArray?.first?.catField("name"), .string("Renamed"))
        await owner.close()
    }

    func testPanInterceptionDefaultsAndIndependentSavesPreserveSourcesMappingsAndOtherProviders() async throws {
        let (owner, document, changes, base, store) = try fixture()
        _ = try await owner.importNetwork(name: "A", sourceURL: "https://example.invalid/a.js", installationId: "a")
        let mapping = RuntimeValue.object(["provider": .string("quark"), "aliases": .array([.string("自定义线路")])])
        _ = try await document.update { original in
            var root = try XCTUnwrap(original?.filmObject)
            root["port"] = .number(1234)
            root["providers"] = .object(["quark": .bool(false), "external": .bool(false)])
            root["mappings"] = .array([mapping])
            return .object(root)
        }
        changes.reset()
        let before = try XCTUnwrap(document.readForTest())
        let initial = try await owner.readPanInterception()
        XCTAssertEqual(initial.count, 5)
        XCTAssertFalse(try XCTUnwrap(initial[.quark]))
        XCTAssertTrue(PanProviderID.allCases.filter { $0 != .quark }.allSatisfy { initial[$0] == true })
        XCTAssertEqual(try document.readForTest(), before, "Reading defaults does not persist them")
        XCTAssertTrue(changes.values.isEmpty)
        for provider in PanProviderID.allCases {
            let previous = try await owner.readPanInterception()
            let saved = try await owner.savePanInterception(provider, enabled: previous[provider] != true)
            XCTAssertEqual(saved[provider], previous[provider] != true)
            for sibling in PanProviderID.allCases where sibling != provider {
                XCTAssertEqual(saved[sibling], previous[sibling])
            }
            let persisted = try XCTUnwrap(document.readForTest())
            for field in ["scripts", "mappings", "port"] { XCTAssertEqual(persisted.catField(field), before.catField(field)) }
            XCTAssertEqual(persisted.catField("providers")?.catField("external"), .bool(false))
            XCTAssertEqual(persisted.catField("providers")?.catField(provider.rawValue), .bool(previous[provider] != true))
        }
        XCTAssertEqual(changes.values, Array(repeating: "cat.sources", count: 5))
        await owner.close()
        let reopened = FilmSourceService(transport: unavailableCatTransport("UNEXPECTED_RUNTIME"), database: store,
            settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        let restored = try await reopened.readPanInterception()
        XCTAssertTrue(try XCTUnwrap(restored[.quark]))
        XCTAssertTrue(PanProviderID.allCases.filter { $0 != .quark }.allSatisfy { restored[$0] == false })
        XCTAssertEqual(changes.values.count, 5)
        await reopened.close()
    }

    func testCurrentDisplayRelationsKeepProbeMetadataAndVideoChoicesAcrossIncrementalEdits() async throws {
        let database = try storageTestDatabase(self)
        let repository = FilmSourceRepository(database: database)
        let site = FilmSourceSite(key: "site", name: "Site", api: "/api", actions: ["search"], methods: [:])
        let cat = FilmCatSource(installationId: "a", name: "A", sourceType: "network", importedAt: 1,
            updatedAt: 2, sites: [site], inputRevision: 1)
        let video = FilmVideoSource(key: .init(installationId: "a", siteKey: "site"), name: "Site",
            api: "/api", actions: ["search"], methods: [:], options: .init(enabled: false), order: 4)
        _ = try await repository.save(.init(cats: [StoredFilmCat(cat)], videos: [video]), expected: 0)
        let (loaded, revision) = try await repository.load()
        XCTAssertEqual(loaded.cats, [StoredFilmCat(cat)])
        XCTAssertEqual(loaded.videos, [video])
        var changed = loaded; changed.videos[0].order = 10
        _ = try await repository.save(changed, expected: revision)
        let (saved, next) = try await repository.load()
        XCTAssertEqual(saved.cats, loaded.cats)
        XCTAssertEqual(saved.videos[0].order, 10)
        XCTAssertEqual(saved.videos[0].options, video.options)
        var refreshed = saved
        let newSite = FilmSourceSite(key: "site", name: "Renamed", api: "/api", actions: ["search"], methods: [:])
        refreshed.cats = [StoredFilmCat(FilmCatSource(installationId: "a", name: "A", sourceType: "network",
            importedAt: 1, updatedAt: 3, sites: [newSite], inputRevision: 1))]
        _ = try await repository.save(refreshed, expected: next)
        let (probed, _) = try await repository.load()
        XCTAssertEqual(probed.cats.first?.sites.first?.name, "Renamed")
        XCTAssertEqual(probed.videos, saved.videos)
    }

    func testSelectedVideoIntentRequiresUsableIdentityAndUnambiguousImageSelection() async throws {
        let database = try storageTestDatabase(self)
        let repository = FilmSourceRepository(database: database)
        let first = FilmVideoSource(key: .init(installationId: "a", siteKey: "one"), name: "One",
            api: "/api", actions: [], methods: [:], options: .init(aggregateImages: true))
        let second = FilmVideoSource(key: .init(installationId: "a", siteKey: "two"), name: "Two",
            api: "/api", actions: [], methods: [:], options: .init(aggregateImages: true))
        let invalid = FilmVideoSource(key: .init(installationId: "", siteKey: "broken"), name: "Invalid",
            api: "/api", actions: [], methods: [:])
        for videos in [[first, first], [first, second], [invalid]] {
            await storageTestThrows({ try await repository.save(.init(videos: videos), expected: 0) }) {
                XCTAssertEqual($0 as? FilmSourceError, .init(code: "INVALID_DOCUMENT"))
            }
            let (retained, revision) = try await repository.load()
            XCTAssertTrue(retained.videos.isEmpty); XCTAssertEqual(revision, 0)
        }
        _ = try await repository.save(.init(videos: [first]), expected: 0)
        let (saved, revision) = try await repository.load()
        XCTAssertEqual(saved.videos, [first]); XCTAssertEqual(revision, 1)
    }

    func testPickerCopyIsConsumedAsCompleteHashInputNotTemporaryPathOrSourceJSON() async throws {
        let (owner, document, _, base, store) = try fixture()
        let source = "// 中文 e\u{0301}\n" + String(repeating: "x", count: 65_537)
        let bytes = Data(source.utf8), hash = SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
        let picker = base.appendingPathComponent("picker-copy.js")
        try bytes.write(to: picker)
        _ = try await owner.importLocal(name: "Picker", fileURL: picker, installationId: "local")
        try FileManager.default.removeItem(at: picker)
        let saved = try XCTUnwrap(try document.readForTest())
        let input = try XCTUnwrap(saved.catField("scripts")?.catArray?.first)
        XCTAssertEqual(input.catField("localPath"), .null)
        XCTAssertEqual(input.catField("localHash"), .string(hash))
        let json = String(decoding: try RuntimeJSON.encode(saved), as: UTF8.self)
        XCTAssertFalse(json.contains(picker.path))
        XCTAssertFalse(json.contains(source))
        let runtimeFile = base.appendingPathComponent("cat/sources/\(hash).js")
        XCTAssertEqual(try Data(contentsOf: runtimeFile), bytes)
        let permissions = try FileManager.default.attributesOfItem(atPath: runtimeFile.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(permissions?.intValue, 0o600)
        let paths = try FileManager.default.contentsOfDirectory(atPath: base.appendingPathComponent("cat").path)
        XCTAssertEqual(paths, ["sources"], "Preparing input cannot write runtime settings or installation facts")
        await owner.close()
        let reopened = FilmSourceService(transport: unavailableCatTransport("UNEXPECTED_RUNTIME"),
            database: store,
            settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await reopened.initialize()
        let snapshot = await reopened.snapshot
        XCTAssertEqual(snapshot.cats.first?.sourceType, "local")
        XCTAssertEqual(snapshot.cats.first?.state, "stopped")
        XCTAssertEqual(try Data(contentsOf: runtimeFile), bytes)
        await reopened.close()
    }

    func testInputRevisionChangesOnlyForSourceNotRenameOrEnableAndSurvivesTypeSwitches() async throws {
        let (owner, document, changes, base, _) = try fixture()
        let first = try await owner.importLocal(name: "A", source: "// one", installationId: "a")
        let renamed = try await owner.editCat("a", name: "Renamed")
        let disabled = try await owner.enableCat("a", enabled: false)
        XCTAssertEqual(first.inputRevision, 1)
        XCTAssertEqual(renamed.inputRevision, 1)
        XCTAssertEqual(disabled.inputRevision, 1)
        let same = try await owner.updateCat("a", source: "// one")
        XCTAssertEqual(same.inputRevision, 1)
        XCTAssertEqual(changes.values.count, 3, "Identical input is not another commit")
        let local = try await owner.updateCat("a", source: "// two")
        XCTAssertEqual(local.inputRevision, 2)
        let network = try await owner.editCat("a", name: "Remote", sourceType: "network",
            sourceURL: "https://example.invalid/remote.js")
        XCTAssertEqual(network.inputRevision, 3)
        XCTAssertFalse(network.enabled, "Replacing input retains the enable intent")
        let wire = try XCTUnwrap(try document.readForTest()?.catField("scripts")?.catArray?.first)
        XCTAssertEqual(wire.catField("localHash"), .null)
        XCTAssertEqual(wire.catField("localPath"), .null)
        let restored = try await owner.editCat("a", name: "Local again", sourceType: "local", source: "// one")
        XCTAssertEqual(restored.inputRevision, 4)
        XCTAssertNil(restored.sourceUrl)
        XCTAssertFalse(restored.enabled)
        XCTAssertEqual(try document.readForTest()?.catField("scripts")?.catArray?.first?.catField("sourceUrl"), .null)
        await owner.close()
    }

    func testInvalidIntentDuplicateAndMissingLocalInputDoNotCommitOrNotify() async throws {
        let (owner, document, changes, base, _) = try fixture()
        _ = try await owner.importNetwork(name: "A", sourceURL: "https://example.invalid/a.js", installationId: "a")
        let before = try document.readForTest(), notifications = changes.values
        for operation in 0..<5 {
            do {
                switch operation {
                case 0: _ = try await owner.importNetwork(name: "Other", sourceURL: "https://example.invalid/b.js", installationId: "a")
                case 1: _ = try await owner.editCat("a", name: "A", sourceType: "local")
                case 2: _ = try await owner.editCat("a", name: "A", sourceType: "network", sourceURL: "file:///private/file.js")
                case 3: _ = try await owner.editCat("a", name: "A\nsecret")
                default: _ = try await owner.importLocal(name: "Invalid", source: "", installationId: "empty")
                }
                XCTFail("Invalid input cannot commit: \(operation)")
            } catch {}
            XCTAssertEqual(try document.readForTest(), before)
            XCTAssertEqual(changes.values, notifications)
        }
        await owner.close()
    }

    func testNativeTransactionRebaseAndValidationFailurePreserveOtherFields() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = try storageTestDatabase(self, root: base)
        let changes = SourceChangeRecorder()
        let document = try sourceTestSettings(store, cats: [], changed: { changes.record($0) })
        let settings = CatSourceSettings(document: document)
        let mapping = RuntimeValue.object(["provider": .string("quark"), "aliases": .array([.string("私有别名")])])
        _ = try await document.update { original in
            guard case .object(var root) = original else { throw FilmSourceError(code: "FIXTURE") }
            root["port"] = .number(1234); root["providers"] = .object(["quark": .bool(false)])
            root["mappings"] = .array([mapping])
            return .object(root)
        }
        changes.reset()
        let before = try XCTUnwrap(document.readForTest())
        let input = CatSourceConfiguration(installationId: "a", name: "A", sourceType: "network",
            sourceUrl: "https://example.invalid/a.js", localPath: nil, localHash: nil, enabled: true, inputRevision: 1)
        _ = try await settings.change { $0 + [input] }
        let after = try XCTUnwrap(document.readForTest())
        for field in ["port", "providers", "mappings"] { XCTAssertEqual(after.catField(field), before.catField(field)) }
        XCTAssertEqual(changes.values, ["cat.sources"])
        var invalid = input; invalid.inputRevision = 0
        let rejected = invalid
        do { _ = try await settings.change { _ in [rejected] }; XCTFail("Invalid revision admitted") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "INVALID_DOCUMENT") }
        XCTAssertEqual(try document.readForTest(), after)
        XCTAssertEqual(changes.values.count, 1)
    }

    func testCorruptExistingRuntimeInputIsNotOverwrittenAndDoesNotPublishIntent() async throws {
        let (owner, document, changes, base, _) = try fixture()
        let source = "// immutable", hash = SHA256.hash(data: Data(source.utf8)).map { String(format: "%02x", $0) }.joined()
        let root = base.appendingPathComponent("cat/sources")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let file = root.appendingPathComponent(hash + ".js"), corrupt = Data("not the selected source".utf8)
        try corrupt.write(to: file)
        let before = try document.readForTest()
        do { _ = try await owner.importLocal(name: "A", source: source, installationId: "a"); XCTFail("Corrupt runtime input") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "CORRUPT_STORAGE") }
        XCTAssertEqual(try document.readForTest(), before)
        XCTAssertEqual(try Data(contentsOf: file), corrupt)
        XCTAssertTrue(changes.values.isEmpty)
        let snapshot = await owner.snapshot
        XCTAssertTrue(snapshot.cats.isEmpty)
        await owner.close()
    }

    func testSettingsReadFailureIsExplicitAndNeverFallsBackToRuntime() async throws {
        let (owner, document, changes, base, store) = try fixture()
        _ = try await owner.importNetwork(name: "A", sourceURL: "https://example.invalid/a.js", installationId: "a")
        let before = await owner.snapshot
        _ = try await store.transaction { db in
            try db.execute("PRAGMA ignore_check_constraints=ON")
            defer { _ = try? db.execute("PRAGMA ignore_check_constraints=OFF") }
            return try db.execute("UPDATE ios_cat_settings SET port=-1 WHERE singleton=1")
        }
        changes.reset()
        do { _ = try await owner.enableCat("a", enabled: false); XCTFail("Invalid native configuration") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "INVALID_DOCUMENT") }
        let failed = await owner.snapshot
        XCTAssertEqual(failed, before)
        XCTAssertTrue(changes.values.isEmpty)
        XCTAssertNil(try sourceTestRows(store))
        XCTAssertEqual(try document.readForTest()?.catField("port"), .number(-1))
        await owner.close()
    }

    func testCurrentCatalogPreservesReadOnlyMetadataAndVideoChoicesAcrossOfflineSaveAndReopen() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try storageTestDatabase(self, root: base)
        let document = try sourceTestSettings(store)
        let runtime = SourcesRuntimeFixture()
        let initialOwner = FilmSourceService(transport: runtime.transport, database: store,
            settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await initialOwner.refresh()
        let visible = FilmSourceKey(installationId: "a", siteKey: "same")
        let deleted = FilmSourceKey(installationId: "b", siteKey: "same")
        let detection = FilmSourceDetection(result: "valid", checkedAt: 123, playbackResolved: true,
            steps: [.init("search", "success")])
        let video = FilmVideoSource(key: visible, name: "站点", api: "/api/a", actions: ["search"], methods: [:],
            options: .init(enabled: false, homeVisible: false, searchEnabled: true, aggregateImages: true),
            order: 9, detection: detection)
        try await initialOwner.deleteVideo(deleted)
        try await initialOwner.saveVideoOptions(visible, options: video.options, order: video.order)
        let repository = FilmSourceRepository(database: store)
        let (initialDocument, revision) = try await repository.load()
        _ = try await repository.save(.init(cats: initialDocument.cats, videos: [video], deleted: initialDocument.deleted),
                                      expected: revision)
        let rows = try sourceTestRows(store)
        await initialOwner.close()
        let owner = FilmSourceService(transport: unavailableCatTransport("UNEXPECTED_RUNTIME"), database: store,
            settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await owner.initialize()
        let initial = await owner.snapshot
        XCTAssertEqual(initial.cats.map(\.name), ["A", "B"])
        XCTAssertEqual(initial.cats.first?.sourceUrl, "https://example.invalid/a.js")
        XCTAssertEqual(initial.cats.first?.sites.count, 1)
        XCTAssertEqual(initial.videos, [video])
        XCTAssertEqual(try sourceTestRows(store), rows, "Reading does not write a new receipt")
        try await owner.saveVideoOptions(visible, options: video.options, order: 10)
        let (saved, _) = try await repository.load()
        XCTAssertEqual(saved.cats.count, 2)
        for cat in saved.cats {
            XCTAssertEqual(cat.sites.count, 1)
            XCTAssertEqual(cat.inputRevision, 1)
        }
        let columns = try await store.read { try $0.query("PRAGMA table_info(ios_film_source_probes)").map { try $0.text("name") } }
        for field in ["name", "sourceType", "sourceUrl", "enabled", "localPath", "localHash"] {
            XCTAssertFalse(columns.contains(field), "Editable input is not duplicated in probe facts")
        }
        XCTAssertEqual(saved.deleted, initialDocument.deleted)
        await owner.close()
        let reopened = FilmSourceService(transport: runtime.transport, database: store,
            settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await reopened.initialize()
        let restored = await reopened.snapshot
        XCTAssertEqual(restored.videos.first?.detection, detection)
        XCTAssertEqual(restored.videos.first?.options, video.options)
        XCTAssertEqual(restored.videos.first?.order, 10)
        XCTAssertEqual(restored.cats.map { $0.sites.count }, [1, 1])
        try await reopened.refresh()
        let synchronized = await reopened.snapshot
        XCTAssertEqual(synchronized.videos.map(\.key), [visible], "The saved deletion excludes the other parent's site")
        XCTAssertEqual(synchronized.videos.first?.detection, detection)
        XCTAssertEqual(synchronized.videos.first?.options, video.options)
        await reopened.close()
    }

    func testLateAndMissingStatusCannotDeleteIntentOrRestoreDeletedParentOrVideo() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try storageTestDatabase(self, root: base)
        let document = try sourceTestSettings(store)
        let runtime = SourcesRuntimeFixture()
        let owner = FilmSourceService(transport: runtime.transport, database: store,
            settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await owner.refresh()
        let deleted = FilmSourceKey(installationId: "a", siteKey: "same")
        try await owner.deleteVideo(deleted)
        await runtime.pause("status")
        let pending = Task { try await owner.refresh() }
        await runtime.waitUntilPaused()
        do {
            _ = try await owner.editCat("a", name: "Latest", sourceURL: "https://example.invalid/latest.js")
            try await owner.deleteCat("b")
        } catch { await runtime.release(); _ = try? await pending.value; await owner.close(); throw error }
        let intent = try document.readForTest()
        await runtime.release()
        try await pending.value
        let late = await owner.snapshot
        XCTAssertEqual(late.cats.map(\.installationId), ["a"])
        XCTAssertEqual(late.cats.first?.name, "Latest")
        XCTAssertEqual(late.cats.first?.inputRevision, 2)
        XCTAssertEqual(late.cats.first?.generation, 0)
        XCTAssertFalse(late.cats.first?.canDetect ?? true)
        XCTAssertTrue(late.videos.isEmpty)
        XCTAssertEqual(try document.readForTest(), intent)
        await runtime.returnNext("status", value: .object(["instances": .array([])]))
        try await owner.refresh()
        let missing = await owner.snapshot
        XCTAssertEqual(missing.cats.map(\.installationId), ["a"])
        XCTAssertEqual(missing.cats.first?.inputRevision, 2)
        XCTAssertTrue(missing.videos.isEmpty)
        try await runtime.acceptNativeSettings(document)
        try await owner.refresh()
        let current = await owner.snapshot
        XCTAssertEqual(current.cats.map(\.installationId), ["a"])
        XCTAssertTrue(try XCTUnwrap(current.cats.first).canDetect)
        XCTAssertTrue(current.videos.isEmpty)
        XCTAssertEqual(try document.readForTest(), intent)
        await owner.close()
        let reopened = FilmSourceService(transport: unavailableCatTransport("OFFLINE"), database: store,
            settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await reopened.initialize()
        let restored = await reopened.snapshot
        XCTAssertEqual(restored.cats.map(\.installationId), ["a"])
        XCTAssertTrue(restored.videos.isEmpty)
        await reopened.close()
    }
}

final class SourceChangeRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [String] = []
    var values: [String] { lock.lock(); defer { lock.unlock() }; return recorded }
    func record(_ value: String) { lock.lock(); defer { lock.unlock() }; recorded.append(value) }
    func reset() { lock.lock(); defer { lock.unlock() }; recorded.removeAll() }
}
