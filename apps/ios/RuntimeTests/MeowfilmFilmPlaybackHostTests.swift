import XCTest
import UIKit
import Combine
import MeowContracts
import MeowPlayback
import MeowRuntime
@testable import MeowFilmFeature
@testable import Meowfilm

/// Uses the production App assembly, Node Mobile, Cat worker and video lease.
/// Only upstream source/metadata, a synthetic music tone and the small movie are fixtures.
@MainActor
final class MeowfilmFilmPlaybackHostTests: XCTestCase {
    func testAutomaticFirstEpisodePreparationDoesNotTakeMusicAndFormalPlaybackUsesTheRealOwner() async throws {
        try await exercisePlayback(autoPrepare: true)
    }

    func testDisabledPreparationBrowsesOnDemandAndReturnsFromBackgroundOnlyAfterExplicitPlay() async throws {
        try await exercisePlayback(autoPrepare: false)
    }

    func testPreparedHLSReceiptReadsUTF8BOMManifestAndRewrittenBinaryResourcesThroughNodeMobile() async throws {
        try await exerciseManifestReceipt(format: "hls", invalidUTF8: false)
    }

    func testPreparedDASHReceiptReadsUTF8BOMManifestAndRewrittenBinaryResourcesThroughNodeMobile() async throws {
        try await exerciseManifestReceipt(format: "dash", invalidUTF8: false)
    }

    func testPreparedHLSReceiptRejectsInvalidUTF8WithoutReadingBinaryResourcesThroughNodeMobile() async throws {
        try await exerciseManifestReceipt(format: "hls", invalidUTF8: true)
    }

    func testPreparedDASHReceiptRejectsInvalidUTF8WithoutReadingBinaryResourcesThroughNodeMobile() async throws {
        try await exerciseManifestReceipt(format: "dash", invalidUTF8: true)
    }

    func testExplicitSecondEpisodeSurvivesTheRealDataOwnerAndReentryWhileFreshTargetsIgnoreItsHistory() async throws {
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let client = try await resources.scriptSession.connection()
        let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
        let fixture = FilmPlaybackHostFixture(assembly: assembly, client: client, movie: movie)
        addTeardownBlock { try await fixture.close() }
        try await fixture.install(autoPrepare: true)
        let selected = FilmEpisodePosition(season: 1, episode: 2, globalEpisode: 2)
        let model = try await fixture.makeModel(position: selected)
        await model.open()?.value
        try await waitForPreparation(model, autoPrepare: true)
        XCTAssertEqual(model.snapshot?.target.position, selected)
        XCTAssertEqual(model.snapshot?.attempt?.candidate.position, selected)
        XCTAssertNil(assembly.playback.currentAttempt)
        let preparedHistory = try await fixture.history()
        XCTAssertTrue(preparedHistory.isEmpty)
        try await fixture.mountSurface(model)
        await model.send("play")?.value
        try await waitForHost {
            model.mediaState.attemptID != nil && model.mediaState.position > 0.1 &&
                model.snapshot?.current?.attemptID == model.mediaState.attemptID
        }
        model.pauseOrResume()
        try await waitForHost { model.mediaState.paused }
        await model.waitForPendingWork()
        let firstSession = try XCTUnwrap(model.sessionID)
        let played = try await fixture.sourceState(waitFor: [
            "report": .object(["event": .string("started"), "id": .string("two"), "flag": .string("原始线路")]),
        ])
        XCTAssertEqual(played["missingCookie"], .number(0))
        await model.close()
        XCTAssertTrue(model.isReleased)
        let history = try await fixture.history()
        XCTAssertEqual(history.count, 1)
        XCTAssertEqual(history.first?.filmObject?["episode"],
                       .object(["season": .number(1), "episode": .number(2), "globalEpisode": .number(2)]))
        let stopped = try await fixture.sourceState()
        let reads = stopped["mediaReads"]
        await model.open()?.value
        try await waitForPreparation(model, autoPrepare: true, previousSessionID: firstSession)
        await model.waitForPendingWork()
        XCTAssertEqual(model.snapshot?.target.position, selected, "Reentry keeps the explicit target, not the initial default")
        XCTAssertNil(assembly.playback.currentAttempt)
        XCTAssertNil(model.mediaState.attemptID)
        let reopened = try await fixture.sourceState()
        XCTAssertEqual(reopened["mediaReads"], reads)
        await model.close()
        let fresh = try await fixture.makeModel()
        await fresh.open()?.value
        try await waitForPreparation(fresh, autoPrepare: true)
        XCTAssertEqual(fresh.snapshot?.target.position, .init(season: 1, episode: 1, globalEpisode: 1),
                       "A new coordinate-free card starts at the first regular episode, ignoring iOS viewing history")
        XCTAssertEqual(fresh.snapshot?.attempt?.position, 0)
        XCTAssertNil(assembly.playback.currentAttempt)
        let freshState = try await fixture.sourceState()
        XCTAssertEqual(freshState["mediaReads"], reads)
        let unchangedHistory = try await fixture.history()
        XCTAssertEqual(unchangedHistory, history, "Preparation is neither playback nor a history rewrite")
        await fresh.close()
        XCTAssertTrue(fresh.isReleased)
    }

