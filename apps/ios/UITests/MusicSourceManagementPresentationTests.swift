#if os(iOS)
import XCTest
import UIKit
import MeowContracts
import MeowPlayback
@testable import MeowUI
@testable import MeowMusicFeature

@MainActor
final class MusicSourceManagementPresentationTests: XCTestCase {
    func testNormalRowsKeepSwitchAndDirectEditWithOnlyCheckAndUpdateInMenu() async throws {
        try await withSources { page, _, _, fixture, _ in
            for id in ["network", "local"] {
                let edit = try self.button("management.edit.\(id)", in: page.view)
                let more = try self.button("management.more.\(id)", in: page.view)
                XCTAssertTrue(edit.isEnabled)
                XCTAssertEqual(more.menu?.children.compactMap { ($0 as? UIAction)?.title }, ["检测", "更新"])
                XCTAssertFalse(more.menu?.children.compactMap { $0 as? UIAction }
                    .contains { $0.attributes.contains(.destructive) } ?? true)
                XCTAssertEqual(edit.bounds.size, CGSize(width: 38, height: 38))
                XCTAssertEqual(more.bounds.size, CGSize(width: 38, height: 38))
                XCTAssertEqual(more.convert(more.bounds, to: page.view).minX -
                    edit.convert(edit.bounds, to: page.view).maxX, 4, accuracy: 0.5)
                XCTAssertEqual(edit.tintColor, UIColor(page.theme.settings.accent))
                XCTAssertEqual(more.tintColor, UIColor(page.theme.settings.accent))
                XCTAssertEqual(more.image(for: .normal)?.pngData(), ManagementIcon.more.pngData())
            }
            XCTAssertEqual(self.descendants(page.view).compactMap { $0 as? UISwitch }.count,
                           MusicAccountPlatform.allCases.count + 2)
            let manager = try XCTUnwrap(self.descendants(page.view).compactMap { $0 as? ManagementListView }.first)
            let list = try XCTUnwrap(self.descendants(manager).compactMap { $0 as? UITableView }.first)
            let rowIDs = (0..<list.numberOfRows(inSection: 0)).compactMap {
                list.dataSource?.tableView(list,
                    cellForRowAt: IndexPath(row: $0, section: 0)).accessibilityIdentifier
            }
            XCTAssertEqual(rowIDs.count, MusicAccountPlatform.allCases.count + 2)
            try self.button("management.edit.network", in: page.view).sendActions(for: .touchUpInside)
            let dialog = try await self.dialog(on: page)
            let name = try self.input("music.sources.edit.name", in: dialog)
            let address = try self.input("music.sources.edit.url", in: dialog)
            XCTAssertEqual(name.text, "网络音源")
            XCTAssertEqual(address.text, "https://sources.invalid/original.js")
            dialog.overrideUserInterfaceStyle = .dark
            let labels = dialog.body.arrangedSubviews.compactMap { $0 as? UILabel }
            XCTAssertEqual(labels.map(\.text), ["名称", "网络地址"])
            let settings = PageTheme(colors: page.appearance.pagePalette, dark: true).settings
            for label in labels {
                XCTAssertEqual(label.textColor.resolvedColor(with: dialog.traitCollection), UIColor(settings.ink))
            }
            name.text = "更新名称"; address.text = "https://sources.invalid/edited.js"
            try self.button("management.dialog.confirm", in: dialog.view).sendActions(for: .touchUpInside)
            await self.waitForPresentation("edited source read back") {
                page.presentedViewController == nil && page.state.item("network")?.name == "更新名称"
            }
            await page.state.flush()
            let edits = await fixture.edits
            XCTAssertEqual(edits.count, 1)
            XCTAssertEqual(edits.first?.id, "network")
            XCTAssertEqual(edits.first?.name, "更新名称")
            XCTAssertEqual(edits.first?.url, "https://sources.invalid/edited.js")
        }
    }

