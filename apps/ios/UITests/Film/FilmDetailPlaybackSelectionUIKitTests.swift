#if os(iOS)
import XCTest
import UIKit
import Combine
import MeowUI
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmDetailPlaybackSelectionUIKitTests: XCTestCase {
    private var mountedWindows: [(page: UIViewController, window: UIWindow, previousKeyWindow: UIWindow?)] = []
    private let firstID = String(repeating: "a", count: 64)
    private let secondID = String(repeating: "b", count: 64)
    private let otherLineID = String(repeating: "c", count: 64)
    private let otherSourceID = String(repeating: "d", count: 64)
    private let seasonTwoID = String(repeating: "e", count: 64)

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

    func testPrimaryPlayKeepsExactSecondFromCurrentPreparedAttemptOrRawTarget() async throws {
        for proof in SelectionProof.allCases {
            var fields = try snapshot(kind: proof == .rawTarget ? .source : .movie)
            fields["current"] = current(proof == .current ? secondID : firstID)
            fields["stage"] = .string(proof == .rawTarget ? "resolving" : "playing")
            if proof == .attempt {
                fields["attempt"] = .object([
                    "id": .string("prepared-before-play"),
                    "candidate": candidate(secondID),
                    "prepareOnly": .bool(true), "position": .number(137), "paused": .bool(true),
                ])
                fields["stage"] = .string("prepared")
            }
            if proof == .rawTarget {
                var target = try fields.filmValue("target").filmRequiredObject()
                var source = try target.filmValue("source").filmRequiredObject()
                source["candidateId"] = .string(secondID)
                source["lineId"] = .number(0)
                target["source"] = .object(source); fields["target"] = .object(target)
            }
            let server = FilmUIKitPlaybackServer(.object(fields)), media = FilmUIKitPlaybackMedia()
            let model = try makeModel(server, media, fields: fields)
            await model.open()?.value
            let before = try XCTUnwrap(model.snapshot)
            let page = try detailPage(model), window = try mount(page)
            defer { unmount(page, window) }
            var presentations = 0
            page.onPresentPlayback = { presentations += 1 }
            try await visible(page)
            await model.waitForPendingWork()

            let initial = await server.calls()
            XCTAssertEqual(initial.map(\.operation), ["session.open", "session.command"], "\(proof)")
            XCTAssertEqual(initial.compactMap(\.action), ["browse.refresh"])
            XCTAssertEqual(initial.last?.input.filmObject?["refresh"], .bool(false))
            XCTAssertNotNil(find("film.file.second", page.view))
            let play = try button("film.detail.play", page.view)
            XCTAssertTrue(play.isEnabled)
            play.sendActions(for: .touchUpInside)
            await model.waitForPendingWork()

            let commands = await server.calls().filter { $0.operation == "session.command" }
            XCTAssertEqual(commands.compactMap(\.action), ["browse.refresh", "select.candidate"], "\(proof)")
            XCTAssertEqual(commands.last?.input.filmObject?["candidateId"], .string(secondID), "\(proof)")
            XCTAssertEqual(model.snapshot?.current, before.current, "The page must not manufacture a new current")
            XCTAssertEqual(model.snapshot?.target, before.target)
            XCTAssertTrue(media.inputs.isEmpty, "Prepared receipts remain with the existing model/owner")
            XCTAssertEqual(presentations, 1)
            await model.close()
        }
    }

    func testBrowsingAnotherTypedLineOrSourceKeepsCurrentAndPlaysThatRangesFirstPlayable() async throws {
        for destination in BrowseDestination.allCases {
            var fields = try snapshot(kind: destination == .line ? .source : .group)
            fields["current"] = current(secondID)
            fields["stage"] = .string("playing")
            var target = try fields.filmValue("target").filmRequiredObject()
            var source = try target.filmValue("source").filmRequiredObject()
            source["candidateId"] = .string(secondID)
            source["lineId"] = .number(0)
            target["source"] = .object(source); fields["target"] = .object(target)
            let server = FilmUIKitPlaybackServer(.object(fields)), media = FilmUIKitPlaybackMedia()
            let model = try makeModel(server, media, fields: fields)
            await model.open()?.value
            let before = try XCTUnwrap(model.snapshot)
            let page = try detailPage(model), window = try mount(page)
            defer { unmount(page, window) }
            var presentations = 0
            page.onPresentPlayback = { presentations += 1 }
            try await visible(page)
            await model.waitForPendingWork()

            let sourceID = destination == .line ? "result-a" : "result-other"
            let lineID: RuntimeValue = destination == .line ? .string("0") : .number(0)
            let expectedID = destination == .line ? otherLineID : otherSourceID
            var changed = fields
            changed["browsing"] = browsing(sourceID: sourceID, lineID: lineID, files: [
                file("unavailable", candidateID: nil), file("new-range", candidateID: expectedID),
            ])
            await server.reply(to: "browse", with: .object(changed))
            if destination == .line {
                invoke(try menuAction("另一线路", button("film.detail.line", page.view).menu))
            } else {
                invoke(try menuAction("测试站 · 另一详情", button("film.detail.source", page.view).menu))
            }
            await model.waitForPendingWork()
            try await waitFor("The browsed list is rendered") { self.find("film.file.new-range", page.view) != nil }
            let browsed = try XCTUnwrap(model.snapshot)
            XCTAssertEqual(browsed.browsing.sourceID, sourceID)
            XCTAssertEqual(browsed.browsing.lineID, destination == .line ? .string("0") : .number(0))
            XCTAssertEqual(browsed.current, before.current)
            XCTAssertEqual(browsed.target, before.target)
            XCTAssertEqual(browsed.current?.candidate.id, secondID)
            XCTAssertEqual(browsed.current?.position, 137)
            XCTAssertEqual(browsed.current?.paused, true)
            XCTAssertTrue(media.inputs.isEmpty)
            XCTAssertEqual(presentations, 0, "Browsing alone must not open the player")
            let browseCalls = await server.calls()
            XCTAssertEqual(browseCalls.map(\.operation), ["session.open", "session.command", "session.command"])
            XCTAssertEqual(browseCalls.compactMap(\.action), ["browse.refresh", "browse"])
            XCTAssertEqual(browseCalls.last?.action, "browse")
            XCTAssertEqual(browseCalls.last?.input.filmObject?["sourceId"], .string(sourceID))
            XCTAssertEqual(browseCalls.last?.input.filmObject?["lineId"], destination == .line ? lineID : nil)

            let play = try button("film.detail.play", page.view)
            XCTAssertTrue(play.isEnabled)
            play.sendActions(for: .touchUpInside)
            await model.waitForPendingWork()
            let commands = await server.calls().filter { $0.operation == "session.command" }
            XCTAssertEqual(commands.compactMap(\.action), ["browse.refresh", "browse", "select.candidate"])
            XCTAssertEqual(commands.last?.input.filmObject?["candidateId"], .string(expectedID))
            XCTAssertEqual(model.snapshot?.current, before.current)
            XCTAssertTrue(media.inputs.isEmpty)
            XCTAssertEqual(presentations, 1)
            await model.close()
        }
    }

    func testPrimaryPlayWithoutSelectionUsesFirstPlayableWithoutInventingMetadataEpisodes() async throws {
        var fields = try snapshot(kind: .source)
        fields["browsing"] = browsing(files: [
            file("unavailable", candidateID: nil), file("first", candidateID: firstID),
            file("second", candidateID: secondID),
        ])
        let server = FilmUIKitPlaybackServer(.object(fields)), media = FilmUIKitPlaybackMedia()
        let model = try makeModel(server, media, fields: fields)
        await model.open()?.value
        let page = try detailPage(model), window = try mount(page)
        defer { unmount(page, window) }
        var presentations = 0
        page.onPresentPlayback = { presentations += 1 }
        try await visible(page)
        await model.waitForPendingWork()

        let before = try XCTUnwrap(model.snapshot)
        XCTAssertNil(before.current); XCTAssertNil(before.attempt)
        XCTAssertNil(before.target.value["source"]?.filmObject?["candidateId"])
        XCTAssertNil(before.target.position.season); XCTAssertNil(before.target.position.episode)
        XCTAssertNil(before.target.position.globalEpisode)
        XCTAssertEqual(before.metadata, .null); XCTAssertTrue(before.episodes.isEmpty)
        XCTAssertTrue(page.detail.seasons.isEmpty)
        XCTAssertTrue(try button("film.detail.season", page.view).menu?.children.isEmpty == true)
        XCTAssertFalse(try button("film.file.unavailable", page.view).isEnabled)
        let play = try button("film.detail.play", page.view)
        XCTAssertTrue(play.isEnabled)
        play.sendActions(for: .touchUpInside)
        await model.waitForPendingWork()

        let calls = await server.calls()
        XCTAssertEqual(calls.map(\.operation), ["session.open", "session.command", "session.command"])
        XCTAssertEqual(calls.compactMap(\.action), ["browse.refresh", "select.candidate"])
        XCTAssertEqual(calls.last?.input.filmObject?["candidateId"], .string(firstID))
        XCTAssertNil(calls.last?.input.filmObject?["episode"])
        XCTAssertNil(model.snapshot?.current)
        XCTAssertTrue(media.inputs.isEmpty)
        XCTAssertEqual(presentations, 1)
        await model.close()
    }

    func testBrowsingSeasonTwoKeepsSeasonOneSecondEvenWhenDisplayedSeasonHasNoFiles() async throws {
        for hasSeasonTwoFile in [true, false] {
            let fields = try seasonSnapshot(hasSeasonTwoFile: hasSeasonTwoFile)
            let server = FilmUIKitPlaybackServer(.object(fields)), media = FilmUIKitPlaybackMedia()
            let model = try makeModel(server, media, fields: fields)
            await model.open()?.value
            let before = try XCTUnwrap(model.snapshot)
            let page = try detailPage(model), window = try mount(page)
            defer { unmount(page, window) }
            var presentations = 0
            page.onPresentPlayback = { presentations += 1 }
            try await visible(page)
            await model.waitForPendingWork()
            XCTAssertEqual(before.current?.candidate.position, FilmEpisodePosition(season: 1, episode: 2, globalEpisode: 2))
            XCTAssertEqual(before.target.position, .init(season: 1, episode: 2))

            var changed = fields
            var browsing = try changed.filmValue("browsing").filmRequiredObject()
            browsing["season"] = .number(2)
            browsing["episodes"] = .array(before.browsing.originalFiles.filter {
                $0.position.season == 2
            }.map { file($0.id, candidateID: $0.candidateID, position: $0.position) })
            changed["browsing"] = .object(browsing)
            changed["episodes"] = .array((1...3).map {
                seasonEpisode(season: 2, episode: $0, globalEpisode: 2 + $0)
            })
            await server.reply(to: "browse.season", with: .object(changed))
            invoke(try menuAction("第二季", button("film.detail.season", page.view).menu))
            await model.waitForPendingWork()
            try await waitFor("Season two is displayed without selecting an episode") {
                (self.find("film.detail.season", page.view) as? UIButton)?.title(for: .normal) == "TMDB · 第二季"
            }

            let browsed = try XCTUnwrap(model.snapshot)
            XCTAssertEqual(browsed.browsing.sourceID, before.browsing.sourceID)
            XCTAssertEqual(browsed.browsing.lineID, before.browsing.lineID)
            XCTAssertEqual(browsed.browsing.season, 2)
            XCTAssertEqual(browsed.current, before.current)
            XCTAssertEqual(browsed.target, before.target)
            XCTAssertEqual(browsed.browsing.originalFiles, before.browsing.originalFiles)
            XCTAssertEqual(browsed.browsing.files.compactMap(\.candidateID), hasSeasonTwoFile ? [seasonTwoID] : [])
            XCTAssertFalse(browsed.browsing.files.contains { $0.candidateID == secondID })
            XCTAssertTrue(browsed.browsing.originalFiles.contains { $0.candidateID == secondID })
            XCTAssertEqual(find("film.file.season-two-first", page.view) != nil, hasSeasonTwoFile)
            XCTAssertNil(find("film.file.second", page.view))
            XCTAssertTrue(media.inputs.isEmpty)
            XCTAssertEqual(presentations, 0)
            let browseCalls = await server.calls()
            XCTAssertEqual(browseCalls.map(\.operation), ["session.open", "session.command", "session.command"])
            XCTAssertEqual(browseCalls.compactMap(\.action), ["browse.refresh", "browse.season"])
            XCTAssertEqual(browseCalls.last?.input.filmObject?["season"], .number(2))

            let play = try button("film.detail.play", page.view)
            XCTAssertTrue(play.isEnabled, "An empty displayed season must not hide the exact current in this line")
            play.sendActions(for: .touchUpInside)
            await model.waitForPendingWork()

            let commands = await server.calls().filter { $0.operation == "session.command" }
            XCTAssertEqual(commands.compactMap(\.action), ["browse.refresh", "browse.season", "select.candidate"])
            XCTAssertEqual(commands.last?.input.filmObject?["candidateId"], .string(secondID))
            XCTAssertEqual(model.snapshot?.current, before.current)
            XCTAssertEqual(model.snapshot?.target, before.target)
            XCTAssertEqual(model.snapshot?.current?.position, 137)
            XCTAssertEqual(model.snapshot?.current?.paused, true)
            XCTAssertTrue(media.inputs.isEmpty)
            XCTAssertEqual(presentations, 1)
            await model.close()
        }
    }

    func testOriginalLineDoesNotAuthorizeUnselectedOrUnavailableCandidates() async throws {
        for unavailable in OriginalLineDenial.allCases {
            var fields = try seasonSnapshot(hasSeasonTwoFile: false)
            var browsing = try fields.filmValue("browsing").filmRequiredObject()
            browsing["season"] = .number(2); browsing["episodes"] = .array([])
            switch unavailable {
            case .unselected:
                fields["current"] = .null; fields["stage"] = .string("idle")
            case .unselectable:
                fields["candidates"] = .array([candidate(firstID, position: .init(season: 1, episode: 1, globalEpisode: 1))])
                browsing["originalEpisodes"] = .array([
                    file("first", candidateID: firstID, position: .init(season: 1, episode: 1, globalEpisode: 1)),
                    file("second", candidateID: nil, position: .init(season: 1, episode: 2, globalEpisode: 2)),
                ])
            case .sourceError:
                browsing["error"] = .string("STALE_SOURCE")
            }
            fields["browsing"] = .object(browsing)
            fields["episodes"] = .array([])
            let server = FilmUIKitPlaybackServer(.object(fields)), media = FilmUIKitPlaybackMedia()
            let model = try makeModel(server, media, fields: fields)
            await model.open()?.value
            let before = try XCTUnwrap(model.snapshot)
            let page = try detailPage(model), window = try mount(page)
            defer { unmount(page, window) }
            var presentations = 0
            page.onPresentPlayback = { presentations += 1 }
            try await visible(page)
            await model.waitForPendingWork()

            XCTAssertFalse(before.previewEpisodes)
            XCTAssertTrue(before.browsing.files.isEmpty)
            let play = try button("film.detail.play", page.view)
            XCTAssertFalse(play.isEnabled, "\(unavailable)")
            play.sendActions(for: .touchUpInside)
            await model.waitForPendingWork()
            let calls = await server.calls()
            XCTAssertEqual(calls.map(\.operation), ["session.open", "session.command"], "\(unavailable)")
            XCTAssertEqual(calls.compactMap(\.action), ["browse.refresh"])
            XCTAssertEqual(model.snapshot?.current, before.current)
            XCTAssertEqual(model.snapshot?.target, before.target)
            XCTAssertTrue(media.inputs.isEmpty)
            XCTAssertEqual(presentations, 0)
            await model.close()
        }
    }

    func testBusinessRejectionOfPreservedSelectionDoesNotFallbackOrStartNativeMedia() async throws {
        var fields = try seasonSnapshot(hasSeasonTwoFile: false)
        var browsing = try fields.filmValue("browsing").filmRequiredObject()
        browsing["season"] = .number(2); browsing["episodes"] = .array([])
        fields["browsing"] = .object(browsing); fields["episodes"] = .array([])
        let server = FilmUIKitPlaybackServer(.object(fields)), media = FilmUIKitPlaybackMedia()
        let model = try makeModel(server, media, fields: fields)
        await model.open()?.value
        let before = try XCTUnwrap(model.snapshot)
        let page = try detailPage(model), window = try mount(page)
        defer { unmount(page, window) }
        try await visible(page)
        await model.waitForPendingWork()
        var rejected = fields
        rejected["stage"] = .string("unavailable"); rejected["error"] = .string("NO_MATCH")
        await server.reply(to: "select.candidate", with: .object(rejected))

        let play = try button("film.detail.play", page.view)
        XCTAssertTrue(play.isEnabled)
        play.sendActions(for: .touchUpInside)
        await model.waitForPendingWork()

        let calls = await server.calls()
        XCTAssertEqual(calls.map(\.operation), ["session.open", "session.command", "session.command"])
        XCTAssertEqual(calls.compactMap(\.action), ["browse.refresh", "select.candidate"])
        XCTAssertEqual(calls.last?.input.filmObject?["candidateId"], .string(secondID))
        XCTAssertEqual(model.snapshot?.stage, "unavailable")
        XCTAssertEqual(model.snapshot?.error, "NO_MATCH")
        XCTAssertNil(model.snapshot?.attempt)
        XCTAssertEqual(model.snapshot?.current, before.current)
        XCTAssertEqual(model.snapshot?.target, before.target)
        XCTAssertFalse(try XCTUnwrap(model.snapshot).previewEpisodes)
        XCTAssertTrue(media.inputs.isEmpty, "The original line is not a media-output or authorization receipt")
        await model.close()
    }

    private enum SelectionProof: CaseIterable { case current, attempt, rawTarget }
    private enum BrowseDestination: CaseIterable { case line, source }
    private enum OriginalLineDenial: CaseIterable { case unselected, unselectable, sourceError }

    private func seasonSnapshot(hasSeasonTwoFile: Bool) throws -> RuntimeObject {
        var fields = try FilmUIKitPlaybackFixture.snapshot(kind: .tv).filmRequiredObject()
        let first = FilmEpisodePosition(season: 1, episode: 1, globalEpisode: 1)
        let second = FilmEpisodePosition(season: 1, episode: 2, globalEpisode: 2)
        let next = FilmEpisodePosition(season: 2, episode: 1, globalEpisode: 3)
        let seasonOne = [file("first", candidateID: firstID, position: first),
                         file("second", candidateID: secondID, position: second)]
        let original = seasonOne + (hasSeasonTwoFile
            ? [file("season-two-first", candidateID: seasonTwoID, position: next)] : [])
        fields["browsing"] = browsing(files: seasonOne, original: original, season: 1)
        fields["current"] = current(secondID, episode: second); fields["stage"] = .string("playing")
        fields["sources"] = .array([.object([
            "id": .string("result-a"), "title": .string("星海航线"), "source": FilmUIKitPlaybackFixture.source,
            "state": .string("ready"), "error": .null,
        ])])
        var candidates = [candidate(firstID, position: first), candidate(secondID, position: second)]
        if hasSeasonTwoFile { candidates.append(candidate(seasonTwoID, position: next)) }
        fields["candidates"] = .array(candidates)
        let seasonOneEpisodes = (1...2).map { seasonEpisode(season: 1, episode: $0, globalEpisode: $0) }
        let seasonTwoEpisodes = (1...3).map { seasonEpisode(season: 2, episode: $0, globalEpisode: 2 + $0) }
        fields["episodes"] = .array(seasonOneEpisodes)
        fields["metadata"] = .object([
            "name": .string("星海航线"), "status": .string("Ended"),
            "seasons": .array([
                .object(["id": .number(1), "season_number": .number(1), "name": .string("第一季"),
                         "episode_count": .number(2), "episodes": .array(seasonOneEpisodes)]),
                .object(["id": .number(2), "season_number": .number(2), "name": .string("第二季"),
                         "episode_count": .number(3), "episodes": .array(seasonTwoEpisodes)]),
            ]),
        ])
        return fields
    }
    private func seasonEpisode(season: Int, episode: Int, globalEpisode: Int) -> RuntimeValue {
        .object(["season": .number(Double(season)), "episode": .number(Double(episode)),
                 "season_number": .number(Double(season)), "episode_number": .number(Double(episode)),
                 "globalEpisode": .number(Double(globalEpisode)), "air_date": .string("2020-01-01"),
                 "name": .string("真实第 \(episode) 集")])
    }

    private func snapshot(kind: FilmPlaybackKind) throws -> RuntimeObject {
        var fields = try FilmUIKitPlaybackFixture.snapshot(kind: kind).filmRequiredObject()
        if kind == .source || kind == .group {
            var target: RuntimeObject = [
                "id": .string("raw-exact-target"), "kind": .string(kind.rawValue), "title": .string("星海航线"),
                "source": .object(["key": .string("source-key"), "detailId": .number(0)]),
            ]
            if kind == .group {
                target["members"] = .array([
                    .object(["sourceKey": .string("source-key"), "detailId": .number(0)]),
                    .object(["sourceKey": .string("another-source-key"), "detailId": .string("0")]),
                ])
            }
            fields["target"] = .object(target)
            fields["metadata"] = .null; fields["metadataSources"] = .array([])
            fields["metadataSourceId"] = .null
        } else {
            fields["metadata"] = .object(["title": .string("星海航线")])
            fields["metadataSources"] = .array([.object([
                "id": .string("confirmed-tmdb"), "provider": .string("tmdb"), "metadataId": .number(19),
                "kind": .string("movie"), "title": .string("星海航线"),
            ])])
        }
        fields["episodes"] = .array([]); fields["stage"] = .string("idle")
        var sources: [RuntimeValue] = [
            .object(["id": .string("result-a"), "title": .string("星海航线"),
                     "source": FilmUIKitPlaybackFixture.source, "state": .string("ready"), "error": .null]),
        ]
        if kind != .source {
            sources.append(.object(["id": .string("result-other"), "title": .string("另一详情"),
                "source": try otherSource(), "state": .string("ready"), "error": .null]))
        }
        fields["sources"] = .array(sources)
        fields["browsing"] = browsing(files: [
            file("first", candidateID: firstID), file("second", candidateID: secondID),
        ])
        var candidates = [candidate(firstID), candidate(secondID), candidate(otherLineID, lineID: .string("0"))]
        if kind != .source { candidates.append(candidate(otherSourceID, source: try otherSource())) }
        fields["candidates"] = .array(candidates)
        return fields
    }
    private func otherSource() throws -> RuntimeValue {
        var fields = try FilmUIKitPlaybackFixture.source.filmRequiredObject()
        fields["key"] = .string("another-source-key"); fields["siteId"] = .string("another-site")
        fields["detailId"] = .string("0")
        return .object(fields)
    }
    private func candidate(_ id: String, source: RuntimeValue = FilmUIKitPlaybackFixture.source,
                           lineID: RuntimeValue = .number(0), position: FilmEpisodePosition = .init()) -> RuntimeValue {
        var fields: RuntimeObject = ["id": .string(id), "source": source, "lineId": lineID,
                                     "lineLabel": .string("原标签"), "name": .string("同名正片")]
        for (key, value) in position.value { fields[key] = value }
        return .object(fields)
    }
    private func current(_ id: String, episode: FilmEpisodePosition = .init()) -> RuntimeValue {
        .object(["attemptId": .string("still-playing"), "candidate": candidate(id, position: episode),
                 "position": .number(137), "duration": .number(900), "paused": .bool(true)])
    }
    private func file(_ id: String, candidateID: String?, position: FilmEpisodePosition = .init()) -> RuntimeValue {
        var fields: RuntimeObject = ["id": .string(id), "name": .string("同名正片")]
        fields["candidateId"] = candidateID.map(RuntimeValue.string)
        for (key, value) in position.value { fields[key] = value }
        return .object(fields)
    }
    private func browsing(sourceID: String = "result-a", lineID: RuntimeValue = .number(0),
                          files: [RuntimeValue], original: [RuntimeValue]? = nil, season: Int? = nil) -> RuntimeValue {
        var fields: RuntimeObject = [
            "sourceId": .string(sourceID), "lineId": lineID, "loading": .bool(false), "error": .null,
            "lines": .array([.object(["id": .number(0), "label": .string("原线路")]),
                             .object(["id": .string("0"), "label": .string("另一线路")])]),
            "episodes": .array(files), "originalEpisodes": .array(original ?? files),
        ]
        fields["season"] = season.map { .number(Double($0)) }
        return .object(fields)
    }
    private func makeModel(_ server: FilmUIKitPlaybackServer, _ media: FilmUIKitPlaybackMedia,
                           fields: RuntimeObject) throws -> FilmPlaybackModel {
        let target = try FilmPlaybackTarget(fields.filmValue("target"))
        var sources = [try FilmPlaybackSource(FilmUIKitPlaybackFixture.source)]
        if target.kind != .source { sources.append(try FilmPlaybackSource(otherSource())) }
        let request = FilmPlaybackRequest(target: target, sources: sources)
        let model = FilmPlaybackModel(transport: server.transport, media: media,
            openExternalURL: { _ in XCTFail("Unexpected external-player launch"); return false },
            request: { request })
        addTeardownBlock { await model.close() }
        return model
    }
    private func detailPage(_ model: FilmPlaybackModel) throws -> FilmDetailViewController {
        let suite = "FilmDetailPlaybackSelectionUIKitTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        addTeardownBlock { defaults.removePersistentDomain(forName: suite) }
        let images = ArtworkImages(maximumBytes: 1_024 * 1_024) { _ in
            XCTFail("This fixture contains no artwork"); throw URLError(.resourceUnavailable)
        }
        return FilmDetailViewController(detail: .init(item: .init(sourceID: "fixture", id: "19", title: "星海航线")),
            library: FilmLibrary(defaults: defaults), images: images,
            themeProvider: FilmDetailSelectionTheme(), model: model)
    }
    private func button(_ id: String, _ root: UIView) throws -> UIButton {
        try XCTUnwrap(find(id, root) as? UIButton, id)
    }
    private func find(_ id: String, _ root: UIView) -> UIView? {
        if root.accessibilityIdentifier == id { return root }
        for child in root.subviews {
            if let match = find(id, child) { return match }
        }
        return nil
    }
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
        try await waitFor("The detail page is visible in the real active key window") {
            UIApplication.shared.applicationState == .active &&
                page.view.window?.windowScene?.activationState == .foregroundActive &&
                page.view.window?.isKeyWindow == true && page.isPageVisible && page.canPresentPageAlert
        }
    }
    private enum FixtureFailure: Error { case prerequisite(String) }
    private func waitFor(_ description: String, file: StaticString = #filePath, line: UInt = #line,
                         _ predicate: @escaping @MainActor () -> Bool) async throws {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } },
                                evaluatedWith: nil)
        let result = await XCTWaiter.fulfillment(of: [ready], timeout: 4)
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.map { scene in
            "activation=\(scene.activationState.rawValue), windows=\(scene.windows.map { "key=\($0.isKeyWindow), hidden=\($0.isHidden)" })"
        }
        XCTAssertEqual(result, .completed,
                       "\(description); application=\(UIApplication.shared.applicationState.rawValue), scenes=\(scenes)",
                       file: file, line: line)
        if result != .completed { throw FixtureFailure.prerequisite(description) }
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
private final class FilmDetailSelectionTheme: PageThemeProviding {
    let pageColorway = PageColorway.allCases[0]
    let animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> { Empty(completeImmediately: false).eraseToAnyPublisher() }
}
#endif