    private func exercisePlayback(autoPrepare: Bool) async throws {
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let client = try await resources.scriptSession.connection()
        let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
        let fixture = FilmPlaybackHostFixture(assembly: assembly, client: client, movie: movie)
        addTeardownBlock { try await fixture.close() }
        try await fixture.install(autoPrepare: autoPrepare)
        let model = try await fixture.makeModel()
        let music = assembly.playback.playAttempt(
            resource: try fixture.makeMusicResource(), owner: "music", autoplay: true)
        fixture.music = music
        try await waitForHost {
            assembly.playback.currentAttempt == music && assembly.playback.wantsPlayback &&
                assembly.playback.snapshot.phase == .playing && assembly.playback.snapshot.position > 0.1
        }

        await model.open()?.value
        try await waitForPreparation(model, autoPrepare: autoPrepare)
        XCTAssertEqual(model.snapshot?.target.position, .init(season: 1, episode: 1, globalEpisode: 1),
                       "A new coordinate-free card starts at the real first regular episode")
        XCTAssertEqual(assembly.playback.currentAttempt, music)
        XCTAssertTrue(assembly.playback.wantsPlayback)
        let preparedMusicPosition = assembly.playback.snapshot.position
        try await waitForHost {
            assembly.playback.currentAttempt == music && assembly.playback.wantsPlayback &&
                assembly.playback.snapshot.position > preparedMusicPosition + 0.1
        }
        XCTAssertNil(model.mediaState.attemptID)
        XCTAssertFalse(model.hasSystemOwner)
        let preparedHistory = try await fixture.history()
        XCTAssertTrue(preparedHistory.isEmpty)
        let prepared = try await fixture.sourceState()
        XCTAssertEqual(prepared["mediaReads"], .number(0))
        XCTAssertEqual(prepared["reports"], .array([]))
        XCTAssertEqual(operations(prepared), autoPrepare ? ["search", "detail", "play"] : [])

        if !autoPrepare {
            // Reading/rendering the menu does not run discovery. Explicit more
            // searches only; selecting one result is what admits its detail.
            XCTAssertTrue(try XCTUnwrap(model.snapshot).sources.isEmpty)
            await model.send("search.more")?.value
            try await waitForHost { model.snapshot?.searchComplete == true }
            let searched = try await fixture.sourceState()
            XCTAssertEqual(operations(searched), ["search"])
            let source = try XCTUnwrap(model.snapshot?.sources.first)
            await model.send("browse", parameters: ["sourceId": .string(source.id)])?.value
            try await waitForHost {
                guard let browsing = model.snapshot?.browsing else { return false }
                return browsing.sourceID == source.id && !browsing.loading && browsing.error == nil &&
                    browsing.lines.count == 3
            }
            let browsed = try await fixture.sourceState()
            XCTAssertEqual(operations(browsed), ["search", "detail"])
            let line = try XCTUnwrap(model.snapshot?.browsing.lines.first { $0.label == "备用原始线路" })
            await model.send("browse", parameters: ["sourceId": .string(source.id), "lineId": line.id.value])?.value
            try await waitForHost {
                guard let browsing = model.snapshot?.browsing else { return false }
                return browsing.sourceID == source.id && browsing.lineID == line.id &&
                    !browsing.loading && browsing.error == nil && browsing.files.count == 2 &&
                    browsing.files.allSatisfy { $0.candidateID != nil }
            }
            let selected = try XCTUnwrap(model.snapshot)
            XCTAssertEqual(selected.browsing.files.map(\.name), ["第1集.1080p", "第2集.1080p"])
            XCTAssertEqual(selected.browsing.files.map(\.position), [
                .init(season: 1, episode: 1, globalEpisode: 1),
                .init(season: 1, episode: 2, globalEpisode: 2),
            ])
            for file in selected.browsing.files {
                let candidateID = try XCTUnwrap(file.candidateID)
                XCTAssertFalse(candidateID.isEmpty)
                let candidate = try XCTUnwrap(selected.candidates.first { $0.id == candidateID })
                XCTAssertEqual(candidate.source.key, source.source.key)
                XCTAssertEqual(candidate.lineID, line.id)
                XCTAssertEqual(candidate.lineLabel, "备用原始线路")
                XCTAssertEqual(candidate.position, file.position)
            }
            let selectedLine = try await fixture.sourceState()
            XCTAssertEqual(operations(selectedLine), ["search", "detail"],
                           "a complete detail line is not fetched/listed again")
        }

        try await fixture.mountSurface(model)
        await model.send("play")?.value
        try await waitForHost {
            model.mediaState.attemptID != nil && model.mediaState.position > 0.1 &&
                model.snapshot?.current?.attemptID == model.mediaState.attemptID
        }
        let first = try XCTUnwrap(model.mediaState.attemptID)
        XCTAssertEqual(assembly.playback.currentAttempt?.owner, "film")
        XCTAssertNil(assembly.playback.player.currentItem, "film does not create an AVPlayer bypass")
        XCTAssertGreaterThan(model.mediaState.duration, 2)
        model.pauseOrResume()
        try await waitForHost { model.mediaState.paused }
        model.seek(to: 1.1)
        try await waitForHost { abs(model.mediaState.position - 1.1) < 0.15 && model.mediaState.paused }
        await model.waitForPendingWork()
        let playing: RuntimeObject
        do {
            playing = try await fixture.sourceState(waitFor: [
                "report": .object(["event": .string("started"), "id": .string("one"), "flag": .string("原始线路")]),
            ])
        } catch {
            XCTFail("The source did not confirm started; reportError=\(model.snapshot?.reportError ?? "none")")
            throw error
        }
        XCTAssertGreaterThan(try XCTUnwrap(playing["mediaReads"]?.filmNumber), 0)
        XCTAssertEqual(playing["missingCookie"], .number(0), "the first binary request must already carry the source header")
        XCTAssertTrue(playing["reports"]?.filmArray?.contains {
            $0.filmObject?["event"] == .string("started") &&
                $0.filmObject?["id"] == .string("one") && $0.filmObject?["flag"] == .string("原始线路")
        } == true)
        XCTAssertNil(model.snapshot?.reportError)
        let watchedHistory = try await fixture.history()
        XCTAssertEqual(watchedHistory.count, 1)

        if autoPrepare {
            // Both candidates are actual native media. The replacement must
            // retain the old pause intent and position, not its attempt identity.
            await model.send("switch")?.value
            try await waitForHost {
                model.mediaState.attemptID != nil && model.mediaState.attemptID != first &&
                    model.snapshot?.current?.attemptID == model.mediaState.attemptID
            }
            XCTAssertTrue(model.mediaState.paused)
            XCTAssertEqual(model.mediaState.position, 1.1, accuracy: 0.15)
            let healthy = model.mediaState.attemptID
            let donor = assembly.playback.currentAttempt
            let broken = try XCTUnwrap(model.snapshot?.candidates.first {
                $0.lineLabel == "故障线路" && $0.position.episode == 1
            })
            XCTAssertEqual(broken.quality?.lowercased(), "720p")
            XCTAssertNil(model.actionError)
            let admitted = expectation(description: "The broken candidate has a formal native attempt")
            let failed = expectation(description: "That native input reports its real open failure")
            var brokenAttempt: FilmPlaybackAttempt?
            var nativeFailure: FilmPlaybackError?
            let admission = model.$snapshot.compactMap { $0?.attempt }.first {
                !$0.prepareOnly && $0.candidate.id == broken.id
            }.sink { attempt in
                brokenAttempt = attempt
                admitted.fulfill()
            }
            let failure = model.$actionError.compactMap { $0 }.first {
                brokenAttempt != nil && $0.code == "MEDIA_READ_FAILED"
            }.sink { error in
                nativeFailure = error
                failed.fulfill()
            }
            defer { admission.cancel(); failure.cancel() }
            await model.send("quality", parameters: ["quality": .string("720p")])?.value
            let brokenState = try await fixture.sourceState(waitFor: [
                "play": .object(["id": .string("broken"), "flag": .string("故障线路")]),
                "binary": .object(["path": .string("/proxy/broken.mp4"), "method": .string("GET"), "bytes": .number(4)]),
            ])
            await fulfillment(of: [admitted, failed], timeout: 20)
            let rejected = try XCTUnwrap(brokenAttempt)
            XCTAssertNotEqual(rejected.id, healthy)
            XCTAssertEqual(rejected.candidate.id, broken.id)
            XCTAssertEqual(nativeFailure?.code, "MEDIA_READ_FAILED")
            try await waitForHost {
                model.snapshot?.stage == "unavailable" && model.snapshot?.error == "NO_MATCH" &&
                    model.snapshot?.attempt == nil && model.snapshot?.current?.attemptID == healthy
            }
            XCTAssertEqual(brokenState["missingCookie"], .number(0))
            XCTAssertEqual(model.mediaState.attemptID, healthy)
            XCTAssertEqual(assembly.playback.currentAttempt, donor,
                           "a real decoder failure in the requested quality must not destroy the paused donor")
            XCTAssertTrue(model.mediaState.paused)
            XCTAssertEqual(model.mediaState.position, 1.1, accuracy: 0.15)
        }

        let session = try XCTUnwrap(model.sessionID)
        let current = try XCTUnwrap(model.mediaState.attemptID)
        let receipt = try await client.call(module: .film, operation: "session.attempt", parameters: .object([
            "sessionId": .string(session.uuidString.lowercased()), "attemptId": .string(current),
        ]))
        let mediaURL = try XCTUnwrap(receipt.filmObject?["url"]?.filmText.flatMap(URL.init(string:)))
        // This exercises the actual App notification consumer, not an assertion
        // that a synthetic notification grants OS background/PiP permission.
        NotificationCenter.default.post(name: UIApplication.didEnterBackgroundNotification, object: nil)
        try await waitForHost { model.isReleased }
        await model.waitForPendingWork()
        XCTAssertNil(assembly.playback.currentAttempt)
        XCTAssertNil(model.mediaState.attemptID)
        let (_, response) = try await URLSession.shared.data(from: mediaURL)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 404)
        let history = try await fixture.history()
        XCTAssertEqual(try XCTUnwrap(history.first?.filmObject?["position"]?.filmNumber), 1.1, accuracy: 0.15)
        do {
            _ = try await client.call(module: .film, operation: "session.snapshot", parameters: .object([
                "sessionId": .string(session.uuidString.lowercased()),
            ]))
            XCTFail("the released business session must not survive the native drain")
        } catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("NOT_FOUND")) }

        let beforeReopen = try await fixture.sourceState()["mediaReads"]
        await model.open()?.value
        try await waitForPreparation(model, autoPrepare: autoPrepare, previousSessionID: session)
        await model.waitForPendingWork()
        XCTAssertNil(assembly.playback.currentAttempt, "returning to the page is not consent to resume playback")
        XCTAssertFalse(assembly.playback.wantsPlayback)
        XCTAssertNil(model.mediaState.attemptID)
        let reopenedState = try await fixture.sourceState()
        XCTAssertEqual(reopenedState["mediaReads"], beforeReopen)
        model.pauseOrResume()
        try await waitForHost {
            model.mediaState.attemptID != nil && model.mediaState.position > 0.1 &&
                model.snapshot?.current?.attemptID == model.mediaState.attemptID
        }
        XCTAssertEqual(assembly.playback.currentAttempt?.owner, "film")
        XCTAssertTrue(assembly.playback.wantsPlayback)
        XCTAssertNotEqual(model.sessionID, session)
        XCTAssertFalse(client.isClosed, "a page never owns the shared Node lifetime")
        await model.close()
        XCTAssertTrue(model.isReleased)
    }

    private func exerciseManifestReceipt(format: String, invalidUTF8: Bool) async throws {
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let client = try await resources.scriptSession.connection()
        let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
        let movieBytes = try Data(contentsOf: movie)
        let title = "正片é\u{FEFF}\u{FFFD}🎬"
        let text = format == "hls"
            ? "#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:3,\(title)\nsegment.m4s\n#EXT-X-ENDLIST\n"
            : "<?xml version=\"1.0\" encoding=\"UTF-8\"?><MPD><ProgramInformation><Title>\(title)</Title>" +
                "</ProgramInformation><Period><AdaptationSet contentType=\"video\"><Representation id=\"v\" bandwidth=\"1\">" +
                "<SegmentList duration=\"3\"><Initialization sourceURL=\"init.mp4\"/><SegmentURL media=\"segment.m4s\"/>" +
                "</SegmentList></Representation></AdaptationSet></Period></MPD>"
        var body = Data([0xef, 0xbb, 0xbf])
        let prefix = invalidUTF8 ? (format == "hls" ? text + "#" : text.replacingOccurrences(of: "</MPD>", with: "<!--")) : text
        body.append(contentsOf: prefix.utf8)
        if invalidUTF8 {
            body.append(contentsOf: [0xc0, 0xaf]) // Invalid even in an otherwise harmless manifest comment.
            body.append(contentsOf: (format == "hls" ? "\n" : "--></MPD>").utf8)
        }
        let fixture = FilmPlaybackHostFixture(assembly: assembly, client: client, movie: movie)
        addTeardownBlock { try await fixture.close() }
        let searchTitle = fixture.id.replacingOccurrences(of: "film-host-", with: "ﬁlm-ｈｏｓｔ-\u{2060}")
        XCTAssertNotEqual(searchTitle, fixture.id)
        try await fixture.install(autoPrepare: true, manifest: (format: format, body: body),
                                  searchTitle: searchTitle)
        let model = try await fixture.makeModel()
        await model.open()?.value
        try await waitForPreparation(model, autoPrepare: true)
        let prepared = try await fixture.sourceState()
        XCTAssertEqual(operations(prepared), ["search", "detail", "play"])
        XCTAssertEqual(prepared["mediaReads"], .number(0))
        XCTAssertEqual(prepared["manifestReads"], .array([]))
        XCTAssertEqual(prepared["binaryReads"], .array([]))
        let sessionID = try XCTUnwrap(model.sessionID)
        let attempt = try XCTUnwrap(model.snapshot?.attempt)
        XCTAssertTrue(attempt.prepareOnly)
        let receipt = try await client.call(module: .film, operation: "session.attempt", parameters: .object([
            "sessionId": .string(sessionID.uuidString.lowercased()), "attemptId": .string(attempt.id),
        ]))
        let mediaURL = try XCTUnwrap(receipt.filmObject?["url"]?.filmText.flatMap(URL.init(string:)))
        let origin = try XCTUnwrap(receipt.filmObject?["mediaOrigin"]?.filmText.flatMap(URL.init(string:)))
        XCTAssertEqual(receipt.filmObject?["format"], .string(format))
        XCTAssertEqual(mediaURL.scheme, "http")
        XCTAssertEqual(mediaURL.host, "127.0.0.1")
        XCTAssertEqual(mediaURL.scheme, origin.scheme)
        XCTAssertEqual(mediaURL.host, origin.host)
        XCTAssertEqual(mediaURL.port, origin.port)
        XCTAssertTrue(mediaURL.path.hasPrefix("/media/"))
        let beforeRead = try await fixture.sourceState()
        XCTAssertEqual(beforeRead["mediaReads"], .number(0), "A prepared receipt must not warm media")

        // URLSession consumes the real NodeMobile receipt without a native decoder
        // or a client-side copy of the source's Cookie.
        let httpSession = URLSession(configuration: .ephemeral)
        defer { httpSession.invalidateAndCancel() }
        let (root, response) = try await httpSession.data(from: mediaURL)
        let http = try XCTUnwrap(response as? HTTPURLResponse)
        let rootPath = format == "hls" ? "/proxy/manifest.m3u8" : "/proxy/manifest.mpd"
        let rootReads: RuntimeValue = .array([.object(["path": .string(rootPath), "method": .string("GET")])])
        let rooted = try await fixture.sourceState()
        XCTAssertEqual(rooted["manifestReads"], rootReads)
        XCTAssertEqual(rooted["mediaReads"], .number(1))
        XCTAssertEqual(rooted["binaryReads"], .array([]))
        XCTAssertEqual(rooted["missingCookie"], .number(0))
        if invalidUTF8 {
            XCTAssertEqual(http.statusCode, 502)
            XCTAssertEqual(String(data: root, encoding: .utf8), "MEDIA_MANIFEST_INVALID")
        } else {
            XCTAssertEqual(http.statusCode, 200)
            XCTAssertEqual(http.mimeType, format == "hls" ? "application/vnd.apple.mpegurl" : "application/dash+xml")
            XCTAssertFalse(root.starts(with: [0xef, 0xbb, 0xbf]), "The leading UTF-8 BOM is consumed")
            let rewritten = try XCTUnwrap(String(data: root, encoding: .utf8))
            XCTAssertTrue(rewritten.contains(title), "Interior BOM, U+FFFD and non-ASCII text remain unchanged")
            let base: URL
            let references: [String]
            if format == "hls" {
                base = mediaURL
                let segment = try XCTUnwrap(rewritten.split(whereSeparator: \.isNewline).first {
                    !$0.isEmpty && !$0.hasPrefix("#")
                })
                references = [try manifestCapture(#"URI="([^"]+)""#, in: rewritten), String(segment)]
            } else {
                let baseText = try manifestCapture("<BaseURL>([^<]+)</BaseURL>", in: rewritten)
                base = try XCTUnwrap(URL(string: baseText, relativeTo: mediaURL)?.absoluteURL)
                references = [
                    try manifestCapture(#"<Initialization\b[^>]*\bsourceURL="([^"]+)""#, in: rewritten),
                    try manifestCapture(#"<SegmentURL\b[^>]*\bmedia="([^"]+)""#, in: rewritten),
                ]
            }
            for reference in references {
                let child = try XCTUnwrap(URL(string: reference, relativeTo: base)?.absoluteURL)
                XCTAssertEqual(child.scheme, origin.scheme)
                XCTAssertEqual(child.host, origin.host)
                XCTAssertEqual(child.port, origin.port)
                XCTAssertTrue(child.path.hasPrefix("/media/"))
                let (binary, response) = try await httpSession.data(from: child)
                XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
                XCTAssertEqual(binary, movieBytes)
            }
        }
        let finished = try await fixture.sourceState()
        let binaryReads: [RuntimeValue] = invalidUTF8 ? [] : ["/proxy/init.mp4", "/proxy/segment.m4s"].map {
            .object(["path": .string($0), "method": .string("GET"), "bytes": .number(Double(movieBytes.count))])
        }
        XCTAssertEqual(finished["manifestReads"], rootReads)
        XCTAssertEqual(finished["mediaReads"], .number(invalidUTF8 ? 1 : 3))
        XCTAssertEqual(finished["binaryReads"], .array(binaryReads))
        XCTAssertEqual(finished["missingCookie"], .number(0), "The runtime attaches the source header to root and children")
        XCTAssertEqual(finished["reports"], .array([]), "Network parsing is not playback or a watch report")
        XCTAssertNil(assembly.playback.currentAttempt)
        XCTAssertNil(model.mediaState.attemptID)
        XCTAssertNil(model.snapshot?.current)
        await model.close()
        XCTAssertTrue(model.isReleased)
        let (_, released) = try await httpSession.data(from: mediaURL)
        XCTAssertEqual((released as? HTTPURLResponse)?.statusCode, 404)
        let closed = try await fixture.sourceState()
        XCTAssertEqual(closed["mediaReads"], finished["mediaReads"], "A released receipt cannot reach its upstream")
    }

    private func manifestCapture(_ pattern: String, in text: String) throws -> String {
        let expression = try NSRegularExpression(pattern: pattern)
        let match = try XCTUnwrap(expression.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)))
        let range = try XCTUnwrap(Range(match.range(at: 1), in: text))
        return String(text[range])
    }

    private func operations(_ value: RuntimeObject) -> [String] {
        value["calls"]?.filmArray?.compactMap(\.filmText) ?? []
    }

    private func waitForPreparation(_ model: FilmPlaybackModel, autoPrepare: Bool,
                                    previousSessionID: UUID? = nil) async throws {
        try await waitForHost {
            guard let snapshot = model.snapshot, snapshot.sessionID != previousSessionID,
                  snapshot.error == nil, snapshot.current == nil, !snapshot.browsing.loading,
                  snapshot.browsing.error == nil, model.actionError == nil, model.observationError == nil else { return false }
            return autoPrepare
                ? snapshot.stage == "prepared" && snapshot.attempt?.prepareOnly == true
                : snapshot.stage == "idle" && snapshot.attempt == nil
        }
        XCTAssertNil(model.actionError)
        XCTAssertNil(model.observationError)
        XCTAssertNil(model.snapshot?.error)
        XCTAssertNil(model.snapshot?.browsing.error)
        XCTAssertNil(model.snapshot?.current)
        XCTAssertEqual(model.snapshot?.preferences.iosAutoPrepare, autoPrepare)
    }

    private func waitForHost(_ predicate: @escaping @MainActor () -> Bool) async throws {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 20)
        guard predicate() else { throw FilmPlaybackError(code: "HOST_TEST_TIMEOUT") }
    }
}