    func testNarrowAccessibilitySourceRowRetainsReaderListAndReachableControls() async throws {
        try await withSources { page, _, _, _, window in
            window.frame = CGRect(x: 0, y: 0, width: 320, height: 844)
            window.overrideUserInterfaceStyle = .dark
            page.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
            page.view.layoutIfNeeded()
            let manager = try XCTUnwrap(self.descendants(page.view).compactMap { $0 as? ManagementListView }.first)
            let list = try XCTUnwrap(self.descendants(manager).compactMap { $0 as? UITableView }.first)
            await self.waitForPresentation("large type source section laid out") {
                list.layoutIfNeeded()
                return list.numberOfSections == 1 &&
                    list.numberOfRows(inSection: 0) == MusicAccountPlatform.allCases.count + page.state.scripts.count
            }
            let index = IndexPath(row: MusicAccountPlatform.allCases.count, section: 0)
            list.scrollToRow(at: index, at: .top, animated: false)
            await self.waitForPresentation("narrow source controls displayed") {
                list.layoutIfNeeded()
                return list.cellForRow(at: index) != nil &&
                    self.descendants(page.view).contains {
                        $0.accessibilityIdentifier == "management.edit.network" && $0.bounds.width == 38
                    }
            }
            let cell = try XCTUnwrap(list.cellForRow(at: index))
            let edit = try self.button("management.edit.network", in: cell)
            let more = try self.button("management.more.network", in: cell)
            XCTAssertEqual(more.menu?.children.compactMap { ($0 as? UIAction)?.title }, ["检测", "更新"])
            XCTAssertEqual(list.frame.minX, 24, accuracy: 0.5)
            XCTAssertEqual(list.layer.cornerRadius, 22)
            XCTAssertEqual(list.separatorStyle, .singleLine)
            XCTAssertEqual(list.backgroundColor, UIColor(page.theme.settings.surface))
            let toggle = try XCTUnwrap(self.descendants(cell).compactMap { $0 as? UISwitch }.first)
            XCTAssertEqual(toggle.transform, CGAffineTransform(scaleX: 0.7, y: 0.7))
            for button in [edit, more] {
                let frame = button.convert(button.bounds, to: cell.contentView)
                XCTAssertGreaterThanOrEqual(frame.minX, 10 - 0.5)
                XCTAssertLessThanOrEqual(frame.maxX, cell.contentView.bounds.width - 10 + 0.5)
                XCTAssertEqual(button.bounds.size, CGSize(width: 38, height: 38))
                XCTAssertTrue(button.isEnabled)
                XCTAssertEqual(button.tintColor, UIColor(page.theme.settings.accent))
            }
        }
    }

    func testLocalEditUsesOnlyNameAndCancelDoesNotWrite() async throws {
        try await withSources { page, _, _, fixture, _ in
            try self.button("management.edit.local", in: page.view).sendActions(for: .touchUpInside)
            let cancelled = try await self.dialog(on: page)
            XCTAssertEqual(try self.input("music.sources.edit.name", in: cancelled).text, "本地音源")
            XCTAssertFalse(self.descendants(cancelled.view).contains { $0.accessibilityIdentifier == "music.sources.edit.url" })
            try self.input("music.sources.edit.name", in: cancelled).text = "不保存的名称"
            try self.button("management.dialog.cancel", in: cancelled.view).sendActions(for: .touchUpInside)
            await self.waitForPresentation("cancelled editor restored list") { page.canPresentPageAlert }
            let cancelledEdits = await fixture.edits
            XCTAssertTrue(cancelledEdits.isEmpty)
            XCTAssertEqual(page.state.item("local")?.name, "本地音源")
            try self.button("management.edit.local", in: page.view).sendActions(for: .touchUpInside)
            let saved = try await self.dialog(on: page)
            try self.input("music.sources.edit.name", in: saved).text = "本地新名称"
            try self.button("management.dialog.confirm", in: saved.view).sendActions(for: .touchUpInside)
            await self.waitForPresentation("local name saved") { page.state.item("local")?.name == "本地新名称" }
            await page.state.flush()
            let edits = await fixture.edits
            XCTAssertEqual(edits.count, 1)
            XCTAssertEqual(edits.first?.id, "local")
            XCTAssertNil(edits.first?.url)
        }
    }

