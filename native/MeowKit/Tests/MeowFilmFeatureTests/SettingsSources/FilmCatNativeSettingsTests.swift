import Foundation
import CryptoKit
import XCTest
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

final class FilmCatNativeSettingsTests: XCTestCase {
    private func fixture() throws -> (FilmSourceService, FilmSettingsDocument, SourceChangeRecorder, URL, DocumentStore) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        let changes = SourceChangeRecorder()
        let document = try sourceTestSettings(base, cats: [], changed: { changes.record($0) })
        changes.reset()
        let owner = FilmSourceService(transport: unavailableCatTransport("UNEXPECTED_RUNTIME"),
            storeFactory: { store }, settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        return (owner, document, changes, base, store)
    }

    func testOfflineImportEditEnableDeleteAndReopenUseOnlyNativeIntent() async throws {
        let (owner, document, changes, base, store) = try fixture()
        defer { try? FileManager.default.removeItem(at: base) }
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
        XCTAssertNil(try store.read(key: FilmSourceService.documentKey), "A settings commit does not require a runtime/display cache write")
        await owner.close()
        let reopened = FilmSourceService(transport: unavailableCatTransport("UNEXPECTED_RUNTIME"),
            storeFactory: { store }, settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
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

    func testPickerCopyIsConsumedAsCompleteHashInputNotTemporaryPathOrSourceJSON() async throws {
        let (owner, document, _, base, _) = try fixture()
        defer { try? FileManager.default.removeItem(at: base) }
        let source = "// 中文 e\u{0301}\n" + String(repeating: "x", count: 65_537)
        let bytes = Data(source.utf8), hash = SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
        let picker = base.appendingPathComponent("picker-copy.js")
        try bytes.write(to: picker)
        _ = try await owner.importLocal(name: "Picker", fileURL: picker, installationId: "local")
        try FileManager.default.removeItem(at: picker)
        let saved = try XCTUnwrap(try document.read())
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
            storeFactory: { try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace) },
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
        defer { try? FileManager.default.removeItem(at: base) }
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
        let wire = try XCTUnwrap(try document.read()?.catField("scripts")?.catArray?.first)
        XCTAssertEqual(wire.catField("localHash"), .null)
        XCTAssertEqual(wire.catField("localPath"), .null)
        let restored = try await owner.editCat("a", name: "Local again", sourceType: "local", source: "// one")
        XCTAssertEqual(restored.inputRevision, 4)
        XCTAssertNil(restored.sourceUrl)
        XCTAssertFalse(restored.enabled)
        XCTAssertEqual(try document.read()?.catField("scripts")?.catArray?.first?.catField("sourceUrl"), .null)
        await owner.close()
    }

    func testInvalidIntentDuplicateAndMissingLocalInputDoNotCommitOrNotify() async throws {
        let (owner, document, changes, base, _) = try fixture()
        defer { try? FileManager.default.removeItem(at: base) }
        _ = try await owner.importNetwork(name: "A", sourceURL: "https://example.invalid/a.js", installationId: "a")
        let before = try document.read(), notifications = changes.values
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
            XCTAssertEqual(try document.read(), before)
            XCTAssertEqual(changes.values, notifications)
        }
        await owner.close()
    }