@MainActor
final class FilmPlaybackHostFixture {
    let id: String
    let metadataID: Int
    let assembly: MeowfilmAssembly
    let client: RuntimeHTTPClient
    let movie: URL
    var music: PlayAttempt?
    private var originalSettings: RuntimeValue?
    private var source: FilmPlaybackSource?
    private var model: FilmPlaybackModel?
    private var musicURL: URL?
    private var window: UIWindow?
    private weak var previousWindow: UIWindow?
    var targetID: String { "tmdb:tv:\(metadataID)" }

    init(assembly: MeowfilmAssembly, client: RuntimeHTTPClient, movie: URL,
         id: String = "film-host-" + UUID().uuidString.lowercased(),
         metadataID: Int = Int.random(in: 1_000_000_000...2_000_000_000)) {
        self.assembly = assembly; self.client = client; self.movie = movie
        self.id = id; self.metadataID = metadataID
    }

    func makeMusicResource() throws -> PlaybackResource {
        // The three-second movie can naturally end while Node prepares. This
        // finite PCM tone keeps real audio progressing without a loop/resume shim.
        let rate = 8_000, samples = rate * 120, size = samples * 2
        var bytes = Data()
        bytes.reserveCapacity(44 + size)
        func append<T: FixedWidthInteger>(_ value: T) {
            var little = value.littleEndian
            withUnsafeBytes(of: &little) { bytes.append(contentsOf: $0) }
        }
        bytes.append(contentsOf: "RIFF".utf8); append(UInt32(36 + size))
        bytes.append(contentsOf: "WAVEfmt ".utf8); append(UInt32(16))
        append(UInt16(1)); append(UInt16(1))
        append(UInt32(rate)); append(UInt32(rate * 2))
        append(UInt16(2)); append(UInt16(16))
        bytes.append(contentsOf: "data".utf8); append(UInt32(size))
        var tone = Data()
        for sample in 0..<40 {
            var value = Int16(sin(2 * .pi * Double(sample) / 40) * 1_024).littleEndian
            withUnsafeBytes(of: &value) { tone.append(contentsOf: $0) }
        }
        for _ in 0..<(samples / 40) { bytes.append(tone) }
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(id + ".wav")
        try bytes.write(to: url, options: .atomic)
        musicURL = url
        return try .init(id: id, url: url, title: "Synthetic music donor")
    }