    func testNetworkImportFloatsOverRetainedListAndOnlyConfirmSubmits() async throws {
        try await withSources { page, _, _, fixture, window in
            let background = page.view
            let initial = page.state.scripts.map(\.id)
            page.openURL()
            let cancelled = try await self.dialog(on: page)
            let input = try self.input("music.sources.url", in: cancelled)
            XCTAssertEqual(input.keyboardType, .URL)
            XCTAssertEqual(input.textContentType, .URL)
            XCTAssertEqual(input.autocorrectionType, .no)
            XCTAssertEqual(input.autocapitalizationType, .none)
            XCTAssertEqual(cancelled.modalPresentationStyle, .overFullScreen)
            XCTAssertTrue(page.view === background)
            XCTAssertTrue(page.view.isDescendant(of: window))
            XCTAssertFalse(page.view.isHidden)
            XCTAssertTrue(page.isPageVisible)
            XCTAssertEqual(page.state.scripts.map(\.id), initial)
            input.text = "https://sources.invalid/cancelled.js"
            try self.button("management.dialog.cancel", in: cancelled.view).sendActions(for: .touchUpInside)
            await self.waitForPresentation("cancelled import restored list") { page.canPresentPageAlert }
            let cancelledDownloads = await fixture.downloads
            XCTAssertTrue(cancelledDownloads.isEmpty)
            page.openURL()
            let imported = try await self.dialog(on: page)
            try self.input("music.sources.url", in: imported).text = "https://sources.invalid/imported.js"
            try self.button("management.dialog.confirm", in: imported.view).sendActions(for: .touchUpInside)
            await self.waitForPresentation("confirmed import read back") {
                page.presentedViewController == nil && page.state.item("imported") != nil
            }
            let downloads = await fixture.downloads
            XCTAssertEqual(downloads.map(\.absoluteString), ["https://sources.invalid/imported.js"])
        }
    }

    func testDeleteRequiresSelectedScriptsAndConfirmationAndPreservesBuiltIns() async throws {
        try await withSources { page, _, _, fixture, _ in
            page.confirmRemoveSelected()
            XCTAssertNil(page.presentedViewController)
            page.state.select("official:netease")
            page.confirmRemoveSelected()
            XCTAssertNil(page.presentedViewController)
            page.state.select("network")
            await self.waitForPresentation("selected script enables batch delete") {
                (try? self.button("management.delete", in: page.view).isEnabled) == true
            }
            try self.button("management.delete", in: page.view).sendActions(for: .touchUpInside)
            let cancelled = try await self.dialog(on: page)
            let beforeConfirm = await fixture.removed
            XCTAssertTrue(beforeConfirm.isEmpty)
            try self.button("management.dialog.cancel", in: cancelled.view).sendActions(for: .touchUpInside)
            await self.waitForPresentation("cancelled delete restored selection") { page.canPresentPageAlert }
            XCTAssertEqual(page.state.selected, ["official:netease", "network"])
            let cancelledRemovals = await fixture.removed
            XCTAssertTrue(cancelledRemovals.isEmpty)
            try self.button("management.delete", in: page.view).sendActions(for: .touchUpInside)
            let confirmed = try await self.dialog(on: page)
            try self.button("management.dialog.confirm", in: confirmed.view).sendActions(for: .touchUpInside)
            await self.waitForPresentation("only selected script removed") {
                page.presentedViewController == nil && page.state.scripts.map(\.id) == ["local"]
            }
            await page.state.flush()
            let removed = await fixture.removed
            XCTAssertEqual(removed, ["network"])
            XCTAssertTrue(page.state.officialEnabled(.netease))
            XCTAssertFalse(page.state.selected.contains("network"))
        }
    }

    func testDetailHasNoDeleteAndRetainsRollbackAndSharedRows() async throws {
        try await withSources { page, _, navigation, fixture, _ in
            let runner = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
            let detail = MusicScriptSourceDetailViewController(state: page.state, id: "network", appearance: page.appearance)
            navigation.pushViewController(detail, animated: false)
            await self.waitForPresentation("source detail loaded") {
                detail.canPresentPageAlert && page.state.details["network"]?.rollbackAvailable == true
            }
            let list = try XCTUnwrap(self.descendants(detail.view).compactMap { $0 as? UICollectionView }.first)
            list.layoutIfNeeded()
            let section = try XCTUnwrap((0..<list.numberOfSections).last)
            XCTAssertGreaterThan(section, 0)
            let row = try XCTUnwrap((0..<list.numberOfItems(inSection: section)).last)
            list.scrollToItem(at: IndexPath(item: row, section: section), at: .bottom, animated: false)
            let rollback = runner.buttons["music.sources.script.rollback"]
            await self.waitForPresentation("rollback action displayed") {
                detail.view.layoutIfNeeded()
                list.layoutIfNeeded()
                let bottom = max(-list.adjustedContentInset.top,
                                 list.contentSize.height - list.bounds.height + list.adjustedContentInset.bottom)
                list.setContentOffset(CGPoint(x: list.contentOffset.x, y: bottom), animated: false)
                list.layoutIfNeeded()
                return rollback.exists && rollback.isHittable
            }
            let attachment = XCTAttachment(screenshot: runner.screenshot())
            attachment.name = "music-source-detail-shared-rows"; attachment.lifetime = .keepAlways
            self.add(attachment)
            XCTAssertFalse(runner.descendants(matching: .any)
                .matching(NSPredicate(format: "label CONTAINS %@", "删除")).firstMatch.exists)
            XCTAssertFalse(self.descendants(detail.view).compactMap { $0 as? SettingsRowBackgroundView }.isEmpty)
            XCTAssertEqual(rollback.label, "回退更新")
            XCTAssertTrue(rollback.isEnabled)
            XCTAssertTrue(rollback.isHittable)
            await self.performSourcePageAction { rollback.tap() }
            await self.waitForPresentation("rollback source read back") { page.state.item("network")?.version == "1" }
            let rolledBack = await fixture.rolledBack, removed = await fixture.removed
            XCTAssertEqual(rolledBack, ["network"])
            XCTAssertTrue(removed.isEmpty)
        }
    }