    func testTrueCASConflictAndValidationFailureDoNotNotifyOrOverwriteOtherFields() throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: base) }
        let changes = SourceChangeRecorder()
        let document = try sourceTestSettings(base, cats: [], changed: { changes.record($0) })
        let settings = CatSourceSettings(document: document)
        let store = try DocumentStore(baseURL: base, namespace: FilmSettingsBlock.catSources.namespace)
        let mapping = RuntimeValue.object(["provider": .string("quark"), "aliases": .array([.string("私有别名")])])
        _ = try document.update { original in
            guard case .object(var root) = original else { throw FilmSourceError(code: "FIXTURE") }
            root["port"] = .number(1234); root["providers"] = .object(["quark": .bool(false)])
            root["mappings"] = .array([mapping])
            return .object(root)
        }
        changes.reset()
        let before = try XCTUnwrap(document.read())
        let input = CatSourceConfiguration(installationId: "a", name: "A", sourceType: "network",
            sourceUrl: "https://example.invalid/a.js", localPath: nil, localHash: nil, enabled: true, inputRevision: 1)
        var external = try RuntimeJSON.encode(before); external.append(0x20)
        XCTAssertThrowsError(try settings.change { _ in
            try store.write(external, key: FilmSettingsDocument.key)
            return [input]
        }) { XCTAssertEqual($0 as? FilmSettingsStorageError, .conflict) }
        XCTAssertEqual(try store.read(key: FilmSettingsDocument.key), external)
        XCTAssertTrue(changes.values.isEmpty)
        _ = try settings.change { $0 + [input] }
        let after = try XCTUnwrap(document.read())
        for field in ["port", "providers", "mappings"] { XCTAssertEqual(after.catField(field), before.catField(field)) }
        XCTAssertEqual(changes.values, ["cat.sources"])
        var invalid = input; invalid.inputRevision = 0
        XCTAssertThrowsError(try settings.change { _ in [invalid] })
        XCTAssertEqual(try document.read(), after)
        XCTAssertEqual(changes.values.count, 1)
    }

    func testCorruptExistingRuntimeInputIsNotOverwrittenAndDoesNotPublishIntent() async throws {
        let (owner, document, changes, base, _) = try fixture()
        defer { try? FileManager.default.removeItem(at: base) }
        let source = "// immutable", hash = SHA256.hash(data: Data(source.utf8)).map { String(format: "%02x", $0) }.joined()
        let root = base.appendingPathComponent("cat/sources")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let file = root.appendingPathComponent(hash + ".js"), corrupt = Data("not the selected source".utf8)
        try corrupt.write(to: file)
        let before = try document.read()
        do { _ = try await owner.importLocal(name: "A", source: source, installationId: "a"); XCTFail("Corrupt runtime input") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "CORRUPT_STORAGE") }
        XCTAssertEqual(try document.read(), before)
        XCTAssertEqual(try Data(contentsOf: file), corrupt)
        XCTAssertTrue(changes.values.isEmpty)
        let snapshot = await owner.snapshot
        XCTAssertTrue(snapshot.cats.isEmpty)
        await owner.close()
    }

    func testSettingsReadFailureIsExplicitAndNeverFallsBackToRuntime() async throws {
        let (owner, document, changes, base, store) = try fixture()
        defer { try? FileManager.default.removeItem(at: base) }
        _ = try await owner.importNetwork(name: "A", sourceURL: "https://example.invalid/a.js", installationId: "a")
        let before = await owner.snapshot
        let settingsStore = try DocumentStore(baseURL: base, namespace: FilmSettingsBlock.catSources.namespace)
        try settingsStore.write(Data("{}".utf8), key: FilmSettingsDocument.key)
        changes.reset()
        do { _ = try await owner.enableCat("a", enabled: false); XCTFail("Invalid native configuration") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "INVALID_DOCUMENT") }
        let failed = await owner.snapshot
        XCTAssertEqual(failed, before)
        XCTAssertTrue(changes.values.isEmpty)
        XCTAssertNil(try store.read(key: FilmSourceService.documentKey))
        XCTAssertEqual(try document.read(), .object([:]))
        await owner.close()
    }

    func testLegacyCatalogProjectsNativeIntentAndFirstSuccessfulCommitRemovesCleartextWithoutLosingMetadata() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: base) }
        let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        let legacyCats = [SourcesRuntimeFixture.cat("a"), SourcesRuntimeFixture.cat("b")]
        let document = try sourceTestSettings(base, cats: legacyCats)
        let visible = FilmSourceKey(installationId: "a", siteKey: "same")
        let deleted = FilmSourceKey(installationId: "b", siteKey: "same")
        let detection = FilmSourceDetection(result: "valid", checkedAt: 123, playbackResolved: true,
            steps: [.init("search", "success")])
        let video = FilmVideoSource(key: visible, name: "站点", api: "/api/a", actions: ["search"], methods: [:],
            options: .init(enabled: false, homeVisible: false, searchEnabled: true, aggregateImages: true),
            order: 9, detection: detection)
        let cached = legacyCats.map { cat -> RuntimeValue in
            guard case .object(var fields) = cat else { preconditionFailure("Fixture") }
            for field in ["inputRevision", "generation", "state", "error"] { fields[field] = nil }
            fields["sourceUrl"] = .string("https://example.invalid/old.js?token=synthetic-legacy")
            fields["name"] = .string("Old display name")
            return .object(fields)
        }
        let legacy = RuntimeValue.object(["version": .number(1), "cats": .array(cached),
            "videos": .array([try RuntimeJSON.decode(JSONEncoder().encode(video))]),
            "deleted": .array([try RuntimeJSON.decode(JSONEncoder().encode(deleted))])])
        let bytes = try RuntimeJSON.encode(legacy)
        try store.write(bytes, key: FilmSourceService.documentKey)
        let owner = FilmSourceService(transport: unavailableCatTransport("UNEXPECTED_RUNTIME"), storeFactory: { store },
            settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await owner.initialize()
        let initial = await owner.snapshot
        XCTAssertEqual(initial.cats.map(\.name), ["A", "B"], "Editable fields always come from native intent, never a legacy cache")
        XCTAssertEqual(initial.cats.first?.sourceUrl, "https://example.invalid/a.js")
        XCTAssertEqual(initial.cats.first?.sites.count, 1)
        XCTAssertEqual(initial.videos, [video])
        XCTAssertEqual(try store.read(key: FilmSourceService.documentKey), bytes, "Read projection does not invent a migration receipt")
        try await owner.saveVideoOptions(visible, options: video.options, order: 10)
        let saved = try RuntimeJSON.decode(XCTUnwrap(store.read(key: FilmSourceService.documentKey)))
        XCTAssertEqual(saved.catField("version"), .number(2))
        let cats = try XCTUnwrap(saved.catField("cats")?.catArray)
        XCTAssertEqual(cats.count, 2)
        for cat in cats {
            for field in ["name", "sourceType", "sourceUrl", "enabled", "localPath", "localHash"] {
                XCTAssertNil(cat.catField(field), "No editable or sensitive input in the display cache")
            }
            XCTAssertEqual(cat.catField("sites")?.catArray?.count, 1)
        }
        XCTAssertEqual(saved.catField("deleted"), legacy.catField("deleted"))
        XCTAssertFalse(String(decoding: try RuntimeJSON.encode(saved), as: UTF8.self).contains("synthetic-legacy"))
        await owner.close()
        let runtime = SourcesRuntimeFixture()
        let reopened = FilmSourceService(transport: runtime.transport, storeFactory: { store },
            settingsDocument: document, runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await reopened.initialize()
        let restored = await reopened.snapshot
        XCTAssertEqual(restored.videos.first?.detection, detection)
        XCTAssertEqual(restored.videos.first?.options, video.options)
        XCTAssertEqual(restored.videos.first?.order, 10)
        XCTAssertEqual(restored.cats.map { $0.sites.count }, [1, 1])
        try await reopened.refresh()
        let synchronized = await reopened.snapshot
        XCTAssertEqual(synchronized.videos.map(\.key), [visible], "A legacy tombstone still excludes the other parent's site")
        XCTAssertEqual(synchronized.videos.first?.detection, detection)
        XCTAssertEqual(synchronized.videos.first?.options, video.options)
        await reopened.close()
    }

    func testLateAndMissingStatusCannotDeleteIntentOrRestoreDeletedParentOrVideo() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: base) }
        let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        let document = try sourceTestSettings(base)
        let runtime = SourcesRuntimeFixture()
        let owner = FilmSourceService(transport: runtime.transport, storeFactory: { store },
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
        let intent = try document.read()
        await runtime.release()
        try await pending.value
        let late = await owner.snapshot
        XCTAssertEqual(late.cats.map(\.installationId), ["a"])
        XCTAssertEqual(late.cats.first?.name, "Latest")
        XCTAssertEqual(late.cats.first?.inputRevision, 2)
        XCTAssertEqual(late.cats.first?.generation, 0)
        XCTAssertFalse(late.cats.first?.canDetect ?? true)
        XCTAssertTrue(late.videos.isEmpty)
        XCTAssertEqual(try document.read(), intent)
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
        XCTAssertEqual(try document.read(), intent)
        await owner.close()
        let reopened = FilmSourceService(transport: unavailableCatTransport("OFFLINE"), storeFactory: { store },
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