    func install(autoPrepare: Bool, directMediaBase: URL? = nil, signMediaURL: Bool = false,
                 manifest: (format: String, body: Data)? = nil, searchTitle: String? = nil) async throws {
        let settings = try await client.call(module: .film, operation: "settings.get")
        originalSettings = settings
        // Source-selection assertions use known fixture preferences, without
        // changing the container's original settings after teardown.
        var changed = try FilmPlaybackPreferences.defaults.value.filmRequiredObject()
        changed["iosAutoPrepare"] = .bool(autoPrepare)
        _ = try await client.call(module: .film, operation: "settings.set", parameters: .object(changed))
        let bytes = try Data(contentsOf: movie).base64EncodedString()
        // Base64 only embeds a synthetic test asset in the installed fixture.
        // The production Node/native media path below is binary HTTP with Range.
        let script = Self.script.replacingOccurrences(of: "__MOVIE__", with: bytes)
            .replacingOccurrences(of: "__TITLE__", with: id)
            .replacingOccurrences(of: "__SEARCH_TITLE__", with: String(decoding:
                try RuntimeJSON.encode(.string(searchTitle ?? id)), as: UTF8.self))
            .replacingOccurrences(of: "__DIRECT_BASE__", with: String(decoding:
                try RuntimeJSON.encode(directMediaBase.map { .string($0.absoluteString) } ?? .null), as: UTF8.self))
            .replacingOccurrences(of: "__MANIFEST__", with: String(decoding: try RuntimeJSON.encode(manifest.map {
                .object(["format": .string($0.format), "body": .string($0.body.base64EncodedString())])
            } ?? .null), as: UTF8.self))
            .replacingOccurrences(of: "__MEDIA_QUERY__", with: signMediaURL ? "?signature=film-host-synthetic-signature" : "")
        _ = try await client.upload(module: .cat, installationID: id, sourceRevision: 1,
                                    source: Data(script.utf8))
        _ = try await client.call(module: .cat, operation: "install", parameters: .object([
            "installationId": .string(id), "sourceRevision": .number(1), "enabled": .bool(true),
        ]))
        let status = try await client.call(module: .cat, operation: "status")
        let instance = try XCTUnwrap(status.filmObject?["instances"]?.filmArray?.first {
            $0.filmObject?["installationId"] == .string(id)
        }?.filmObject)
        XCTAssertEqual(instance["state"], .string("ready"))
        let generation = try XCTUnwrap(instance["generation"]?.filmInteger)
        source = .init(key: id, engine: "cat", installationID: id, siteID: "host",
                       generation: Int64(generation), name: "Host fixture")
    }