    func testInputReceiptsCannotCommitAfterAccessChangeOrQuickVisibilityReturn() async throws {
        try await withSources { page, model, _, fixture, _ in
            try self.button("management.edit.network", in: page.view).sendActions(for: .touchUpInside)
            let editor = try await self.dialog(on: page)
            try self.input("music.sources.edit.name", in: editor).text = "旧账号输入"
            model.setAvailable(false); model.setAvailable(true)
            try self.button("management.dialog.confirm", in: editor.view).sendActions(for: .touchUpInside)
            await self.waitForPresentation("stale account editor closed") {
                page.canPresentPageAlert && page.state.available && page.state.scripts.count == 2
            }
            await page.state.flush()
            let edits = await fixture.edits
            XCTAssertTrue(edits.isEmpty)
            page.openURL()
            let importer = try await self.dialog(on: page)
            try self.input("music.sources.url", in: importer).text = "https://sources.invalid/stale.js"
            NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
            XCTAssertFalse(page.isPageVisible)
            NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
            await self.waitForPresentation("same page visible again") { page.isPageVisible }
            try self.button("management.dialog.confirm", in: importer.view).sendActions(for: .touchUpInside)
            await self.waitForPresentation("stale page importer closed") { page.canPresentPageAlert }
            await page.state.flush()
            let downloads = await fixture.downloads
            XCTAssertTrue(downloads.isEmpty)
        }
    }

    private func withSources(
        _ body: (MusicSourceManagementViewController, MusicViewModel, PageNavigationController,
                 MusicSourcePresentationFixture, UIWindow) async throws -> Void
    ) async throws {
        let runner = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
        await performSourcePageAction { runner.activate() }
        let domain = "MusicSourceManagementPresentation." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let fixture = MusicSourcePresentationFixture()
        let model = MusicViewModel(service: fixture, player: MusicSourcePresentationPlayer(), scriptSources: fixture)
        await model.load()
        let page = MusicSourceManagementViewController(model: model, appearance: appearance)
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([page], animated: false)
        await waitForPresentation("real App foreground scene is active") { UIApplication.shared.applicationState == .active }
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let previous = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        window.rootViewController = navigation; window.makeKeyAndVisible()
        window.layoutIfNeeded()
        defer {
            PageViewController.invalidatePages(in: navigation)
            window.isHidden = true; window.rootViewController = nil; previous?.makeKey()
        }
        do {
            await waitForPresentation("source list displayed") {
                page.view.layoutIfNeeded()
                return page.canPresentPageAlert && page.state.scripts.count == 2 &&
                    self.descendants(page.view).contains { $0.accessibilityIdentifier == "management.edit.local" }
            }
            await page.state.flush()
            try await body(page, model, navigation, fixture, window)
        } catch {
            PageViewController.invalidatePages(in: navigation)
            await model.close()
            throw error
        }
        PageViewController.invalidatePages(in: navigation)
        await model.close()
    }

    private func dialog(on page: MusicSourceManagementViewController) async throws -> ManagementDialog {
        await waitForPresentation("management dialog displayed") {
            page.presentedViewController is ManagementDialog && page.presentedViewController?.isBeingPresented == false
        }
        return try XCTUnwrap(page.presentedViewController as? ManagementDialog)
    }

