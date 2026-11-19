import XCTest
import UIKit
import MeowMusicPlatform
import MeowMusicNativeService
import MeowUI
@testable import MeowMusicFeature
@testable import Meowfilm

/// Opt-in anonymous upstream reads and native rendering inside the actual App host.
@MainActor
final class MusicContentLiveHostTests: XCTestCase {
    func testAnonymousHomePlaylistSquareAndElectronicRenderThroughEmbeddedRuntime() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_LIVE_MUSIC_CONTENT"] == "1" else {
            throw XCTSkip("Run anonymous content acceptance explicitly.")
        }
        let resources = try await hostResources()
        let accounts = try XCTUnwrap(resources.musicAccounts)
        let statuses = try await accounts.statuses()
        XCTAssertTrue(statuses.allSatisfy { $0.phase == .signedOut }, "This test cannot borrow a saved account")
        guard statuses.allSatisfy({ $0.phase == .signedOut }) else { return }
        let library = MusicStartupLibrary(failure: nil)
        let service = NativeMusicService(database: library, platformClient: resources.musicPlatform)
        try await service.setAccess(available: true, generation: 1)
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let model = MusicViewModel(service: service, playback: assembly.playback)
        addTeardownBlock { @MainActor in await model.close() }
        await model.load()
        XCTAssertNil(model.error)
        let router = ContentRouter()
        let directory = try await service.publicDiscovery(.channels, cursor: nil)
        let channels = directory.sections.flatMap(\.items).compactMap { item -> MusicPublicBrowseChannel? in
            if case .channel(let value) = item.content { return value }
            return nil
        }
        let square = try XCTUnwrap(channels.first { $0.kind == .playlists })
        let electronic = try XCTUnwrap(channels.first { $0.title.contains("电音") })
        let sceneReady = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive } }
        }, evaluatedWith: nil)
        await fulfillment(of: [sceneReady], timeout: 10)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let previous = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        addTeardownBlock { @MainActor in
            if let page = window.rootViewController { PageViewController.invalidatePages(in: page) }
            window.isHidden = true; window.rootViewController = nil; previous?.makeKey()
        }
        for (name, title, destination) in [("home", "首页", MusicPublicBrowseDestination.home),
                                          ("playlist-square", "歌单广场", .channel(square, tag: "")),
                                          ("electronic", "电音", .channel(electronic, tag: ""))] {
            let page = MusicBrowseViewController(title: title, model: model, browser: service,
                request: .discovery(destination), appearance: assembly.appearance,
                router: router, embedded: true)
            if let previousPage = window.rootViewController { PageViewController.invalidatePages(in: previousPage) }
            window.rootViewController = page
            window.makeKeyAndVisible()
            let collection = try XCTUnwrap(descendants(page.view).compactMap { $0 as? UICollectionView }.first)
            let loaded = expectation(for: NSPredicate { _, _ in
                MainActor.assumeIsolated {
                    page.view.layoutIfNeeded(); collection.layoutIfNeeded()
                    return collection.numberOfSections > 0 && !collection.visibleCells.isEmpty
                }
            }, evaluatedWith: collection)
            await fulfillment(of: [loaded], timeout: 30)
            XCTAssertNil(page.presentedViewController, "An ordinary content read must not show a format error")
            if name == "electronic" {
                let banner = try XCTUnwrap(collection.visibleCells.compactMap { $0 as? MusicBannerCarouselCell }.first)
                XCTAssertEqual(banner.bounds.height, banner.bounds.width / 2.56, accuracy: 0.5)
                XCTAssertTrue(descendants(banner).compactMap { $0 as? UIScrollView }.contains { $0.isPagingEnabled })
            }
            let decoded = expectation(for: NSPredicate { _, _ in
                MainActor.assumeIsolated {
                    collection.layoutIfNeeded()
                    return collection.visibleCells.flatMap { self.descendants($0) }
                        .compactMap { $0 as? MusicArtworkView }.contains { $0.image != nil }
                }
            }, evaluatedWith: collection)
            await fulfillment(of: [decoded], timeout: 25)
            let images = collection.visibleCells.flatMap { descendants($0) }.compactMap { $0 as? MusicArtworkView }
            print("MUSIC_CONTENT page=\(name) visibleArtwork=\(images.count) decoded=\(images.filter { $0.image != nil }.count)")
            let image = UIGraphicsImageRenderer(bounds: window.bounds).image { _ in
                window.drawHierarchy(in: window.bounds, afterScreenUpdates: true)
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = "music-content-" + name; attachment.lifetime = .keepAlways
            add(attachment)
        }
        XCTAssertTrue(model.playbackState.queue.isEmpty, "Content rendering cannot start playback")
        let finalStatuses = try await accounts.statuses()
        XCTAssertTrue(finalStatuses.allSatisfy { $0.phase == .signedOut })
    }

    private func descendants(_ view: UIView) -> [UIView] {
        [view] + view.subviews.flatMap { descendants($0) }
    }
}

@MainActor
private final class ContentRouter: MusicUIRouting {
    let primaryPlaybackPresentation = MusicMiniPlayerPresentation()
    func showSearch() { XCTFail("No search in content acceptance") }
    func showSettings() { XCTFail("No settings in content acceptance") }
    func showPublicBrowse(_ request: MusicPublicBrowseRequest, title: String) { XCTFail("No playback in content acceptance") }
    func showPublicCollection(_ collection: MusicPublicBrowseCollection) { XCTFail("No collection expansion in content acceptance") }
    func showLocalCollection(_ collection: MusicCollection) { XCTFail("No local collection in content acceptance") }
    func showPlayer(_ destination: MusicPlaybackDestination) { XCTFail("No player in content acceptance") }
    func selectChannel(_ channel: MusicPublicBrowseChannel) { XCTFail("No synthetic navigation in content acceptance") }
}