    func makeRequest(position: FilmEpisodePosition = .init()) async throws -> FilmPlaybackRequest {
        let tmdb = try await client.call(module: .tmdb, operation: "settings.get")
        let douban = try await client.call(module: .douban, operation: "settings.get")
        let now = Date().timeIntervalSince1970 * 1_000
        func entry(_ provider: String, _ path: String, _ query: RuntimeObject,
                   _ value: RuntimeValue, _ settings: RuntimeValue) throws -> RuntimeValue {
            .object(["kind": .string("metadata"), "provider": .string(provider), "path": .string(path),
                     "query": .object(query), "value": value, "completeness": .string("complete"),
                     "fetchedAt": .number(now),
                     "settings": .object(["revision": try XCTUnwrap(settings.filmObject?["revision"])])])
        }
        let metadata: RuntimeValue = .object([
            "id": .number(Double(metadataID)), "name": .string(id), "genres": .array([]),
            "first_air_date": .string("2020-01-01"), "number_of_seasons": .number(1), "number_of_episodes": .number(2),
            "seasons": .array([.object(["season_number": .number(1), "episode_count": .number(2),
                                        "air_date": .string("2020-01-01")])]),
        ])
        let season: RuntimeValue = .object(["season_number": .number(1), "episodes": .array((1...2).map {
            .object(["id": .number(Double($0)), "season_number": .number(1), "episode_number": .number(Double($0)),
                     "name": .string("第\($0)集"), "air_date": .string("2020-01-01")])
        })])
        let entries = try [
            entry("tmdb", "/3/tv/\(metadataID)", ["language": .string("zh-CN"),
                "append_to_response": .string("credits,similar,images")], metadata, tmdb),
            entry("tmdb", "/3/tv/\(metadataID)/season/1", ["language": .string("zh-CN")], season, tmdb),
            entry("douban", "search", ["query": .string(id)], .object(["subjects": .object(["items": .array([])])]), douban),
        ]
        return FilmPlaybackRequest(target: .init(id: targetID, kind: .tv, title: id,
            metadataProvider: .tmdb, metadataID: String(metadataID), position: position),
            sources: [try XCTUnwrap(source)], metadata: metadata, cacheEntries: entries)
    }

