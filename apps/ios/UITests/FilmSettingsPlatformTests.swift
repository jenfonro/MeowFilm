#if os(iOS)
import XCTest
import UIKit
import Combine
import MeowJSON
import MeowStorage
@testable import MeowFilmFeature
@testable import MeowUI

/// Exercises actual local owners and UIKit. The RPC boundary is a fixture, not Node evidence.
@MainActor
final class FilmSettingsPlatformTests: XCTestCase {
    func testDraftSurvivesNavigationAndWholeDraftSaveRestoreAndTestNeverSaves() async throws {
        let fixture = try FilmSettingsFixture()
        defer { fixture.removeFiles() }
        let rules = fixture.rules
        try await rules.initialize()
        let owner = FilmRulesDraft(service: rules)
        try await owner.load()
        let initial = try XCTUnwrap(owner.draft)
        XCTAssertFalse(initial.episodeCleanRegexRules.isEmpty)
        XCTAssertFalse(initial.movieRules.isEmpty)
        let editor = FilmRuleEditorController(owner: owner, group: .episodeClean, index: 0, themeProvider: FilmSettingsTheme())
        let host = host(editor)
        defer { unhost(host) }
        editor.pattern.text = "^草稿"
        editor.textViewDidChange(editor.pattern)
        let section = FilmRuleSectionController(owner: owner, section: .movie, themeProvider: FilmSettingsTheme())
        section.loadViewIfNeeded()
        let reopened = FilmRuleEditorController(owner: owner, group: .episodeClean, index: 0, themeProvider: FilmSettingsTheme())
        XCTAssertEqual(reopened.pattern.text, "^草稿")
        owner.edit(.movie, index: 0, rule: .init(pattern: "(电影)", replace: "$1", flags: "i"))
        try await owner.save()
        let saved = try await rules.savedSnapshot()
        XCTAssertEqual(saved.episodeCleanRegexRules.first, "^草稿")
        XCTAssertEqual(saved.movieRules.first?.flags, "i")
        owner.edit(.episodeClean, index: 0, rule: .init(pattern: "^保留"))
        try await owner.restore(.movie)
        let restored = try await rules.savedSnapshot()
        XCTAssertEqual(restored.episodeCleanRegexRules.first, "^保留")
        XCTAssertNotEqual(restored.movieRules.first?.pattern, "(电影)")
        owner.edit(.movie, index: 0, rule: .init(pattern: "["))
        let before = try fixture.rulesStore.read(key: FilmRuleService.documentKey)
        let test = FilmRuleTestController(owner: owner, section: .movie, themeProvider: FilmSettingsTheme())
        test.loadViewIfNeeded(); test.input.text = "电影 2026"; test.runTest()
        await eventually { !test.testing && test.result != nil }
        XCTAssertFalse(try XCTUnwrap(test.result).errors.isEmpty)
        XCTAssertEqual(try fixture.rulesStore.read(key: FilmRuleService.documentKey), before)
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "[")
        await rules.close(); await fixture.sources.close()
    }

    func testRulesSaveFailureRetainsDraftAndRetryCommitsWithoutLosingOtherGroups() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.rules.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let bytes = try XCTUnwrap(fixture.rulesStore.read(key: FilmRuleService.documentKey))
        owner.edit(.episodeClean, index: 0, rule: .init(pattern: "^修改"))
        try fixture.rulesStore.write(Data("{}".utf8), key: FilmRuleService.documentKey)
        do { try await owner.save(); XCTFail("CAS conflict must remain a failure") } catch {}
        XCTAssertEqual(owner.rules(.episodeClean).first?.pattern, "^修改")
        let saved = try await fixture.rules.savedSnapshot()
        XCTAssertNotEqual(saved.episodeCleanRegexRules.first, "^修改")
        try fixture.rulesStore.write(bytes, key: FilmRuleService.documentKey)
        try await owner.save()
        let committed = try await fixture.rules.savedSnapshot()
        XCTAssertEqual(committed.episodeCleanRegexRules.first, "^修改")
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testModelStartsIndependentLocalOwnersAndCloseDoesNotCloseServices() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        let model = FilmSettingsModel(rules: fixture.rules, sources: fixture.sources)
        model.start()
        await eventually { model.rules.draft != nil && model.snapshot != nil }
        let calls = await fixture.runtime.calls
        XCTAssertTrue(calls.isEmpty, "Model cannot launch Node or run detection")
        model.rules.edit(.episodeClean, index: 0, rule: .init(pattern: "长期草稿"))
        model.close(); model.start()
        await eventually { model.snapshot != nil }
        XCTAssertEqual(model.rules.rules(.episodeClean).first?.pattern, "长期草稿")
        model.close()
        _ = try await fixture.rules.savedSnapshot()
        try await fixture.sources.initialize()
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testFilePickerSnapshotRejectsMalformedUTF8AndKeepsCopiedContent() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: base) }
        let file = base.appendingPathComponent("猫源.js")
        let text = "// 猫源\nconst name = '影视';"
        try Data(text.utf8).write(to: file)
        let snapshot = try await FilmScriptFileSnapshot.read(file)
        try Data("changed".utf8).write(to: file)
        XCTAssertEqual(snapshot.source, text)
        XCTAssertEqual(snapshot.fileName, "猫源.js")
        try Data([0xff, 0xfe, 0x61]).write(to: file)
        let link = base.appendingPathComponent("link.js")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: file)
        for invalid in [file, base.appendingPathComponent("script.txt"), link] {
            do { _ = try await FilmScriptFileSnapshot.read(invalid); XCTFail("Invalid file must be rejected") }
            catch {}
        }
        let oversized = base.appendingPathComponent("large.js")
        FileManager.default.createFile(atPath: oversized.path, contents: nil)
        let handle = try FileHandle(forWritingTo: oversized)
        try handle.truncate(atOffset: UInt64(ImportedFileReader.maximumSupportedBytes + 1))
        try handle.close()
        do { _ = try await FilmScriptFileSnapshot.read(oversized); XCTFail("File read resource boundary") }
        catch { XCTAssertEqual(error as? ImportedFileError, .tooLarge) }
    }

    func testVideoOptionsAreDurableExclusiveOrderedAndFailedSaveDoesNotPublish() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.sources.refresh()
        let first = FilmSourceKey(installationId: "a", siteKey: "site")
        let second = FilmSourceKey(installationId: "b", siteKey: "site")
        let page = FilmVideoDetailController(service: fixture.sources, key: first, themeProvider: FilmSettingsTheme())
        let window = host(page); defer { unhost(window) }
        await eventually { page.snapshot?.videos.count == 2 }
        page.saveOptions(.init(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true), order: -2)
        await eventually { !page.busy && page.snapshot?.videos.first?.order == -2 }
        let options = try XCTUnwrap(page.snapshot?.videos.first).options
        XCTAssertEqual(options, .init(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true))
        try await fixture.sources.saveVideoOptions(second, options: .init(aggregateImages: true), order: -3)
        await eventually { page.snapshot?.videos.first?.key == second }
        XCTAssertEqual(page.snapshot?.videos.first(where: { $0.key == first })?.options.aggregateImages, false)
        let bytes = try XCTUnwrap(fixture.sourceStore.read(key: FilmSourceService.documentKey))
        try fixture.sourceStore.write(Data("{}".utf8), key: FilmSourceService.documentKey)
        let before = await fixture.sources.snapshot
        page.saveOptions(.init())
        await eventually { !page.busy }
        XCTAssertEqual(page.snapshot, before)
        await eventually { page.presentedViewController is UIAlertController }
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.actions.map(\.title), ["取消", "重试"])
        await eventually { !alert.isBeingPresented && alert.view.window != nil }
        try fixture.sourceStore.write(bytes, key: FilmSourceService.documentKey)
        let rebuilt = FilmSourceService(transport: .init(rpc: { _, _, _ in throw FilmSourceError(code: "OFFLINE") }),
                                       storeFactory: { fixture.sourceStore })
        try await rebuilt.initialize()
        let snapshot = await rebuilt.snapshot
        XCTAssertEqual(snapshot.videos, before.videos)
        await rebuilt.close(); await fixture.sources.close(); await fixture.rules.close()
    }

    func testVideoDetectionCancellationOnHiddenPageDoesNotPublishLateResult() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.sources.refresh()
        await fixture.runtime.pauseProbes()
        let key = FilmSourceKey(installationId: "a", siteKey: "site")
        let page = FilmVideoDetailController(service: fixture.sources, key: key, themeProvider: FilmSettingsTheme())
        let window = host(page); defer { unhost(window) }
        await eventually { page.snapshot?.videos.count == 2 }
        page.detect()
        await fixture.runtime.waitForProbe()
        XCTAssertTrue(page.detecting)
        attach(page, "film-video-detecting")
        page.pageDidBecomeHidden()
        XCTAssertFalse(page.detecting)
        await fixture.runtime.releaseProbe()
        let snapshot = await fixture.sources.snapshot
        XCTAssertNil(snapshot.videos.first { $0.key == key }?.detection)
        XCTAssertTrue(snapshot.videos.allSatisfy { $0.options == FilmVideoOptions() })
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testCatActionsSelectionConfirmationAndDisabledParent() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.sources.refresh()
        let cats = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
        let window = host(cats); defer { unhost(window) }
        await eventually { cats.snapshot?.cats.count == 2 }
        try row(cats, "film.cats.select").action?()
        try row(cats, "film.cats.selectAll").action?()
        XCTAssertTrue(try row(cats, "film.cats.batch.4").enabled)
        try row(cats, "film.cats.batch.4").action?()
        let alert = try XCTUnwrap(cats.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.actions.map(\.style), [.cancel, .destructive])
        let calls = await fixture.runtime.calls
        XCTAssertFalse(calls.contains("uninstall"), "Showing confirmation is not deletion consent")
        await eventually { !alert.isBeingPresented && alert.view.window != nil }
        let detail = FilmCatDetailController(service: fixture.sources, installationId: "a", themeProvider: FilmSettingsTheme())
        detail.loadViewIfNeeded(); detail.beginAppearanceTransition(true, animated: false); detail.endAppearanceTransition()
        await eventually { detail.snapshot?.cats.count == 2 }
        for action in ["edit", "update", "restart", "delete", "enabled"] {
            XCTAssertNotNil(try row(detail, "film.cat.\(action)").action)
        }
        _ = try await fixture.sources.enableCat("a", enabled: false)
        await eventually { detail.snapshot?.cats.first?.enabled == false }
        XCTAssertFalse(try row(detail, "film.cat.restart").enabled)
        let disabled = await fixture.sources.snapshot
        XCTAssertEqual(disabled.videos.count, 2)
        PageViewController.invalidatePages(in: detail)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testNarrowWideLargeTypeAndEmptyScreensProduceRealUIKitAttachments() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.rules.initialize(); try await fixture.sources.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        for width: CGFloat in [320, 768] {
            let pages: [FilmSettingsListController] = [
                FilmRulesViewController(owner: owner, themeProvider: FilmSettingsTheme()),
                FilmRuleSectionController(owner: owner, section: .episode, themeProvider: FilmSettingsTheme()),
                FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme()),
                FilmVideoSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme()),
            ]
            for (index, page) in pages.enumerated() {
                page.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
                let window = host(page, size: CGSize(width: width, height: 568))
                if let source = page as? FilmSourcePageController {
                    await eventually { source.snapshot != nil }
                }
                await settle()
                page.view.layoutIfNeeded()
                XCTAssertGreaterThan(page.table.bounds.width, 0)
                XCTAssertLessThanOrEqual(page.table.frame.maxX, page.view.bounds.maxX)
                XCTAssertGreaterThanOrEqual(page.table.frame.minY, page.pageHeader.frame.maxY)
                XCTAssertEqual(page.table.frame.maxY, page.view.safeAreaLayoutGuide.layoutFrame.maxY, accuracy: 1)
                for cell in page.table.visibleCells {
                    XCTAssertGreaterThanOrEqual(cell.bounds.height, 44)
                    XCTAssertFalse(cell.accessibilityIdentifier?.isEmpty ?? true)
                }
                attach(page, "film-settings-\(index)-\(Int(width))-AXXXL")
                if let lastSection = page.sections.indices.last,
                   let lastRow = page.sections[lastSection].rows.indices.last,
                   page.table.contentSize.height > page.table.bounds.height {
                    let last = IndexPath(row: lastRow, section: lastSection)
                    // Self-sizing rows update contentSize while UIKit lays them
                    // out. Walk actual rows instead of trusting estimated height.
                    for section in page.sections.indices {
                        for row in page.sections[section].rows.indices {
                            page.table.scrollToRow(at: IndexPath(row: row, section: section), at: .bottom, animated: false)
                            page.table.layoutIfNeeded()
                        }
                    }
                    page.table.scrollToRow(at: last, at: .bottom, animated: false)
                    page.table.layoutIfNeeded()
                    XCTAssertGreaterThan(page.table.contentOffset.y, 0)
                    XCTAssertTrue(page.table.indexPathsForVisibleRows?.contains(last) == true)
                    attach(page, "film-settings-\(index)-\(Int(width))-AXXXL-bottom")
                }
                unhost(window)
            }
        }
        await fixture.sources.close(); await fixture.rules.close()
    }

    private func row(_ page: FilmSettingsListController, _ id: String) throws -> FilmSettingsRow {
        try XCTUnwrap(page.sections.flatMap(\.rows).first { $0.id == id })
    }
    private func eventually(_ predicate: @escaping @MainActor () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        let expected = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } }, evaluatedWith: nil)
        await fulfillment(of: [expected], timeout: 5)
        XCTAssertTrue(predicate(), file: file, line: line)
    }
    private func settle() async {
        let settled = expectation(description: "UIKit layout settled")
        DispatchQueue.main.async { settled.fulfill() }
        await fulfillment(of: [settled], timeout: 3)
    }
    private func host(_ page: UIViewController, size: CGSize? = nil) -> UIWindow {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let bounds = scene?.coordinateSpace.bounds ?? UIScreen.main.bounds
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: bounds)
        window.frame = bounds
        window.windowLevel = .normal + 1
        let container = UIViewController()
        container.addChild(page); container.view.addSubview(page.view)
        page.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            page.view.topAnchor.constraint(equalTo: container.view.topAnchor),
            page.view.leadingAnchor.constraint(equalTo: container.view.leadingAnchor),
            page.view.widthAnchor.constraint(equalToConstant: size?.width ?? bounds.width),
            page.view.heightAnchor.constraint(equalToConstant: size?.height ?? bounds.height),
        ])
        page.didMove(toParent: container)
        window.rootViewController = container; window.makeKeyAndVisible()
        page.beginAppearanceTransition(true, animated: false); page.endAppearanceTransition()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        window.layoutIfNeeded()
        page.view.layoutIfNeeded()
        return window
    }
    private func unhost(_ window: UIWindow) {
        if let page = window.rootViewController { PageViewController.invalidatePages(in: page) }
        window.endEditing(true)
        window.isHidden = true; window.rootViewController = nil
    }
    private func attach(_ page: UIViewController, _ name: String) {
        page.view.layoutIfNeeded()
        // Alert compositing needs a foreground scene and is covered by the
        // App-hosted test; these fixtures capture only ordinary page content.
        let image = UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
            page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true)
        }
        let attachment = XCTAttachment(image: image); attachment.name = name; attachment.lifetime = .keepAlways
        add(attachment)
    }
}

