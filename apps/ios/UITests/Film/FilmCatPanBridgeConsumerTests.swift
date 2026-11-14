#if os(iOS)
import XCTest
import UIKit
import MeowRuntime
import MeowUI
@testable import MeowFilmFeature

/// Uses tests/film-cat-pan-bridge-fixture.mjs through its public HTTP surface.
/// The media owner records receipts only: no media request or decoder is opened.
@MainActor
final class FilmCatPanBridgeConsumerTests: XCTestCase {
    override func setUp() async throws {
        try await super.setUp()
        let identifier = try XCTUnwrap(Bundle.main.bundleIdentifier)
        await withCheckedContinuation { continuation in
            RunLoop.main.perform {
                MainActor.assumeIsolated { XCUIApplication(bundleIdentifier: identifier).activate() }
                continuation.resume()
            }
        }
        try await waitFor {
            UIApplication.shared.applicationState == .active &&
                UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive }
        }
    }

    func testCatPanHTTPDirectoryKeepsSiteAndPanDistinctAndConsumesExactFileReceipt() async throws {
        let bridge = try await connection()
        let media = FilmUIKitPlaybackMedia()
        let model = makeModel(bridge, media: media, target: bridge.target)
        await model.open()?.value
        try await waitFor { model.snapshot?.stage == "idle" }
        XCTAssertEqual(model.snapshot?.entrySurface, "search")
        XCTAssertEqual(model.snapshot?.target.kind, .source)
        XCTAssertNil(model.snapshot?.attempt)
        let result = try XCTUnwrap(model.snapshot?.sources.first)
        XCTAssertEqual(result.source.name, "Bridge site")
        XCTAssertTrue(model.snapshot?.browsing.lines.isEmpty == true)
        let page = try detailPage(model)
        try mount(page)
        try await waitFor {
            page.isPageVisible && model.snapshot?.browsing.loading == false &&
                model.snapshot?.browsing.files.count == 3
        }
        invoke(try action("Bridge site · Bridge Series", button("film.detail.source", page).menu))
        await model.waitForPendingWork()
        try await waitFor {
            model.snapshot?.browsing.sourceID == result.id && model.snapshot?.browsing.loading == false &&
                model.snapshot?.browsing.files.count == 3
        }
        let browsing = try XCTUnwrap(model.snapshot?.browsing)
        XCTAssertNil(browsing.error)
        XCTAssertEqual(browsing.lines.map(\.label), ["夸克"])
        XCTAssertEqual(browsing.lines.map(\.provider), ["quark"])
        XCTAssertEqual(browsing.files.map(\.quality), ["4k_fps", "1080p", "1080p"])
        XCTAssertEqual(browsing.files.map(\.position.episode), [1, 1, 2])
        XCTAssertEqual(browsing.files.first?.resolution, "3840x2160")
        XCTAssertEqual(browsing.files.first?.codec, "hevc")
        XCTAssertEqual(try XCTUnwrap(browsing.files.first?.fps), 60_000.0 / 1_001, accuracy: 0.0001)
        try await waitFor {
            (try? self.button("film.detail.source", page).title(for: .normal)) == "Bridge site · Bridge Series" &&
                (try? self.button("film.detail.line", page).title(for: .normal)) == "夸克"
        }
        XCTAssertEqual(try button("film.detail.line", page).accessibilityLabel, "网盘源")
        let high = try XCTUnwrap(model.snapshot?.candidates.first { $0.quality == "4k_fps" })
        XCTAssertEqual(high.provider, "quark")
        XCTAssertEqual(high.source.key, bridge.source.key)
        XCTAssertEqual(high.source.detailID, .string("bridge-detail"))
        XCTAssertEqual(high.resolution, browsing.files.first?.resolution)
        XCTAssertEqual(high.codec, browsing.files.first?.codec)
        let file = try XCTUnwrap(browsing.files.first)
        XCTAssertEqual(file.candidateID, high.id)
        let control = try button("film.file.\(file.id)", page)
        control.sendActions(for: .touchUpInside)
        try await waitFor { model.snapshot?.current?.candidate.id == high.id && media.inputs.count == 1 }
        await model.waitForPendingWork()
        let input = try XCTUnwrap(media.inputs.first)
        XCTAssertEqual(input.sessionID, model.sessionID)
        XCTAssertEqual(input.attemptID, model.snapshot?.current?.attemptID)
        XCTAssertEqual(model.snapshot?.current?.candidate.position, high.position)
        XCTAssertEqual(high.position.season, 1); XCTAssertEqual(high.position.episode, 1)
        let receipt = try await bridge.client.call(module: .film, operation: "session.attempt", parameters: .object([
            "sessionId": .string(input.sessionID.uuidString.lowercased()), "attemptId": .string(input.attemptID),
        ]))
        XCTAssertEqual(input.resourceID, receipt.catField("id")?.catText)
        XCTAssertEqual(input.url.absoluteString, receipt.catField("url")?.catText)
        XCTAssertEqual(input.mediaOrigin?.absoluteString, receipt.catField("mediaOrigin")?.catText)
        XCTAssertEqual(input.scope, receipt.catField("scope"))
        XCTAssertEqual(input.scope?.catField("accountProvider"), .string("quark"))
        XCTAssertEqual(input.scope?.catField("accountRevision"), .number(1))
        XCTAssertEqual(input.scope?.catField("sourceKey"), .string(try XCTUnwrap(bridge.source.key)))
        XCTAssertNil(receipt.catField("headers"), "Private playback headers remain inside Node")
        let evidence = try await bridge.control("evidence")
        let records = Array((evidence.catField("records")?.catArray ?? []).dropFirst(bridge.recordCount))
        XCTAssertTrue(records.contains { $0.catField("layer") == .string("cat") && $0.catField("phase") == .string("intercepted") })
        XCTAssertEqual(records.filter { $0.catField("layer") == .string("pan-service") }.compactMap {
            $0.catField("phase")?.catText
        }, ["list", "prepare", "play"])
        XCTAssertEqual(evidence.catField("counters")?.catField("scriptNetwork"), .number(0))
        XCTAssertEqual(evidence.catField("counters")?.catField("mediaBody"), .number(0))
        await model.close()
        XCTAssertTrue(model.isReleased)
        XCTAssertTrue(media.isLocallyDrained(sessionID: input.sessionID))
        XCTAssertNil(model.mediaStopError); XCTAssertNil(model.sessionReleaseError)
        let released = try await bridge.control("evidence")
        XCTAssertEqual(released.catField("counters")?.catField("mediaBody"), .number(0))
    }

    func testCatDASHCENCReceiptUsesNodeMediaOriginAndPreservesOptionalDescriptorFields() async throws {
        try await descriptor(direct: false)
    }

    func testHeaderlessCatDASHCENCReceiptKeepsDirectURLWithoutNodeMediaOrigin() async throws {
        try await descriptor(direct: true)
    }

    private func descriptor(direct: Bool) async throws {
        let bridge = try await connection()
        let detailID = direct ? "descriptor-direct" : "descriptor"
        var fields = bridge.target.value
        var source = try XCTUnwrap(fields["source"]?.filmObject)
        source["detailId"] = .string(detailID)
        fields["source"] = .object(source); fields["id"] = .string("site:\(detailID)")
        let target = try FilmPlaybackTarget(.object(fields))
        let media = FilmUIKitPlaybackMedia(), model = makeModel(bridge, media: media, target: target)
        await model.open()?.value
        try await waitFor { model.snapshot?.stage == "idle" }
        await model.send("play")?.value
        try await waitFor { model.snapshot?.current != nil && media.inputs.count == 1 }
        let input = try XCTUnwrap(media.inputs.first)
        XCTAssertEqual(input.format, "dash")
        XCTAssertEqual(input.drm?.catField("scheme"), .string("cenc"))
        XCTAssertEqual(input.drm?.catField("clearKeys"), .object([
            "00000000000000000000000000000001": .string("00000000000000000000000000000002"),
        ]))
        if direct { XCTAssertNil(input.mediaOrigin) }
        else { XCTAssertNotNil(input.mediaOrigin) }
        XCTAssertEqual(input.sessionID, model.sessionID)
        XCTAssertEqual(input.attemptID, model.snapshot?.current?.attemptID)
        XCTAssertEqual(model.snapshot?.current?.candidate.source.detailID, .string(detailID))
        let receipt = try await bridge.client.call(module: .film, operation: "session.attempt", parameters: .object([
            "sessionId": .string(input.sessionID.uuidString.lowercased()), "attemptId": .string(input.attemptID),
        ]))
        XCTAssertEqual(input.url.absoluteString, receipt.catField("url")?.catText)
        XCTAssertEqual(input.mediaOrigin?.absoluteString, receipt.catField("mediaOrigin")?.catText)
        XCTAssertEqual(input.drm, receipt.catField("drm"))
        XCTAssertEqual(input.scope, receipt.catField("scope"))
        XCTAssertNil(receipt.catField("headers"))
        let evidence = try await bridge.control("evidence")
        let records = Array((evidence.catField("records")?.catArray ?? []).dropFirst(bridge.recordCount))
        XCTAssertFalse(records.contains { $0.catField("layer") == .string("pan-service") },
                       "A direct Cat descriptor cannot be replaced by a pan file")
        XCTAssertEqual(evidence.catField("counters")?.catField("mediaBody"), .number(0))
        XCTAssertEqual(evidence.catField("counters")?.catField("mediaHead"), .number(0))
        await model.close()
        XCTAssertTrue(model.isReleased)
        XCTAssertNil(model.mediaStopError); XCTAssertNil(model.sessionReleaseError)
    }

    func testCatPanHTTPListFailureCannotExposePlayableRowsAndOwnerRetryRebuildsDirectory() async throws {
        let bridge = try await connection()
        _ = try await bridge.control("scenario", body: .object(["scenario": .string("list-http")]))
        let media = FilmUIKitPlaybackMedia(), model = makeModel(bridge, media: media, target: bridge.target)
        await model.open()?.value
        try await waitFor { model.snapshot?.stage == "idle" }
        let result = try XCTUnwrap(model.snapshot?.sources.first)
        model.browse(sourceID: result.id)
        try await waitFor { model.snapshot?.browsing.error != nil && model.snapshot?.browsing.loading == false }
        XCTAssertEqual(model.snapshot?.browsing.error, "UPSTREAM_HTTP")
        XCTAssertTrue(model.snapshot?.browsing.files.isEmpty == true)
        XCTAssertTrue(model.snapshot?.browsing.originalFiles.isEmpty == true)
        XCTAssertNil(model.snapshot?.attempt); XCTAssertNil(model.snapshot?.current)
        let page = try detailPage(model)
        page.loadViewIfNeeded()
        XCTAssertFalse(try button("film.detail.play", page).isEnabled)
        XCTAssertTrue(media.inputs.isEmpty)
        _ = try await bridge.control("scenario", body: .object(["scenario": .string("success")]))
        await model.refreshBrowsing()?.value
        try await waitFor { model.snapshot?.browsing.files.count == 3 && model.snapshot?.browsing.loading == false }
        XCTAssertNil(model.snapshot?.browsing.error)
        XCTAssertEqual(model.snapshot?.browsing.sourceID, result.id)
        XCTAssertEqual(model.snapshot?.browsing.lines.first?.provider, "quark")
        XCTAssertTrue(media.inputs.isEmpty)
        let evidence = try await bridge.control("evidence")
        XCTAssertEqual(evidence.catField("counters")?.catField("mediaBody"), .number(0))
        await model.close()
        XCTAssertTrue(model.isReleased)
    }

    private func connection() async throws -> FilmBridgeConnection {
        let address = try XCTUnwrap(ProcessInfo.processInfo.environment["MEOWFILM_CAT_PAN_FIXTURE"],
            "Set MEOWFILM_CAT_PAN_FIXTURE to the shared fixture controlUrl")
        let controlURL = try XCTUnwrap(URL(string: address))
        let value = try await FilmBridgeConnection.read(controlURL.appendingPathComponent("fixture"))
        let addressValue = try XCTUnwrap(value.catField("baseUrl")?.catText)
        let client = try RuntimeHTTPClient(baseURL: XCTUnwrap(URL(string: addressValue)))
        let fields = try value.filmRequiredObject()
        let source = try FilmPlaybackSource(fields.filmValue("source"))
        let target = try FilmPlaybackTarget(fields.filmValue("directTarget"))
        _ = try await FilmBridgeConnection.read(controlURL.appendingPathComponent("scenario"),
            body: .object(["scenario": .string("success")]))
        _ = try await client.call(module: .film, operation: "data.invalidate", parameters: .object(["domain": .string("quark")]))
        _ = try await client.call(module: .film, operation: "data.invalidate", parameters: .object(["domain": .string("source")]))
        let evidence = try await FilmBridgeConnection.read(controlURL.appendingPathComponent("evidence"))
        let result = FilmBridgeConnection(client: client, controlURL: controlURL, source: source, target: target,
            recordCount: evidence.catField("records")?.catArray?.count ?? 0)
        addTeardownBlock {
            _ = try await result.control("scenario", body: .object(["scenario": .string("success")]))
            await client.close()
        }
        return result
    }

    private func makeModel(_ bridge: FilmBridgeConnection, media: FilmUIKitPlaybackMedia,
                           target: FilmPlaybackTarget) -> FilmPlaybackModel {
        let request = FilmPlaybackRequest(target: target, sources: [bridge.source], entrySurface: "search")
        let model = FilmPlaybackModel(transport: FilmPlaybackTransport { bridge.client }, media: media, request: { request })
        addTeardownBlock { await model.close() }
        return model
    }

    private func detailPage(_ model: FilmPlaybackModel) throws -> FilmDetailViewController {
        let suite = "FilmCatPanBridgeConsumerTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        addTeardownBlock { defaults.removePersistentDomain(forName: suite) }
        return FilmDetailViewController(detail: .init(item: .init(sourceID: "fixture", id: "bridge-detail", title: "Bridge Series")),
            library: FilmLibrary(defaults: defaults),
            images: ArtworkImages(maximumBytes: 1_024) { _ in
                XCTFail("This directory has no artwork"); throw URLError(.resourceUnavailable)
            }, themeProvider: FilmPlaybackUIKitTheme(), model: model)
    }

    private func mount(_ page: PageViewController) throws {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let previous = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.rootViewController = page
        window.makeKeyAndVisible(); window.layoutIfNeeded()
        addTeardownBlock {
            PageViewController.invalidatePages(in: page)
            window.isHidden = true; window.rootViewController = nil
            previous?.makeKey()
        }
    }

    private func button(_ id: String, _ page: UIViewController) throws -> UIButton {
        try XCTUnwrap(descendants(page.view).compactMap { $0 as? UIButton }.first { $0.accessibilityIdentifier == id }, id)
    }
    private func descendants(_ view: UIView) -> [UIView] { view.subviews.flatMap { [$0] + descendants($0) } }
    private func action(_ title: String, _ menu: UIMenu?) throws -> UIAction {
        try XCTUnwrap(menu?.children.compactMap { $0 as? UIAction }.first { $0.title == title }, title)
    }
    private func invoke(_ action: UIAction) {
        let button = UIButton(type: .system)
        button.addAction(action, for: .touchUpInside); button.sendActions(for: .touchUpInside)
    }
    private func waitFor(_ predicate: @escaping @MainActor () -> Bool) async throws {
        let ready = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            MainActor.assumeIsolated { predicate() }
        }, object: nil)
        let result = await XCTWaiter.fulfillment(of: [ready], timeout: 10)
        XCTAssertEqual(result, .completed)
        guard result == .completed else { throw FilmPlaybackError(code: "FIXTURE_NOT_READY") }
    }
}

private struct FilmBridgeConnection: Sendable {
    let client: RuntimeHTTPClient
    let controlURL: URL
    let source: FilmPlaybackSource
    let target: FilmPlaybackTarget
    let recordCount: Int

    func control(_ path: String, body: RuntimeValue? = nil) async throws -> RuntimeValue {
        try await Self.read(controlURL.appendingPathComponent(path), body: body)
    }
    static func read(_ url: URL, body: RuntimeValue? = nil) async throws -> RuntimeValue {
        var request = URLRequest(url: url)
        request.timeoutInterval = 10
        if let body {
            request.httpMethod = "POST"; request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try RuntimeJSON.encode(body)
        }
        let (bytes, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
        return try RuntimeJSON.decode(bytes)
    }
}
#endif