    func makeModel(position: FilmEpisodePosition = .init()) async throws -> FilmPlaybackModel {
        let request = try await makeRequest(position: position)
        let model = try assembly.makeFilmPlayback(request: { request })
        self.model = model
        try await assembly.activateFilm(model)
        return model
    }

    func mountSurface(_ model: FilmPlaybackModel) async throws {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        previousWindow = scene.windows.first { $0.isKeyWindow }
        let host = UIViewController()
        let view = try XCTUnwrap(model.makeVideoView())
        host.loadViewIfNeeded()
        view.frame = host.view.bounds; view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        host.view.addSubview(view)
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds; window.windowLevel = .normal + 1
        window.rootViewController = host; window.makeKeyAndVisible()
        window.layoutIfNeeded()
        self.window = window
    }

    func sourceState(waitFor condition: RuntimeObject? = nil) async throws -> RuntimeObject {
        let source = try XCTUnwrap(source)
        let value = try await client.call(module: .cat, operation: "invoke", parameters: .object([
            "installationId": .string(id), "generation": try XCTUnwrap(source.value["generation"]),
            "siteId": .string("host"), "operation": .string("home"),
            "payload": .object(condition.map { ["waitFor": .object($0)] } ?? [:]),
        ]))
        let state = try value.filmRequiredObject()
        if let error = state["waitError"]?.filmText { throw FilmPlaybackError(code: error) }
        return state
    }