@MainActor
private final class FilmSettingsTheme: PageThemeProviding {
    let pageColorway = PageColorway.lilac
    let animatesPageColorway = false
    let pageThemeChanges = Empty<Void, Never>().eraseToAnyPublisher()
}

private struct FilmSettingsFixture: Sendable {
    let base: URL
    let rulesStore: DocumentStore
    let sourceStore: DocumentStore
    let rules: FilmRuleService
    let sources: FilmSourceService
    let runtime: FilmSettingsRPCFixture
    init() throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        rulesStore = try DocumentStore(baseURL: base, namespace: FilmRuleService.documentNamespace)
        sourceStore = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        rules = try FilmRuleService(store: rulesStore)
        runtime = FilmSettingsRPCFixture()
        let runtime = runtime, store = sourceStore
        sources = FilmSourceService(transport: .init(rpc: { method, params, _ in try await runtime.rpc(method, params) }),
                                    storeFactory: { store })
    }
    func removeFiles() { try? FileManager.default.removeItem(at: base) }
}

private actor FilmSettingsRPCFixture {
    var calls: [String] = []
    private var disabled = Set<String>()
    private var pause = false
    private var entered = false
    private var waiter: CheckedContinuation<Void, Never>?
    private var enteredWaiters: [CheckedContinuation<Void, Never>] = []
    func pauseProbes() { pause = true }
    func waitForProbe() async {
        if entered { return }
        await withCheckedContinuation { enteredWaiters.append($0) }
    }
    func releaseProbe() { waiter?.resume(); waiter = nil }
    func rpc(_ method: String, _ params: JSONValue) async throws -> JSONValue {
        let method = String(method.dropFirst("cat.script.".count)); calls.append(method)
        let fields: [String: JSONValue]
        if case .object(let values) = params { fields = values } else { fields = [:] }
        let id: String
        if case .string(let value)? = fields["installationId"] { id = value } else { id = "a" }
        let value: JSONValue
        switch method {
        case "status": value = .object(["instances": .array([cat("a"), cat("b")])])
        case "start": value = .object([:])
        case "enable":
            if fields["enabled"] == .bool(false) { disabled.insert(id) } else { disabled.remove(id) }
            value = cat(id)
        case "restart", "edit", "update", "install", "import": value = cat(id)
        case "invoke":
            if pause {
                pause = false; entered = true
                let waiting = enteredWaiters; enteredWaiters.removeAll(); waiting.forEach { $0.resume() }
                await withCheckedContinuation { waiter = $0 }
            }
            value = .object(["list": .array([]), "class": .array([])])
        default: throw FilmSourceError(code: "UNEXPECTED_\(method)")
        }
        return .object(["contract": .string("cat.script.v1"), "ok": .bool(true),
                        "data": .string(String(decoding: try JSONEncoder().encode(value), as: UTF8.self))])
    }
    private func cat(_ id: String) -> JSONValue {
        .object(["installationId": .string(id), "name": .string("测试猫源 " + id),
                 "sourceType": .string("network"), "sourceUrl": .string("https://example.invalid/\(id).js"),
                 "enabled": .bool(!disabled.contains(id)), "generation": .number(1),
                 "state": .string(disabled.contains(id) ? "disabled" : "ready"),
                 "importedAt": .number(1_700_000_000_000), "updatedAt": .null, "updateState": .string("idle"),
                 "updateError": .null, "sourceHash": .string("fixture"), "error": .null,
                 "sites": .array([.object(["key": .string("site"), "name": .string("测试视频源 " + id),
                    "api": .string("/api/" + id), "actions": .array([.string("home"), .string("category")]), "methods": .object([:])])])])
    }
}
#endif