    private func button(_ id: String, in view: UIView) throws -> UIButton {
        try XCTUnwrap(descendants(view).first { $0.accessibilityIdentifier == id } as? UIButton)
    }

    private func input(_ id: String, in dialog: ManagementDialog) throws -> UITextField {
        try XCTUnwrap(descendants(dialog.view).first { $0.accessibilityIdentifier == id } as? UITextField)
    }

    private func descendants(_ view: UIView) -> [UIView] { [view] + view.subviews.flatMap(descendants) }

    private func performSourcePageAction(_ action: @escaping @MainActor () -> Void) async {
        let performed = expectation(description: "source page runner action delivered")
        RunLoop.main.perform {
            MainActor.assumeIsolated { action() }
            performed.fulfill()
        }
        await fulfillment(of: [performed], timeout: 5)
    }

    private func waitForPresentation(_ message: String, until predicate: @escaping @MainActor () -> Bool) async {
        let deadline = Date().addingTimeInterval(3)
        while Date() < deadline {
            if predicate() { return }
            try? await Task.sleep(nanoseconds: 20_000_000)
        }
        XCTAssertTrue(predicate(), message)
    }
}

private actor MusicSourcePresentationFixture: MusicServing, MusicScriptSourceManaging {
    private var names = ["network": "网络音源", "local": "本地音源"]
    private var addresses = ["network": "https://sources.invalid/original.js"]
    private var enabled = Set<String>(), rolled = Set<String>()
    private var installed = ["network", "local"]
    private(set) var edits: [(id: String, name: String, url: String?)] = []
    private(set) var downloads: [URL] = []
    private(set) var removed: [String] = []
    private(set) var rolledBack: [String] = []

    private func item(_ id: String) -> MusicScriptSourceItem {
        .init(id: id, name: names[id] ?? id, version: rolled.contains(id) ? "1" : "2",
              origin: addresses[id] == nil ? .file : .network(host: "sources.invalid"),
              platforms: [.qq], enabled: enabled.contains(id), state: enabled.contains(id) ? .ready : .disabled)
    }
    func scriptSources() -> [MusicScriptSourceItem] { installed.map(item) }
    func scriptSourceDetail(_ id: String) -> MusicScriptSourceDetail {
        .init(item: item(id), networkURL: addresses[id], rollbackAvailable: id == "network" && !rolled.contains(id))
    }
    func editScriptSource(_ id: String, name: String, networkURL: String?) -> MusicScriptSourceItem {
        edits.append((id, name, networkURL)); names[id] = name; addresses[id] = networkURL
        return item(id)
    }
    func downloadScriptSource(from url: URL) -> MusicScriptSourceItem {
        downloads.append(url); installed.append("imported"); names["imported"] = "导入音源"; addresses["imported"] = url.absoluteString
        return item("imported")
    }
    func setScriptSourceEnabled(_ id: String, enabled: Bool) -> MusicScriptSourceItem {
        if enabled { self.enabled.insert(id) } else { self.enabled.remove(id) }; return item(id)
    }
    func removeScriptSource(_ id: String) { removed.append(id); installed.removeAll { $0 == id } }
    func rollbackScriptSource(_ id: String) -> MusicScriptSourceItem {
        rolledBack.append(id); rolled.insert(id); return item(id)
    }
    func checkScriptSource(_ id: String) -> MusicScriptSourceItem { item(id) }
    func updateScriptSource(_ id: String) -> MusicScriptSourceItem { item(id) }
    func updateScriptSource(_ id: String, data: Data, fileName: String) -> MusicScriptSourceItem { item(id) }
    func importScriptSource(_ data: Data, fileName: String) throws -> MusicScriptSourceItem { throw MusicServiceError.unsupported }
    func state() -> MusicPlaybackState { .init() }
    func queue() -> MusicQueueSnapshot { .init(revision: 0, currentId: nil, entries: []) }
    func platforms() -> [MusicPlatform] { [] }
    func collections() -> [MusicCollection] { [] }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { [] }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func sourceAvailability(for ref: MusicReference) -> MusicSourceAvailability { .init(options: [], scriptState: .ready) }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }
}

@MainActor
private final class MusicSourcePresentationPlayer: MusicPlaybackDriving {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        XCTFail("Unexpected playback"); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected pause") }
    func resume(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected resume") }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { XCTFail("Unexpected seek"); return false }
    func stop(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected stop") }
}
#endif