    func history() async throws -> [RuntimeValue] {
        let value = try await client.call(module: .film, operation: "library.get")
        return try value.filmRequiredObject().filmArray("history").filter { $0.filmObject?["targetId"] == .string(targetID) }
    }

    func removePresentation() throws {
        if let music { _ = assembly.playback.stop(attempt: music) }
        window?.isHidden = true; window?.rootViewController = nil; window = nil
        previousWindow?.makeKey()
        if let musicURL {
            try FileManager.default.removeItem(at: musicURL); self.musicURL = nil
        }
    }

    func restorePreferences() async throws {
        if let originalSettings {
            _ = try await client.call(module: .film, operation: "settings.set", parameters: originalSettings)
        }
    }

    func close(preservingLibrary: Bool = false) async throws {
        await model?.close()
        if let model { XCTAssertTrue(model.isReleased) }
        var first: Error?
        do { try removePresentation() } catch { first = error }
        if preservingLibrary {
            if let first { throw first }
            return
        }
        do {
            _ = try await client.call(module: .film, operation: "library.remove", parameters: .object([
                "kind": .string("history"), "id": .string(targetID),
            ]))
        } catch { first = first ?? error }
        do {
            let status = try await client.call(module: .cat, operation: "status")
            if status.filmObject?["instances"]?.filmArray?.contains(where: {
                $0.filmObject?["installationId"] == .string(id)
            }) == true {
                _ = try await client.call(module: .cat, operation: "uninstall",
                                          parameters: .object(["installationId": .string(id)]))
            }
        } catch { first = first ?? error }
        do { try await restorePreferences() } catch { first = first ?? error }
        if let first { throw first }
    }

