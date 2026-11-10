#if os(iOS)
import XCTest
import UIKit
import Combine
import MeowUI
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackUIKitTests: XCTestCase {
    private var mountedWindows: [(page: UIViewController, window: UIWindow, previousKeyWindow: UIWindow?)] = []

    override func setUp() async throws {
        try await super.setUp()
        addTeardownBlock {
            await MainActor.run {
                while let mounted = self.mountedWindows.last {
                    self.unmount(mounted.page, mounted.window)
                }
            }
        }
        try await runnerAction { $0.activate() }
        try await waitFor("Runner and scene are really foreground active") {
            UIApplication.shared.applicationState == .active &&
                UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive }
        }
    }

    func testMetadataMenusOnlyExposeConfirmedProvidersAndChangingProviderUpdatesDirectoryVisibility() async throws {
        let server = FilmUIKitPlaybackServer(), media = FilmUIKitPlaybackMedia()
        let model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        let page = try detailPage(model)
        let window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        let source = try button("film.detail.source", page.view)
        XCTAssertEqual(source.menu?.children.compactMap { ($0 as? UIAction)?.title }, [
            "TMDB · 星海航线", "测试站 · 星海航线", "测试站 · 失败站点", "加载更多来源",
        ])
        let before = await server.calls()
        XCTAssertEqual(before.map(\.operation), ["session.open"])
        source.sendActions(for: .touchUpInside)
        let opened = await server.calls()
        XCTAssertEqual(opened.count, before.count, "Opening the menu must not start another source search")
        XCTAssertTrue(try button("film.detail.directory", page.view).isHidden)

        let files = [FilmUIKitPlaybackFixture.file("one", name: "第一季", path: "/剧集/第一季/1.mp4")]
        let associated = FilmUIKitPlaybackFixture.metadataSources([.tmdb, .douban])
        try await server.publish(FilmUIKitPlaybackFixture.snapshot(2, metadataSources: associated,
                                                                 metadataError: "UPSTREAM_HTTP"))
        await settle()
        let status = try XCTUnwrap(view("film.detail.metadata-state", page.view) as? UILabel)
        XCTAssertEqual(status.text, "UPSTREAM_HTTP")
        XCTAssertTrue(try button("film.detail.play", page.view).isEnabled,
                      "Association failure must not block the primary playback chain")
        await server.reply(to: "browse.metadata", with: FilmUIKitPlaybackFixture.snapshot(provider: .douban,
            browsing: FilmUIKitPlaybackFixture.browsing(sourceID: "result-a", files: files, original: files),
            metadataSources: associated))
        invoke(try menuAction("豆瓣 · 星海航线", source.menu))
        await model.waitForPendingWork()
        await settle()
        XCTAssertFalse(try button("film.detail.directory", page.view).isHidden)
        XCTAssertEqual(try button("film.detail.line", page.view).title(for: .normal), "未知原标签")
        let season = try button("film.detail.season", page.view)
        let mapping = try XCTUnwrap(season.menu?.children.compactMap { $0 as? UIMenu }.first { $0.title == "分季资料" })
        invoke(try menuAction("TMDB · 星海航线", mapping))
        await model.waitForPendingWork()
        let commands = await server.calls().filter { $0.operation == "session.command" }
        XCTAssertEqual(commands.map(\.action), ["browse.metadata", "browse.metadata"])
        XCTAssertNil(commands[0].input.filmObject?["keepSource"])
        XCTAssertEqual(commands[1].input.filmObject?["keepSource"], .bool(true))
        XCTAssertTrue(media.inputs.isEmpty)
    }

    func testCurrentSourceFailureClearsThirdRowButKeepsSourceSelectionAndCanRecover() async throws {
        let file = FilmUIKitPlaybackFixture.file("first", name: "原始文件名")
        let ready = FilmUIKitPlaybackFixture.snapshot(provider: .douban,
            browsing: FilmUIKitPlaybackFixture.browsing(sourceID: "result-a", files: [file], original: [file]))
        let server = FilmUIKitPlaybackServer(ready), media = FilmUIKitPlaybackMedia()
        let model = makeModel(server, media)
        await model.open()?.value
        let page = try detailPage(model), window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        XCTAssertNotNil(view("film.file.file-first", page.view))
        await server.reply(to: "browse", with: FilmUIKitPlaybackFixture.snapshot(provider: .douban,
            browsing: FilmUIKitPlaybackFixture.browsing(sourceID: "result-b", files: [file],
                                                        original: [file], error: "UPSTREAM_HTTP")))
        invoke(try menuAction("测试站 · 失败站点", button("film.detail.source", page.view).menu))
        await model.waitForPendingWork()
        await settle()
        XCTAssertNil(view("film.file.file-first", page.view))
        let error = try XCTUnwrap(view("film.detail.episodes-state", page.view) as? UILabel)
        XCTAssertEqual(error.text, "UPSTREAM_HTTP")
        XCTAssertEqual(error.textAlignment, .center)
        XCTAssertFalse(try button("film.detail.play", page.view).isEnabled)
        XCTAssertTrue(try button("film.detail.source", page.view).isEnabled)
        try button("film.detail.play", page.view).sendActions(for: .touchUpInside)
        await model.waitForPendingWork()
        await server.reply(to: "browse", with: ready)
        invoke(try menuAction("测试站 · 星海航线", button("film.detail.source", page.view).menu))
        await model.waitForPendingWork()
        await settle()
        XCTAssertNotNil(view("film.file.file-first", page.view))
        XCTAssertTrue(try button("film.detail.play", page.view).isEnabled)
        let calls = await server.calls()
        XCTAssertEqual(calls.compactMap(\.action), ["browse", "browse"])
        XCTAssertTrue(media.inputs.isEmpty)
    }

    func testRawDirectoryUsesTheCompleteUnfilteredLineAndFoldersMakeZeroRequests() async throws {
        let first = FilmUIKitPlaybackFixture.file("first", name: "第一季原文件", path: "/剧集/第一季/1.mp4")
        let second = FilmUIKitPlaybackFixture.file("second", name: "第二季原文件", path: "/剧集/第二季/1.mp4")
        let server = FilmUIKitPlaybackServer(FilmUIKitPlaybackFixture.snapshot(provider: .douban,
            browsing: FilmUIKitPlaybackFixture.browsing(sourceID: "result-a", season: 2,
                                                        files: [second], original: [first, second])))
        let model = makeModel(server, FilmUIKitPlaybackMedia())
        await model.open()?.value
        let page = FilmDirectoryViewController(model: model, sourceID: "result-a", lineID: .number(0),
                                               themeProvider: FilmPlaybackUIKitTheme())
        var presentations = 0
        page.onPresentPlayback = { presentations += 1 }
        let navigation = PageNavigationController(themeProvider: FilmPlaybackUIKitTheme())
        navigation.setViewControllers([page], animated: false)
        let window = try mount(navigation)
        defer { unmount(navigation, window) }
        try await visible(page)
        try select("film.directory.folder.0", page)
        let root = try XCTUnwrap(navigation.topViewController as? FilmDirectoryViewController)
        try await visible(root)
        XCTAssertEqual(root.pageTitle, "/")
        try select("film.directory.folder.0", root)
        let directory = try XCTUnwrap(navigation.topViewController as? FilmDirectoryViewController)
        try await visible(directory)
        XCTAssertEqual(directory.sections[0].rows.map(\.title), ["第一季", "第二季"],
                       "Season two browsing must not crop the original directory")
        try select("film.directory.folder.0", directory)
        let season = try XCTUnwrap(navigation.topViewController as? FilmDirectoryViewController)
        try await visible(season)
        XCTAssertEqual(season.sections[0].rows.map(\.title), ["第一季原文件"])
        let before = await server.calls()
        XCTAssertEqual(before.map(\.operation), ["session.open"])
        try select("film.directory.file.file-first", season)
        await model.waitForPendingWork()
        let calls = await server.calls()
        XCTAssertEqual(calls.compactMap(\.action), ["select.candidate"])
        XCTAssertEqual(calls.last?.input.filmObject?["candidateId"], .string("candidate-first"))
        XCTAssertEqual(presentations, 1)
        navigation.popViewController(animated: false)
        XCTAssertTrue(navigation.topViewController === directory)
    }

    func testFourDetailActionsSendExactConstraintsAndBlockOnlyAfterConfirmation() async throws {
        let server = FilmUIKitPlaybackServer(try blockSnapshot("selected-A", current: false))
        let model = makeModel(server, FilmUIKitPlaybackMedia())
        await model.open()?.value
        let page = try detailPage(model), window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        invoke(try menuAction("UC 网盘", button("film.detail.pan", page.view).menu))
        await model.waitForPendingWork()
        invoke(try menuAction("4K HDR", button("film.detail.quality", page.view).menu))
        await model.waitForPendingWork()
        try button("film.detail.switch", page.view).sendActions(for: .touchUpInside)
        await model.waitForPendingWork()
        try button("film.detail.block", page.view).sendActions(for: .touchUpInside)
        try await waitFor { page.presentedViewController is UIAlertController }
        var calls = await server.calls()
        XCTAssertFalse(calls.compactMap(\.action).contains("block"))
        try await runnerAction { $0.alerts.buttons["屏蔽并换源"].tap() }
        try await waitFor { page.presentedViewController == nil && page.canPresentPageAlert }
        await model.waitForPendingWork()
        calls = await server.calls().filter { $0.operation == "session.command" }
        XCTAssertEqual(calls.map(\.action), ["pan", "quality", "switch", "block"])
        XCTAssertEqual(calls[0].input.filmObject?["provider"], .string("uc"))
        XCTAssertEqual(calls[1].input.filmObject?["quality"], .string("4k_hdr"))
        XCTAssertEqual(calls[3].input.filmObject?["attemptId"], .string("selected-A"))
        let blocked = await server.blockedAttempts()
        XCTAssertEqual(blocked, ["selected-A"])
    }

    func testBothBlockConfirmationsKeepCapturedAttemptAfterCurrentReplacement() async throws {
        for player in [false, true] {
            let server = FilmUIKitPlaybackServer(try blockSnapshot("attempt-A", current: true))
            let media = FilmUIKitPlaybackMedia(), model = makeModel(server, media)
            await model.open()?.value
            await server.awaitObservation()
            let page = try blockPage(model, player: player), window = try mount(page)
            defer { unmount(page, window) }
            try await visible(page)
            try button(player ? "film.player.block" : "film.detail.block", page.view).sendActions(for: .touchUpInside)
            let confirmation = try await presentedAlert(page, title: "片源错误", style: .alert)
            let before = await server.calls().filter { $0.action == "block" }
            XCTAssertTrue(before.isEmpty)
            try await server.publish(blockSnapshot("attempt-B", current: true, sequence: 10))
            XCTAssertEqual(model.snapshot?.current?.attemptID, "attempt-B")
            try await runnerAction { $0.alerts.buttons["屏蔽并换源"].tap() }
            try await waitFor("The captured block confirmation is dismissed, not any subsequent error alert") {
                page.presentedViewController !== confirmation && confirmation.presentingViewController == nil &&
                    confirmation.viewIfLoaded?.window == nil && !confirmation.isBeingDismissed
            }
            try await waitFor { model.actionError?.code == "STALE_SESSION" }
            await model.waitForPendingWork()
            let calls = await server.calls().filter { $0.action == "block" }
            XCTAssertEqual(calls.count, 1)
            XCTAssertEqual(calls.first?.input.filmObject?["attemptId"], .string("attempt-A"))
            XCTAssertEqual(model.actionError?.code, "STALE_SESSION")
            XCTAssertEqual(model.snapshot?.current?.attemptID, "attempt-B")
            let blocked = await server.blockedAttempts()
            XCTAssertTrue(blocked.isEmpty, "A stale confirmation must not block its replacement")
            XCTAssertTrue(media.inputs.isEmpty)
            let error = try await presentedAlert(page, title: "错误", style: .alert, after: confirmation)
            XCTAssertEqual(error.message, "STALE_SESSION")
        }
    }

    func testBothBlockConfirmationsCancelWithoutCommandsAndConfirmExactCurrentOrPreparedAttempt() async throws {
        for player in [false, true] {
            for current in [false, true] {
                let server = FilmUIKitPlaybackServer(try blockSnapshot("attempt-A", current: current))
                let model = makeModel(server, FilmUIKitPlaybackMedia())
                await model.open()?.value
                let page = try blockPage(model, player: player), window = try mount(page)
                defer { unmount(page, window) }
                try await visible(page)
                let control = try button(player ? "film.player.block" : "film.detail.block", page.view)
                control.sendActions(for: .touchUpInside)
                let cancellation = try await presentedAlert(page, title: "片源错误", style: .alert)
                try await runnerAction { $0.alerts.buttons["取消"].tap() }
                try await waitFor("Cancelling the block confirmation leaves the page ready") {
                    page.presentedViewController == nil && page.canPresentPageAlert
                }
                await model.waitForPendingWork()
                let cancelled = await server.calls()
                XCTAssertEqual(cancelled.map(\.operation), ["session.open"], "Cancellation sends no command at all")
                let before = await server.blockedAttempts()
                XCTAssertTrue(before.isEmpty)
                control.sendActions(for: .touchUpInside)
                _ = try await presentedAlert(page, title: "片源错误", style: .alert, after: cancellation)
                try await runnerAction { $0.alerts.buttons["屏蔽并换源"].tap() }
                try await waitFor { page.presentedViewController == nil && page.canPresentPageAlert }
                try await waitFor { model.snapshot?.sequence == 2 || model.actionError != nil }
                await model.waitForPendingWork()
                let calls = await server.calls().filter { $0.operation == "session.command" }
                XCTAssertEqual(calls.map(\.action), ["block"])
                XCTAssertEqual(calls.first?.input.filmObject?["attemptId"], .string("attempt-A"),
                               "Current takes precedence over a different pending attempt; prepared-only uses its own ID")
                let blocked = await server.blockedAttempts()
                XCTAssertEqual(blocked, ["attempt-A"])
                XCTAssertNil(model.actionError)
            }
        }
    }

    func testBothBlockEntrypointsWithoutAnActualSelectionSendNothing() async throws {
        for player in [false, true] {
            let server = FilmUIKitPlaybackServer(), model = makeModel(server, FilmUIKitPlaybackMedia())
            await model.open()?.value
            let page = try blockPage(model, player: player), window = try mount(page)
            defer { unmount(page, window) }
            try await visible(page)
            try button(player ? "film.player.block" : "film.detail.block", page.view).sendActions(for: .touchUpInside)
            await model.waitForPendingWork()
            XCTAssertNil(page.presentedViewController)
            let calls = await server.calls(), blocked = await server.blockedAttempts()
            XCTAssertEqual(calls.map(\.operation), ["session.open"])
            XCTAssertTrue(blocked.isEmpty)
        }
    }

    func testPlayerControlsUseTheOnePassiveSurfaceAndTheActualMediaOwner() async throws {
        let server = FilmUIKitPlaybackServer(FilmUIKitPlaybackFixture.snapshot(attemptID: "controls"))
        let media = FilmUIKitPlaybackMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        let page = FilmPlayerViewController(model: model, themeProvider: FilmPlaybackUIKitTheme())
        let window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        XCTAssertEqual(media.inputs.count, 1, "Mounting/fullscreen presentation must not create another engine")
        XCTAssertEqual(media.surface.superview?.accessibilityIdentifier, "film.player.video")
        try button("film.player.play-pause", page.view).sendActions(for: .touchUpInside)
        try button("film.player.play-pause", page.view).sendActions(for: .touchUpInside)
        XCTAssertEqual(media.paused, 1); XCTAssertEqual(media.resumed, 1)
        let seek = try XCTUnwrap(view("film.player.seek", page.view) as? UISlider)
        seek.sendActions(for: .touchDown); seek.value = 92; seek.sendActions(for: .touchUpInside)
        XCTAssertEqual(media.seeks, [92])
        invoke(try menuAction("1.5×", button("film.player.rate", page.view).menu))
        invoke(try menuAction("填满", button("film.player.scale", page.view).menu))
        let volume = try XCTUnwrap(view("film.player.volume", page.view) as? UISlider)
        volume.value = 0.4; volume.sendActions(for: .valueChanged)
        XCTAssertEqual(media.rates, [1.5]); XCTAssertEqual(media.scales.last, .fill)
        XCTAssertEqual(try XCTUnwrap(media.volumes.last), 0.4, accuracy: 0.0001)
        try button("film.player.pip", page.view).sendActions(for: .touchUpInside)
        XCTAssertTrue(media.systemOwner)
        await settle()
        try button("film.player.pip", page.view).sendActions(for: .touchUpInside)
        XCTAssertFalse(media.systemOwner)
        var shortFields = try FilmUIKitPlaybackFixture.snapshot(shortVideo: true).filmRequiredObject()
        shortFields["current"] = .object(["attemptId": .string("controls"), "candidate": FilmUIKitPlaybackFixture.candidate(),
            "position": .number(34), "duration": .number(600), "paused": .bool(false)])
        await server.reply(to: "shortvideo", with: .object(shortFields))
        try button("film.player.short-video", page.view).sendActions(for: .touchUpInside)
        await model.waitForPendingWork()
        await settle()
        XCTAssertTrue(try button("film.player.short-video", page.view).isSelected)
        try button("film.player.next", page.view).sendActions(for: .touchUpInside)
        await model.waitForPendingWork()
        try button("film.player.previous", page.view).sendActions(for: .touchUpInside)
        await model.waitForPendingWork()
        let calls = await server.calls()
        XCTAssertEqual(calls.compactMap(\.action).suffix(3), ["shortvideo", "next", "previous"])
        XCTAssertEqual(calls.first { $0.action == "shortvideo" }?.input.filmObject?["enabled"], .bool(true))
        try button("film.player.fullscreen", page.view).sendActions(for: .touchUpInside)
        XCTAssertTrue(page.prefersHomeIndicatorAutoHidden)
        XCTAssertEqual(media.inputs.count, 1)
    }

    func testExternalPlayerMenuInvokesBothRealLaunchConsumersAndPreservesTheInternalOwnerOnRejection() async throws {
        for player in FilmExternalPlayer.allCases {
            let server = FilmUIKitPlaybackServer(FilmUIKitPlaybackFixture.snapshot(attemptID: "external"))
            let media = FilmUIKitPlaybackMedia()
            var opened: [URL] = []
            let model = makeModel(server, media, opener: { opened.append($0); return false })
            await model.open()?.value
            await model.waitForPendingWork()
            let page = FilmPlayerViewController(model: model, themeProvider: FilmPlaybackUIKitTheme())
            let window = try mount(page)
            try await visible(page)
            let external = try button("film.player.external", page.view)
            XCTAssertTrue(external.isEnabled); XCTAssertTrue(external.showsMenuAsPrimaryAction)
            XCTAssertEqual(external.menu?.children.compactMap { ($0 as? UIAction)?.title }, ["Infuse", "nPlayer"])
            let before = await server.calls()
            external.sendActions(for: .touchUpInside)
            XCTAssertTrue(opened.isEmpty, "Opening the menu itself must not export a URL")
            let menuCalls = await server.calls()
            XCTAssertEqual(menuCalls.count, before.count)
            invoke(try menuAction(player.title, external.menu))
            try await waitFor { model.actionError?.code == "EXTERNAL_PLAYER_OPEN_FAILED" && !model.requiresExplicitPlay }
            await model.waitForPendingWork()
            XCTAssertEqual(opened.map(\.scheme), [player == .infuse ? "infuse" : "nplayer-https"])
            XCTAssertEqual(media.paused, 1); XCTAssertEqual(media.resumed, 1)
            XCTAssertEqual(media.stops, 0); XCTAssertEqual(model.mediaState.attemptID, "external")
            let calls = await server.calls()
            XCTAssertEqual(calls.filter { $0.operation == "session.attempt" }.count, 2)
            XCTAssertFalse(calls.contains { $0.operation == "session.release" || $0.action == "media.ended" })
            unmount(page, window)
            await model.close()
        }
    }

    func testExternalPlayerMenuHasAnHonestNoCurrentErrorInsteadOfADisabledConsumer() async throws {
        let server = FilmUIKitPlaybackServer(), media = FilmUIKitPlaybackMedia()
        let model = makeModel(server, media)
        await model.open()?.value
        let page = FilmPlayerViewController(model: model, themeProvider: FilmPlaybackUIKitTheme())
        let window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        let external = try button("film.player.external", page.view)
        XCTAssertTrue(external.isEnabled)
        invoke(try menuAction("Infuse", external.menu))
        try await waitFor { page.presentedViewController is UIAlertController }
        XCTAssertEqual((page.presentedViewController as? UIAlertController)?.message, "当前没有可交接的播放")
        let calls = await server.calls()
        XCTAssertFalse(calls.contains { $0.operation == "session.attempt" })
        XCTAssertEqual(media.paused, 0); XCTAssertEqual(media.stops, 0)
    }

    func testTheSamePlayerModalReturnsFromOrdinaryBackgroundWithoutAutoplayAndOneTapFormallyPlays() async throws {
        for autoPrepare in [false, true] {
            let server = FilmUIKitPlaybackServer(FilmUIKitPlaybackFixture.snapshot(attemptID: "before-background"))
            let media = FilmUIKitPlaybackMedia(), model = makeModel(server, media)
            await model.open()?.value
            await model.waitForPendingWork()
            let page = FilmPlayerViewController(model: model, themeProvider: FilmPlaybackUIKitTheme())
            let window = try mount(page)
            try await visible(page)
            NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
            await model.close(allowSystemOwner: true)
            XCTAssertTrue(model.isReleased); XCTAssertEqual(media.stops, 1)
            var fields = try FilmUIKitPlaybackFixture.snapshot(100,
                attemptID: autoPrepare ? "after-background" : nil).filmRequiredObject()
            fields["stage"] = .string(autoPrepare ? "prepared" : "idle")
            if var attempt = fields["attempt"]?.filmObject {
                attempt["prepareOnly"] = .bool(true); fields["attempt"] = .object(attempt)
            }
            var preferences = FilmPlaybackPreferences.defaults
            preferences.iosAutoPrepare = autoPrepare; fields["preferences"] = preferences.value
            try await server.publish(.object(fields))
            NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
            try await visible(page)
            await model.waitForPendingWork()
            await settle()
            XCTAssertTrue(window.rootViewController === page, "The modal and its controls are not reconstructed")
            XCTAssertEqual(model.snapshot?.stage, autoPrepare ? "prepared" : "idle")
            XCTAssertEqual(model.snapshot?.preferences.iosAutoPrepare, autoPrepare)
            XCTAssertNil(model.mediaState.attemptID)
            XCTAssertEqual(media.inputs.map(\.attemptID), ["before-background"])
            let play = try button("film.player.play-pause", page.view)
            XCTAssertTrue(play.isEnabled); XCTAssertEqual(play.title(for: .normal), "播放")
            var calls = await server.calls()
            XCTAssertFalse(calls.contains { $0.action == "play" })
            var formal = try FilmUIKitPlaybackFixture.snapshot(101, attemptID: "after-background").filmRequiredObject()
            formal["preferences"] = preferences.value
            await server.reply(to: "play", with: .object(formal))
            play.sendActions(for: .touchUpInside)
            await model.waitForPendingWork()
            await settle()
            calls = await server.calls()
            XCTAssertEqual(calls.compactMap(\.action).filter { $0 == "play" }, ["play"])
            XCTAssertEqual(media.inputs.map(\.attemptID), ["before-background", "after-background"])
            XCTAssertEqual(media.resumed, 0)
            XCTAssertEqual(calls.filter { $0.operation == "session.media.release" }
                .map { $0.input.filmObject?["attemptId"] }, [.string("before-background")])
            unmount(page, window)
            await model.close()
        }
    }

    func testBufferingPlayerShowsTheActualPlayingIntentAndPauseControlDoesNotResumeIt() async throws {
        let server = FilmUIKitPlaybackServer(FilmUIKitPlaybackFixture.snapshot(attemptID: "buffering"))
        let media = FilmUIKitPlaybackMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        let page = FilmPlayerViewController(model: model, themeProvider: FilmPlaybackUIKitTheme())
        let window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        media.setBuffering(true)
        await settle()
        let play = try button("film.player.play-pause", page.view)
        XCTAssertEqual(play.title(for: .normal), "暂停")
        XCTAssertTrue(try XCTUnwrap(descendants(page.view).compactMap { $0 as? UIActivityIndicatorView }.first).isAnimating)
        play.sendActions(for: .touchUpInside)
        await settle()
        XCTAssertEqual(media.paused, 1); XCTAssertEqual(media.resumed, 0)
        XCTAssertEqual(play.title(for: .normal), "播放")
        XCTAssertTrue(model.mediaState.paused)
    }

    func testShortVideoSwipesUseAdjacentEpisodeCommandsOnlyWhenEnabled() async throws {
        let server = FilmUIKitPlaybackServer(), media = FilmUIKitPlaybackMedia()
        let model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        let page = FilmPlayerViewController(model: model, themeProvider: FilmPlaybackUIKitTheme())
        let window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        let video = try XCTUnwrap(view("film.player.video", page.view))
        let up = try XCTUnwrap(video.gestureRecognizers?.compactMap { $0 as? UISwipeGestureRecognizer }.first {
            $0.direction == .up
        })
        let down = try XCTUnwrap(video.gestureRecognizers?.compactMap { $0 as? UISwipeGestureRecognizer }.first {
            $0.direction == .down
        })
        XCTAssertFalse(up.isEnabled); XCTAssertFalse(down.isEnabled)
        let app = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
        try await runnerAction { $0.buttons["film.player.fullscreen"].tap() }
        try await waitFor("Fullscreen exposes the video surface to touch") {
            page.view.layoutIfNeeded()
            let center = CGPoint(x: video.bounds.midX, y: video.bounds.midY)
            let point = video.convert(center, to: window)
            guard let hit = window.hitTest(point, with: nil) else { return false }
            return hit === video || hit.isDescendant(of: video)
        }
        try await runnerAction { _ in app.otherElements["film.player.video"].swipeUp() }
        var calls = await server.calls()
        XCTAssertTrue(calls.compactMap(\.action).isEmpty)
        try await server.publish(FilmUIKitPlaybackFixture.snapshot(2, shortVideo: true))
        await settle()
        XCTAssertTrue(up.isEnabled); XCTAssertTrue(down.isEnabled)
        try await runnerAction { _ in app.otherElements["film.player.video"].swipeUp() }
        await model.waitForPendingWork()
        try await runnerAction { _ in app.otherElements["film.player.video"].swipeDown() }
        await model.waitForPendingWork()
        calls = await server.calls()
        XCTAssertEqual(calls.compactMap(\.action), ["next", "previous"])
        XCTAssertTrue(media.inputs.isEmpty, "A swipe submits one business intent, not a parallel native prefetch")
    }

    func testPlayerPresentsAnExistingFailureAfterTheModalTransitionFinishes() async throws {
        let server = FilmUIKitPlaybackServer(), model = makeModel(server, FilmUIKitPlaybackMedia())
        await model.open()?.value
        await server.awaitObservation()
        model.reportMediaControlError(.init(code: "MEDIA_OUTPUT_FAILED"))
        let host = PageViewController(themeProvider: FilmPlaybackUIKitTheme())
        let window = try mount(host)
        defer { unmount(host, window) }
        try await visible(host)
        let page = FilmPlayerViewController(model: model, themeProvider: FilmPlaybackUIKitTheme())
        defer { PageViewController.invalidatePages(in: page) }
        host.present(page, animated: true)
        try await waitFor { page.presentedViewController is UIAlertController }
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.message, "MEDIA_OUTPUT_FAILED")
        XCTAssertEqual(model.actionError?.code, "MEDIA_OUTPUT_FAILED",
                       "Presentation is not acknowledgment or a successful playback receipt")
        await withCheckedContinuation { continuation in
            host.dismiss(animated: false) { continuation.resume() }
        }
    }

    func testChangingMetadataClearsOldImagesAndUsesTheNewLocalFavoriteIdentity() async throws {
        let server = FilmUIKitPlaybackServer(), media = FilmUIKitPlaybackMedia()
        let model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        let suite = "FilmPlaybackUIKitTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let original = FilmItem(sourceID: "tmdb", id: "19", title: "星海航线", kind: .tv,
            posterURL: URL(string: "https://artwork.example.invalid/poster.png"))
        let logo = URL(string: "https://artwork.example.invalid/logo.png")!
        let library = FilmLibrary(defaults: defaults)
        XCTAssertTrue(try library.toggle(original))
        let data = try XCTUnwrap(UIGraphicsImageRenderer(size: CGSize(width: 8, height: 8)).image { context in
            UIColor.red.setFill(); context.fill(CGRect(x: 0, y: 0, width: 8, height: 8))
        }.pngData())
        let images = ArtworkImages(maximumBytes: 1_024 * 1_024) { _ in data }
        let page = FilmDetailViewController(detail: .init(item: original, logoURL: logo, summary: "原资料"),
            library: library, images: images, themeProvider: FilmPlaybackUIKitTheme(), model: model)
        let window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        try await waitFor {
            self.descendants(page.view).compactMap { $0 as? ArtworkView }.filter { $0.image != nil }.count == 2
        }
        let favorite = try button("film.detail.favorite", page.view)
        XCTAssertEqual(favorite.configuration?.title, "已收藏")
        var changed = try FilmUIKitPlaybackFixture.snapshot(2, provider: .douban).filmRequiredObject()
        changed["metadata"] = .null
        var target = try XCTUnwrap(changed["target"]?.filmObject)
        target["metadataId"] = .number(0)
        changed["target"] = .object(target)
        try await server.publish(.object(changed))
        await settle()
        XCTAssertTrue(descendants(page.view).compactMap { $0 as? ArtworkView }.allSatisfy { $0.image == nil })
        XCTAssertEqual(favorite.configuration?.title, "收藏")
        XCTAssertEqual(try XCTUnwrap(view("film.detail.title", page.view) as? UILabel).text, "星海航线")
        favorite.sendActions(for: .touchUpInside)
        XCTAssertTrue(library.contains(.init(sourceID: "douban", id: "0", title: "星海航线")))
        XCTAssertTrue(library.contains(original), "Switching metadata must not rewrite the existing favorite")
    }

    func testAutoPrepareUIEditsADraftAndOnlySuccessfulSavePublishesPreferences() async throws {
        let store = FilmUIKitPlaybackSettingsStore()
        let owner = FilmPlaybackSettingsModel(transport: store.transport)
        let page = FilmPlaybackPreferencesController(owner: owner, area: .autoPrepare,
                                                     themeProvider: FilmPlaybackUIKitTheme())
        let window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        try await waitFor { owner.preferences != nil && !owner.busy }
        await settle()
        let control = try await visibleSwitch("film.preferences.auto-prepare", page)
        XCTAssertTrue(control.isOn)
        control.isOn = false; control.sendActions(for: .valueChanged)
        XCTAssertEqual(owner.draft?.iosAutoPrepare, false)
        XCTAssertEqual(owner.preferences?.iosAutoPrepare, true)
        await store.setSaveFailure(true)
        try headerSave(page).sendActions(for: .touchUpInside)
        let failure = try await presentedAlert(page, title: "错误", style: .alert)
        XCTAssertEqual(owner.preferences?.iosAutoPrepare, true)
        XCTAssertEqual(owner.draft?.iosAutoPrepare, false)
        await store.setSaveFailure(false)
        try await runnerAction { $0.alerts.buttons["重试"].tap() }
        try await waitFor { owner.preferences?.iosAutoPrepare == false && !owner.busy }
        _ = try await presentedAlert(page, title: "已保存", style: .alert, after: failure)
        let calls = await store.calls()
        XCTAssertEqual(calls.map(\.operation), ["settings.get", "settings.set", "settings.set"])
    }

    func testManualEditorPreservesTypedZeroAndPrivateShareFieldsAndSupportsRemoval() async throws {
        var item = FilmManualMatch(targetID: "fixture-target", title: "人工线路")
        item.value["id"] = .string("manual-fixture")
        item.value["sourceKey"] = .string("source-key"); item.value["detailId"] = .number(0)
        item.value["seasonHint"] = .number(0); item.value["failureCount"] = .number(2)
        let store = FilmUIKitPlaybackSettingsStore(manual: [item.value])
        let owner = FilmPlaybackSettingsModel(transport: store.transport)
        try await owner.loadLibrary()
        let page = FilmManualMatchEditor(owner: owner, item: item, themeProvider: FilmPlaybackUIKitTheme())
        let window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        let enabled = try await visibleSwitch("film.manual.enabled", page)
        enabled.isOn = false; enabled.sendActions(for: .valueChanged)
        try headerSave(page).sendActions(for: .touchUpInside)
        try await waitFor { owner.library?.manual.first?.enabled == false && !owner.busy }
        let calls = await store.calls()
        let saved = try XCTUnwrap(calls.first { $0.operation == "manual.update" }?.input.filmObject?["item"]?.filmObject)
        XCTAssertEqual(saved["detailId"], .number(0)); XCTAssertEqual(saved["seasonHint"], .number(0))
        XCTAssertEqual(saved["failureCount"], .number(2)); XCTAssertEqual(saved["enabled"], .bool(false))
        try select("film.manual.remove", page)
        _ = try await presentedAlert(page, title: "删除人工匹配", style: .alert)
        try await runnerAction { $0.alerts.buttons["确认"].tap() }
        try await waitFor { owner.library?.manual.isEmpty == true && !owner.busy }

        var share = FilmManualMatch(targetID: "fixture-target", kind: "share")
        share.value["provider"] = .string("quark")
        share.value["share"] = .object(["url": .string("https://share.example.invalid/fixture"), "password": .string("fixture")])
        let editor = FilmManualMatchEditor(owner: owner, item: share, themeProvider: FilmPlaybackUIKitTheme())
        let editorWindow = try mount(editor)
        defer { unmount(editor, editorWindow) }
        try await visible(editor)
        editor.table.layoutIfNeeded()
        editor.table.scrollToRow(at: IndexPath(row: 4, section: 0), at: .top, animated: false)
        editor.table.layoutIfNeeded()
        XCTAssertTrue(try XCTUnwrap(view("film.manual.share", editor.view) as? UITextField).isSecureTextEntry)
        XCTAssertTrue(try XCTUnwrap(view("film.manual.password", editor.view) as? UITextField).isSecureTextEntry)
    }

    func testPreferenceUIReordersAllDimensionsAndOnlyRestoresTheSelectedGroup() async throws {
        let store = FilmUIKitPlaybackSettingsStore()
        let owner = FilmPlaybackSettingsModel(transport: store.transport)
        let page = FilmPlaybackPreferencesController(owner: owner, area: .preferences,
                                                     themeProvider: FilmPlaybackUIKitTheme())
        let window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        try await waitFor { owner.draft != nil && !owner.busy }
        owner.edit { $0.keywords = ["DDP", "H.265"]; $0.iosAutoPrepare = false }
        await settle()
        try select("film.preferences.dimension.pan", page)
        let ruleMenu = try await presentedAlert(page, title: "网盘", style: .actionSheet)
        try await runnerAction { $0.sheets.buttons["上移"].tap() }
        try await waitFor("preference.ruleOrder") {
            owner.draft?.ruleOrder == [.pan, .quality, .keyword] && page.presentedViewController == nil
        }
        try select("film.preferences.pan.quark", page)
        let panMenu = try await presentedAlert(page, title: "夸克网盘", style: .actionSheet, after: ruleMenu)
        try await runnerAction { $0.sheets.buttons["上移"].tap() }
        try await waitFor("preference.panOrder") {
            owner.draft?.panOrder == [.yidong, .quark, .tianyi, .uc, .baidu] && page.presentedViewController == nil
        }
        try select("film.preferences.keyword.1", page)
        let keywordMenu = try await presentedAlert(page, title: "H.265", style: .actionSheet, after: panMenu)
        try await runnerAction { $0.sheets.buttons["排序"].tap() }
        let keywordOrder = try await presentedAlert(page, title: "H.265", style: .actionSheet, after: keywordMenu)
        try await waitFor {
            (page.presentedViewController as? UIAlertController)?.actions.contains { $0.title == "上移" } == true
        }
        try await runnerAction { $0.sheets.buttons["上移"].tap() }
        try await waitFor { owner.draft?.keywords == ["H.265", "DDP"] && page.presentedViewController == nil }
        try select("film.preferences.restore.panOrder", page)
        await settle()
        XCTAssertEqual(owner.draft?.panOrder, FilmPlaybackPreferences.defaults.panOrder)
        XCTAssertEqual(owner.draft?.ruleOrder, [.pan, .quality, .keyword])
        XCTAssertEqual(owner.draft?.keywords, ["H.265", "DDP"])
        XCTAssertEqual(owner.draft?.iosAutoPrepare, false)
        XCTAssertEqual(owner.preferences, .defaults, "Editing and restore must not publish a durable save")
        try headerSave(page).sendActions(for: .touchUpInside)
        try await waitFor { owner.preferences?.keywords == ["H.265", "DDP"] && !owner.busy }
        let writes = await store.calls().filter { $0.operation == "settings.set" }
        XCTAssertEqual(writes.count, 1)
        XCTAssertEqual(writes[0].input, owner.draft?.value)
        _ = try await presentedAlert(page, title: "已保存", style: .alert, after: keywordOrder)
    }

    func testSiteCleanUICanRemoveAddSaveAndRestoreKeywordsWithoutChangingPlaybackOrder() async throws {
        let store = FilmUIKitPlaybackSettingsStore()
        let owner = FilmPlaybackSettingsModel(transport: store.transport)
        let page = FilmPlaybackPreferencesController(owner: owner, area: .siteClean,
                                                     themeProvider: FilmPlaybackUIKitTheme())
        let window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        try await waitFor { owner.draft != nil && !owner.busy }
        await settle()
        try select("film.preferences.keyword.0", page)
        let deletion = try await presentedAlert(page, title: "直播", style: .actionSheet)
        try await runnerAction { $0.sheets.buttons["删除"].tap() }
        try await waitFor("siteClean.firstDelete") {
            owner.draft?.siteCleanKeywords.contains("直播") == false && page.presentedViewController == nil
        }
        _ = try await visibleCell("film.preferences.keywords.add", page)
        try select("film.preferences.keywords.add", page)
        let alert = try await presentedAlert(page, title: "添加关键词", style: .alert, textFields: 1, after: deletion)
        let keyword = try XCTUnwrap(alert.textFields?.first)
        keyword.text = "预告"
        try await runnerAction { $0.alerts.buttons["确定"].tap() }
        try await waitFor { owner.draft?.siteCleanKeywords.last == "预告" && page.presentedViewController == nil }
        let draft = try XCTUnwrap(owner.draft)
        try headerSave(page).sendActions(for: .touchUpInside)
        try await waitFor { owner.preferences == draft && !owner.busy }
        _ = try await presentedAlert(page, title: "已保存", style: .alert, after: alert)
        try await runnerAction { $0.alerts.buttons["确定"].tap() }
        try await waitFor("The saved notice was acknowledged through its UI action") {
            page.presentedViewController == nil && page.canPresentPageAlert
        }
        try select("film.preferences.restore.siteClean", page)
        await settle()
        XCTAssertEqual(owner.draft?.siteCleanKeywords, FilmPlaybackPreferences.defaults.siteCleanKeywords)
        XCTAssertEqual(owner.draft?.ruleOrder, draft.ruleOrder)
        XCTAssertEqual(owner.draft?.panOrder, draft.panOrder)
        XCTAssertEqual(owner.preferences, draft, "Restoring the draft alone must not save")
    }

    func testHistoryAndBlocksRequireConfirmationAndUseExactOrBulkRemovalReceipts() async throws {
        let history: RuntimeValue = .object(["targetId": .string("history-target"), "title": .string("观看记录"),
            "position": .number(95), "duration": .number(600), "updatedAt": .number(1_700_000_000_000)])
        let block: RuntimeValue = .object(["targetId": .string("blocked-target"), "sourceKey": .string("source-key"),
            "detailId": .number(0), "lineId": .number(0)])
        let store = FilmUIKitPlaybackSettingsStore(history: [history], blocks: [block])
        let owner = FilmPlaybackSettingsModel(transport: store.transport)
        let blocks = FilmPlaybackLibraryController(owner: owner, area: .blocks,
                                                   themeProvider: FilmPlaybackUIKitTheme())
        let blockWindow = try mount(blocks)
        try await visible(blocks)
        try await waitFor { owner.library != nil && !owner.busy }
        try select("film.blocks.0", blocks)
        try await waitFor { blocks.presentedViewController is UIAlertController }
        var writes = await store.calls().filter { $0.operation == "library.remove" }
        XCTAssertTrue(writes.isEmpty)
        blocks.selectAlertChoice("取消")
        try await waitFor { blocks.presentedViewController == nil && blocks.canPresentPageAlert }
        XCTAssertEqual(owner.library?.blocks.count, 1)
        try select("film.blocks.0", blocks)
        try await waitFor { blocks.presentedViewController is UIAlertController }
        blocks.selectAlertChoice("确认")
        try await waitFor { owner.library?.blocks.isEmpty == true && !owner.busy }
        writes = await store.calls().filter { $0.operation == "library.remove" }
        XCTAssertEqual(writes.first?.input.filmObject?["block"], block)
        unmount(blocks, blockWindow)
        let page = FilmPlaybackLibraryController(owner: owner, area: .history,
                                                 themeProvider: FilmPlaybackUIKitTheme())
        let window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        try await waitFor { page.sections.first?.rows.contains { $0.id == "film.playback-library.clear" } == true && !owner.busy }
        try select("film.playback-library.clear", page)
        try await waitFor { page.presentedViewController is UIAlertController }
        page.selectAlertChoice("确认")
        try await waitFor { owner.library?.history.isEmpty == true && !owner.busy }
        writes = await store.calls().filter { $0.operation == "library.remove" }
        XCTAssertEqual(writes.last?.input, .object(["kind": .string("history")]))
        XCTAssertEqual(page.sections.first?.rows.map(\.title), ["暂无记录"])
    }

    private func blockSnapshot(_ id: String, current: Bool, sequence: Int = 1) throws -> RuntimeValue {
        var fields = try FilmUIKitPlaybackFixture.snapshot(sequence).filmRequiredObject()
        fields["stage"] = .string(current ? "playing" : "prepared")
        fields["attempt"] = .object([
            "id": .string(current ? "different-pending" : id), "prepareOnly": .bool(true),
            "candidate": FilmUIKitPlaybackFixture.candidate(), "position": .number(0), "paused": .bool(false),
        ])
        if current {
            fields["current"] = .object(["attemptId": .string(id), "candidate": FilmUIKitPlaybackFixture.candidate(),
                "position": .number(34), "duration": .number(600), "paused": .bool(false)])
        }
        return .object(fields)
    }
    private func blockPage(_ model: FilmPlaybackModel, player: Bool) throws -> PageViewController {
        if player { return FilmPlayerViewController(model: model, themeProvider: FilmPlaybackUIKitTheme()) }
        return try detailPage(model)
    }
    private func makeModel(_ server: FilmUIKitPlaybackServer, _ media: FilmUIKitPlaybackMedia,
                           opener: @escaping @MainActor (URL) async -> Bool = { _ in
                               XCTFail("Unexpected external-player launch"); return false
                           }) -> FilmPlaybackModel {
        let model = FilmPlaybackModel(transport: server.transport, media: media,
            openExternalURL: opener, request: { FilmUIKitPlaybackFixture.request() })
        addTeardownBlock { await model.close() }
        return model
    }
    private func detailPage(_ model: FilmPlaybackModel) throws -> FilmDetailViewController {
        let suite = "FilmPlaybackUIKitTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        addTeardownBlock { defaults.removePersistentDomain(forName: suite) }
        let images = ArtworkImages(maximumBytes: 1_024 * 1_024) { _ in
            XCTFail("This fixture contains no artwork requests"); throw URLError(.resourceUnavailable)
        }
        return FilmDetailViewController(detail: .init(item: .init(sourceID: "fixture", id: "19", title: "星海航线")),
            library: FilmLibrary(defaults: defaults), images: images, themeProvider: FilmPlaybackUIKitTheme(), model: model)
    }
    private func rowPath(_ id: String, _ page: FilmSettingsListController) -> IndexPath? {
        for (section, values) in page.sections.enumerated() {
            if let row = values.rows.firstIndex(where: { $0.id == id }) {
                return IndexPath(row: row, section: section)
            }
        }
        return nil
    }
    private func select(_ id: String, _ page: FilmSettingsListController) throws {
        let path = try XCTUnwrap(rowPath(id, page), "Missing row \(id)")
        page.tableView(page.table, didSelectRowAt: path)
    }
    private func visibleCell(_ id: String, _ page: FilmSettingsListController) async throws -> UITableViewCell {
        try await waitFor("Published row \(id)") { self.rowPath(id, page) != nil }
        let path = try XCTUnwrap(rowPath(id, page))
        page.view.layoutIfNeeded(); page.table.layoutIfNeeded()
        page.table.scrollToRow(at: path, at: .middle, animated: false)
        page.table.layoutIfNeeded()
        try await waitFor("Laid out visible cell \(id)") {
            page.table.layoutIfNeeded()
            guard let cell = page.table.cellForRow(at: path) else { return false }
            return cell.accessibilityIdentifier == id && cell.window === page.view.window && !cell.bounds.isEmpty
        }
        return try XCTUnwrap(page.table.cellForRow(at: path))
    }
    private func visibleSwitch(_ id: String, _ page: FilmSettingsListController) async throws -> UISwitch {
        _ = try await visibleCell(id, page)
        try await waitFor("Actual cell switch \(id)") {
            page.table.layoutIfNeeded()
            guard let path = self.rowPath(id, page), let cell = page.table.cellForRow(at: path),
                  let control = self.view(id + ".switch", cell) as? UISwitch else { return false }
            return control.window === page.view.window && !control.bounds.isEmpty
        }
        let path = try XCTUnwrap(rowPath(id, page))
        let cell = try XCTUnwrap(page.table.cellForRow(at: path))
        return try XCTUnwrap(view(id + ".switch", cell) as? UISwitch)
    }
    private func presentedAlert(_ page: PageViewController, title: String, style: UIAlertController.Style,
                                textFields: Int = 0, after previous: UIAlertController? = nil) async throws -> UIAlertController {
        try await waitFor("New \(title) presentation, style \(style.rawValue), text fields \(textFields)") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert !== previous && alert.title == title && alert.preferredStyle == style &&
                (alert.textFields?.count ?? 0) == textFields && alert.viewIfLoaded?.window != nil &&
                !alert.isBeingPresented && !alert.isBeingDismissed
        }
        return try XCTUnwrap(page.presentedViewController as? UIAlertController)
    }
    private func headerSave(_ page: UIViewController) throws -> UIButton {
        try XCTUnwrap(descendants(page.view).compactMap { $0 as? PageHeaderTextButton }.first {
            $0.title(for: .normal) == "保存"
        })
    }
    private func button(_ id: String, _ root: UIView) throws -> UIButton { try XCTUnwrap(view(id, root) as? UIButton, id) }
    private func view(_ id: String, _ root: UIView) -> UIView? {
        ([root] + descendants(root)).first { $0.accessibilityIdentifier == id }
    }
    private func descendants(_ root: UIView) -> [UIView] { root.subviews.flatMap { [$0] + descendants($0) } }
    private func menuAction(_ title: String, _ menu: UIMenu?) throws -> UIAction {
        try XCTUnwrap(menu?.children.compactMap { $0 as? UIAction }.first { $0.title == title }, title)
    }
    private func invoke(_ action: UIAction) {
        let control = UIButton(type: .system)
        control.addAction(action, for: .touchUpInside); control.sendActions(for: .touchUpInside)
    }
    private func mount(_ page: UIViewController) throws -> UIWindow {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first {
            $0.activationState == .foregroundActive
        }, "The activated runner must have a foreground scene")
        let previous = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        mountedWindows.append((page, window, previous))
        window.rootViewController = page; window.makeKeyAndVisible(); window.layoutIfNeeded()
        page.view.layoutIfNeeded()
        return window
    }
    private func unmount(_ page: UIViewController, _ window: UIWindow) {
        let index = mountedWindows.firstIndex { $0.window === window }!
        let previous = mountedWindows.remove(at: index).previousKeyWindow
        PageViewController.invalidatePages(in: page); window.isHidden = true; window.rootViewController = nil
        previous?.makeKey()
    }
    private func visible(_ page: PageViewController) async throws {
        try await waitFor("The page is visible in the real active key window") {
            UIApplication.shared.applicationState == .active &&
                page.view.window?.windowScene?.activationState == .foregroundActive &&
                page.view.window?.isKeyWindow == true && page.isPageVisible && page.canPresentPageAlert
        }
    }
    private func settle() async {
        await withCheckedContinuation { continuation in RunLoop.main.perform { continuation.resume() } }
    }
    private enum FixtureFailure: Error { case prerequisite(String) }
    private func waitFor(_ description: String = "UIKit prerequisite", file: StaticString = #filePath, line: UInt = #line,
                         _ condition: @escaping @MainActor () -> Bool) async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(4))
        while !condition(), ContinuousClock.now < deadline {
            await settle()
            try? await Task.sleep(for: .milliseconds(10))
        }
        let satisfied = condition()
        XCTAssertTrue(satisfied, description, file: file, line: line)
        if !satisfied { throw FixtureFailure.prerequisite(description) }
    }
    private func runnerAction(_ action: @escaping @MainActor (XCUIApplication) -> Void) async throws {
        let identifier = try XCTUnwrap(Bundle.main.bundleIdentifier)
        await withCheckedContinuation { continuation in
            RunLoop.main.perform {
                MainActor.assumeIsolated { action(XCUIApplication(bundleIdentifier: identifier)) }
                continuation.resume()
            }
        }
    }
}