    private static let script = #"""
    const http = require('http');
    const bytes = Buffer.from('__MOVIE__', 'base64');
    const directBase = __DIRECT_BASE__;
    const manifest = __MANIFEST__;
    const manifestPath = manifest && '/proxy/manifest.' + (manifest.format === 'hls' ? 'm3u8' : 'mpd');
    const manifestBody = manifest && Buffer.from(manifest.body, 'base64');
    let server, mediaReads = 0, missingCookie = 0;
    const calls = [], reports = [], plays = [], binaryReads = [], manifestReads = [], waiters = new Set();
    const json = (response, value) => {
      response.setHeader('content-type', 'application/json'); response.end(JSON.stringify(value));
    };
    const state = () => ({ calls, reports, plays, binaryReads, mediaReads, missingCookie, ...(manifest ? { manifestReads } : {}) });
    const records = { report: reports, play: plays, binary: binaryReads };
    const observed = condition => Object.entries(condition).every(([kind, expected]) =>
      records[kind].some(record => Object.entries(expected).every(([key, value]) => record[key] === value)));
    const notify = () => { for (const waiter of [...waiters]) waiter(); };
    function observe(response, condition) {
      let timer;
      const cancel = () => { waiters.delete(changed); clearTimeout(timer); };
      const finish = error => {
        cancel(); response.removeListener('close', cancel);
        json(response, { ...state(), ...(error ? { waitError: error } : {}) });
      };
      const changed = () => { if (observed(condition)) finish(); };
      waiters.add(changed); response.once('close', cancel);
      timer = setTimeout(() => finish('HOST_FIXTURE_EVENT_TIMEOUT'), 10000);
      changed();
    }
    module.exports.start = () => new Promise(resolve => {
      server = http.createServer(async (request, response) => {
        const url = new URL(request.url, 'http://localhost');
        if (url.pathname === '/config' || url.pathname === '/full-config') {
          json(response, { video: { sites: [{ key: 'host', name: 'Host fixture', api: '/api/host',
            actions: ['home', 'search', 'detail', 'play', 'report'] }] } }); return;
        }
        if (url.pathname.startsWith('/proxy/')) {
          mediaReads++;
          if (request.headers.cookie !== 'host-fixture=private') {
            missingCookie++; response.writeHead(403); response.end(); return;
          }
          if (manifest && url.pathname === manifestPath) {
            manifestReads.push({ path: url.pathname, method: request.method });
            response.writeHead(200, { 'content-type': manifest.format === 'hls' ? 'application/vnd.apple.mpegurl' : 'application/dash+xml',
              'content-length': manifestBody.length });
            response.end(request.method === 'HEAD' ? undefined : manifestBody); return;
          }
          const value = url.pathname === '/proxy/broken.mp4' ? Buffer.from([0, 1, 2, 3]) : bytes;
          const range = /^bytes=(\d+)-(\d*)$/.exec(request.headers.range || '');
          const start = range ? Number(range[1]) : 0;
          const end = range && range[2] ? Math.min(Number(range[2]), value.length - 1) : value.length - 1;
          if (start > end || start >= value.length) {
            response.writeHead(416, { 'content-range': 'bytes */' + value.length }); response.end(); return;
          }
          const headers = { 'content-type': 'video/mp4', 'content-length': end - start + 1, 'accept-ranges': 'bytes' };
          if (range) headers['content-range'] = `bytes ${start}-${end}/${value.length}`;
          response.writeHead(range ? 206 : 200, headers);
          binaryReads.push({ path: url.pathname, method: request.method, bytes: request.method === 'HEAD' ? 0 : end - start + 1 });
          notify();
          response.end(request.method === 'HEAD' ? undefined : value.subarray(start, end + 1)); return;
        }
        const chunks = []; for await (const chunk of request) chunks.push(Buffer.from(chunk));
        const input = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : Object.fromEntries(url.searchParams);
        const action = url.pathname.split('/').pop();
        if (action === 'home') {
          if (input.waitFor) observe(response, input.waitFor); else json(response, state());
          return;
        }
        if (action === 'report') {
          reports.push({ event: input.event, id: input.id, flag: input.flag }); notify();
          json(response, { ok: true }); return;
        }
        calls.push(action);
        if (action === 'search') {
          json(response, { list: [{ vod_id: 0, vod_name: __SEARCH_TITLE__ }], pagecount: 1 });
        } else if (action === 'detail') {
          json(response, { list: [{ vod_id: 0, vod_name: '__TITLE__',
            vod_play_from: '原始线路$$$备用原始线路$$$故障线路',
            vod_play_url: '第1集.1080p$one#第2集.1080p$two$$$第1集.1080p$other#第2集.1080p$second$$$第1集.720p$broken' }] });
        } else if (action === 'play') {
          plays.push({ id: input.id, flag: input.flag }); notify();
          if (manifest) {
            json(response, { parse: 0, watchReport: true, format: manifest.format,
              url: 'http://127.0.0.1:9987' + manifestPath, headers: { Cookie: 'host-fixture=private' } }); return;
          }
          json(response, { parse: 0, watchReport: true, ...(directBase
            ? { url: `${directBase}/${input.id}.mp4__MEDIA_QUERY__` }
            : { url: `http://127.0.0.1:9987/proxy/${input.id}.mp4__MEDIA_QUERY__`,
                headers: { Cookie: 'host-fixture=private' } }) });
        } else { response.writeHead(404); response.end(); }
      });
      server.listen(9987, '127.0.0.1', resolve);
    });
    module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
    """#
}