@MainActor
final class FilmSettingsFeedbackUIKitTests: XCTestCase {
    override func setUp() async throws {
        try await super.setUp()
        try await runnerAction { $0.activate() }
        try await waitFor("Runner and scene are really foreground active") {
            UIApplication.shared.applicationState == .active &&
                UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive }
        }
    }

    func testImmediateRetrySuccessShowsSavedNoticeAfterActualErrorDismissal() async throws {
        let page = FilmSettingsListController(themeProvider: FilmPlaybackUIKitTheme())
        try mount(page)
        try await waitFor("Feedback page ready") { page.canPresentPageAlert }
        var retries = 0
        weak var original: UIAlertController?
        page.failure(FilmPlaybackError(code: "STORAGE_ERROR")) { [weak page] in
            guard let page else { return }
            retries += 1
            XCTAssertNil(page.presentedViewController)
            XCTAssertNil(original?.presentingViewController)
            XCTAssertNil(original?.viewIfLoaded?.window)
            XCTAssertFalse(original?.isBeingDismissed ?? false)
            XCTAssertTrue(page.canPresentPageAlert)
            page.notice("已保存", message: "立即重试成功")
        }
        let failure = try await alert("错误", page)
        original = failure
        XCTAssertEqual(retries, 0)
        try await runnerAction { $0.alerts.buttons["重试"].tap() }
        let saved = try await alert("已保存", page, after: failure)
        XCTAssertEqual(retries, 1)
        XCTAssertEqual(saved.message, "立即重试成功")
        XCTAssertEqual(saved.actions.map(\.title), ["确定"])
        try await runnerAction { $0.alerts.buttons["确定"].tap() }
        try await waitFor("Saved notice consumed once") {
            page.presentedViewController == nil && page.canPresentPageAlert
        }
        XCTAssertEqual(retries, 1)
    }

    func testPendingNoticesSurviveAppearanceAndActualAlertDismissalAndAreConsumedOnce() async throws {
        let page = FilmSettingsListController(themeProvider: FilmPlaybackUIKitTheme())
        page.notice("已保存", message: "首次结果")
        try mount(page)
        let saved = try await alert("已保存", page)
        XCTAssertEqual(saved.message, "首次结果")
        page.notice("已恢复默认", message: "等待前框退出")
        XCTAssertTrue(page.presentedViewController === saved)
        try await runnerAction { $0.alerts.buttons["确定"].tap() }
        let restored = try await alert("已恢复默认", page, after: saved)
        XCTAssertNil(saved.presentingViewController)
        XCTAssertNil(saved.viewIfLoaded?.window)
        XCTAssertFalse(saved.isBeingDismissed)
        XCTAssertEqual(restored.message, "等待前框退出")
        try await runnerAction { $0.alerts.buttons["确定"].tap() }
        try await waitFor("Both pending notices consumed once") {
            page.presentedViewController == nil && page.canPresentPageAlert
        }
    }

    func testInvalidatedPageDoesNotRetryOrPresentItsPendingNotice() async throws {
        let page = FilmSettingsListController(themeProvider: FilmPlaybackUIKitTheme())
        try mount(page)
        try await waitFor("Feedback page ready before invalidation") { page.canPresentPageAlert }
        var retries = 0
        page.failure(FilmPlaybackError(code: "STORAGE_ERROR")) { [weak page] in
            retries += 1
            page?.notice("不应显示")
        }
        let failure = try await alert("错误", page)
        page.notice("已保存", message: "等待旧框退出")
        PageViewController.invalidatePages(in: page)
        XCTAssertFalse(page.isPageActive)
        try await runnerAction { $0.alerts.buttons["重试"].tap() }
        try await waitFor("Invalidated page still dismisses the old alert") {
            page.presentedViewController == nil && failure.presentingViewController == nil &&
                failure.viewIfLoaded?.window == nil && !failure.isBeingDismissed
        }
        XCTAssertEqual(retries, 0)
        XCTAssertFalse(page.canPresentPageAlert)
        page.notice("已保存", message: "失效后到达")
        XCTAssertNil(page.presentedViewController)
    }

    private func mount(_ page: FilmSettingsListController) throws {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first {
            $0.activationState == .foregroundActive
        })
        let previous = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        addTeardownBlock {
            await MainActor.run {
                PageViewController.invalidatePages(in: page)
                page.dismiss(animated: false)
                window.isHidden = true; window.rootViewController = nil
                previous?.makeKey()
            }
        }
        window.rootViewController = page; window.makeKeyAndVisible(); window.layoutIfNeeded()
        page.view.layoutIfNeeded()
    }

    private func alert(_ title: String, _ page: FilmSettingsListController,
                       after previous: UIAlertController? = nil) async throws -> UIAlertController {
        try await waitFor("New feedback alert \(title)") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert !== previous && alert.title == title && alert.preferredStyle == .alert &&
                (alert.textFields?.count ?? 0) == 0 && alert.viewIfLoaded?.window != nil &&
                !alert.isBeingPresented && !alert.isBeingDismissed
        }
        return try XCTUnwrap(page.presentedViewController as? UIAlertController)
    }

    private func waitFor(_ description: String, file: StaticString = #filePath, line: UInt = #line,
                         _ condition: @escaping @MainActor () -> Bool) async throws {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { condition() } },
                                evaluatedWith: nil)
        let result = await XCTWaiter.fulfillment(of: [ready], timeout: 4)
        XCTAssertEqual(result, .completed, description, file: file, line: line)
        if result != .completed { throw FixtureFailure.prerequisite(description) }
    }
    private enum FixtureFailure: Error { case prerequisite(String) }

    private func runnerAction(_ action: @escaping @MainActor (XCUIApplication) -> Void) async throws {
        let identifier = try XCTUnwrap(Bundle.main.bundleIdentifier)
        await withCheckedContinuation { continuation in
            RunLoop.main.perform {
                MainActor.assumeIsolated { action(XCUIApplication(bundleIdentifier: identifier)) }
                continuation.resume()
            }
        }
    }
}

@MainActor
private final class FilmPlaybackUIKitTheme: PageThemeProviding {
    let pageColorway = PageColorway.allCases[0]
    let animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> { Empty(completeImmediately: false).eraseToAnyPublisher() }
}
#endif
