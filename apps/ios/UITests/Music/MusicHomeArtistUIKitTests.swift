#if os(iOS)
import XCTest
import UIKit
import MeowUI
import MeowPlayback
@testable import MeowMusicFeature

@MainActor
final class MusicHomeArtistUIKitTests: XCTestCase {
    func testArtistReleaseCardKeepsArtworkCopyCountAndPlayInsideOneCompactSurface() throws {
        let album = MusicPublicBrowseCollection(platform: .netease, nativeID: "42", kind: .album,
            title: "留不住的夏天", creator: "黄霄雲", count: 2)
        let item = MusicPublicBrowseItem(id: album.id, title: album.title, subtitle: album.creator,
            content: .collection(album), coverLabel: "新专辑")
        for width in [CGFloat(240), 300, 356] {
            let cell = MusicBrowseCell(frame: CGRect(x: 0, y: 0, width: width, height: width / 2.9))
            var plays = 0
            cell.configure(item: item, layout: .artistUpdates, theme: PageTheme()) { plays += 1 }
            cell.layoutIfNeeded()
            let art = try XCTUnwrap(descendants(cell).compactMap { $0 as? MusicArtworkView }.first)
            let title = try label(album.title, in: cell)
            let artist = try label(album.creator, in: cell)
            let count = try label("2 首歌曲", in: cell)
            let badge = try label("新专辑", in: cell)
            let play = try playButton(item.id, in: cell)
            XCTAssertEqual(art.frame.width, art.frame.height, accuracy: 0.5)
            XCTAssertEqual(art.frame.height, cell.bounds.height, accuracy: 0.5)
            XCTAssertEqual(art.frame.origin, .zero)
            XCTAssertGreaterThan(title.frame.minX, art.frame.maxX)
            XCTAssertGreaterThan(title.frame.width, 0)
            XCTAssertLessThanOrEqual(title.frame.maxY, artist.frame.minY)
            XCTAssertLessThan(artist.frame.maxY, count.frame.minY)
            XCTAssertLessThanOrEqual(title.frame.maxY, play.frame.minY)
            XCTAssertLessThanOrEqual(artist.frame.maxX, play.frame.minX)
            XCTAssertLessThanOrEqual(count.frame.maxX, play.frame.minX)
            for label in [title, artist, count] {
                XCTAssertTrue(cell.contentView.bounds.contains(label.frame))
            }
            XCTAssertTrue(art.frame.contains(badge.frame))
            XCTAssertTrue(cell.contentView.bounds.contains(play.frame))
            XCTAssertGreaterThanOrEqual(play.bounds.width, 44)
            XCTAssertGreaterThanOrEqual(play.bounds.height, 44)
            XCTAssertEqual(play.backgroundColor, .clear)
            XCTAssertTrue(cell.contentView.clipsToBounds)
            XCTAssertEqual(cell.contentView.backgroundColor, .clear)
            XCTAssertEqual(art.backgroundColor, .clear)
            XCTAssertNil(art.image)
            XCTAssertTrue(cell.hitTest(play.center, with: nil) === play)
            play.sendActions(for: .touchUpInside)
            XCTAssertEqual(plays, 1)
        }
    }

    func testArtistPanelRendersHorizontalGradientWhileArtworkStaysBlankUntilImageArrives() async throws {
        let urls = try await artworkURLs()
        let album = MusicPublicBrowseCollection(platform: .netease, nativeID: "gradient", kind: .album,
            title: "渐变专辑", creator: "艺人", count: 2)
        for width in [CGFloat(240), 300, 356] {
            let cell = MusicBrowseCell(frame: CGRect(x: 0, y: 0, width: width, height: width / 2.9))
            cell.configure(item: .init(id: album.id, title: album.title, subtitle: album.creator,
                                      content: .collection(album)),
                           layout: .artistUpdates, theme: PageTheme(), onPlay: {})
            cell.layoutIfNeeded()
            let art = try XCTUnwrap(descendants(cell).compactMap { $0 as? MusicArtworkView }.first)
            let gradient = try gradient(in: cell)
            // CALayer derives frame from position/bounds, introducing subpixel rounding.
            XCTAssertEqual(gradient.frame.minX, art.frame.maxX, accuracy: 1e-6)
            XCTAssertEqual(gradient.frame.maxX, width, accuracy: 1e-6)
            XCTAssertEqual(gradient.frame.minY, 0)
            XCTAssertEqual(gradient.frame.height, cell.bounds.height)
            XCTAssertEqual(gradient.startPoint, CGPoint(x: 0, y: 0.5))
            XCTAssertEqual(gradient.endPoint, CGPoint(x: 1, y: 0.5))
            let coverPoint = CGPoint(x: art.frame.midX, y: art.frame.midY)
            let initialColors = try XCTUnwrap(gradient.colors as? [CGColor])
            for url in [nil, urls[0], urls[1], nil] as [URL?] {
                cell.updateArtwork(url)
                cell.layoutIfNeeded()
                XCTAssertEqual(art.backgroundColor, .clear)
                XCTAssertEqual(art.layer.cornerRadius, 0)
                let image = render(cell.contentView)
                let coverColor = try color(in: image, at: coverPoint)
                if let url {
                    let source = try XCTUnwrap(art.image)
                    XCTAssertTrue(source === MusicArtworkImages.shared.cached(url))
                    XCTAssertNotEqual(try XCTUnwrap(gradient.colors as? [CGColor]), initialColors)
                    let sourceColor = try color(in: source, at: coverPoint)
                    for channel in zip(rgb(coverColor), rgb(sourceColor)) {
                        XCTAssertEqual(channel.0, channel.1, accuracy: 0.015)
                    }
                } else {
                    XCTAssertNil(art.image)
                    XCTAssertEqual(gradient.colors as? [CGColor], initialColors)
                    XCTAssertEqual(coverColor.cgColor.alpha, 0, accuracy: 0.01)
                }
                let y = cell.bounds.height - 6
                let points = [art.frame.maxX + 6, (art.frame.maxX + width) / 2, width - 16]
                let tones = try points.map { try luminance(color(in: image, at: CGPoint(x: $0, y: y))) }
                XCTAssertGreaterThan(tones[1], tones[0] + 0.025)
                XCTAssertGreaterThan(tones[2], tones[1] + 0.025)
                XCTAssertGreaterThan(tones[2], tones[0] + 0.08,
                    "The rendered panel must visibly lighten towards the right, not remain a flat color")
            }
        }
    }

    func testArtistCardReuseClearsReleaseOnlyPresentationAndKeepsOrdinaryCardsUnchanged() throws {
        let cell = MusicBrowseCell(frame: CGRect(x: 0, y: 0, width: 300, height: 300 / 2.9))
        let album = MusicPublicBrowseCollection(platform: .netease, nativeID: "42", kind: .album,
            title: "单曲专辑", count: 1)
        cell.configure(item: .init(id: album.id, title: album.title, content: .collection(album),
                                  coverLabel: "新单曲"),
                       layout: .artistUpdates, theme: PageTheme(), onPlay: {})
        cell.layoutIfNeeded()
        let badge = try label("新单曲", in: cell)
        XCTAssertFalse(badge.isHidden)
        let withoutCount = MusicPublicBrowseCollection(platform: .netease, nativeID: "43",
            kind: .album, title: "未知曲数")
        cell.configure(item: .init(id: withoutCount.id, title: withoutCount.title, content: .collection(withoutCount)),
                       layout: .artistUpdates, theme: PageTheme(), onPlay: {})
        cell.layoutIfNeeded()
        XCTAssertTrue(badge.isHidden)
        XCTAssertFalse(descendants(cell).compactMap { $0 as? UILabel }.contains { $0.text == "1 首歌曲" })
        let playlist = MusicPublicBrowseCollection(platform: .netease, nativeID: "44", title: "普通歌单")
        cell.frame.size.height = 342
        cell.configure(item: .init(id: playlist.id, title: playlist.title, content: .collection(playlist)),
                       layout: .playlistRail, theme: PageTheme(), onPlay: {})
        cell.layoutIfNeeded()
        XCTAssertTrue(badge.isHidden)
        XCTAssertEqual(cell.contentView.backgroundColor, .clear)
        XCTAssertFalse(cell.contentView.clipsToBounds)
        let art = try XCTUnwrap(descendants(cell).compactMap { $0 as? MusicArtworkView }.first)
        let gradient = try gradient(in: cell)
        XCTAssertEqual(art.backgroundColor, UIColor.white.withAlphaComponent(0.18))
        XCTAssertTrue(gradient.isHidden)
        XCTAssertEqual(gradient.frame, .zero)
        XCTAssertEqual(try playButton(playlist.id, in: cell).backgroundColor,
                       UIColor.white.withAlphaComponent(0.88))
        for layout in [MusicPublicBrowseSection.Layout.featureRail, .banner] {
            cell.configure(item: .init(id: playlist.id, title: playlist.title, content: .collection(playlist)),
                           layout: .artistUpdates, theme: PageTheme(), onPlay: {})
            cell.configure(item: .init(id: playlist.id, title: playlist.title, content: .collection(playlist)),
                           layout: layout, theme: PageTheme(), onPlay: {})
            cell.layoutIfNeeded()
            XCTAssertFalse(gradient.isHidden)
            XCTAssertEqual(gradient.startPoint, CGPoint(x: 0.5, y: 0))
            XCTAssertEqual(gradient.endPoint, CGPoint(x: 0.5, y: 1))
            XCTAssertEqual(gradient.frame, art.frame)
            XCTAssertEqual(gradient.cornerRadius, 8)
            XCTAssertEqual(art.layer.cornerRadius, layout == .banner ? 0 : 8)
        }
    }

    func testArtistHeaderPutsAvatarsBeforeTitleAndPreservesChannelAction() async throws {
        let urls = try await artworkURLs()
        let channel = MusicPublicBrowseChannel(id: "artist-activity", title: "你关注的艺人新动向",
                                               kind: .artistActivity)
        for width in [CGFloat(280), 358] {
            let header = MusicBrowseHeader(frame: CGRect(x: 0, y: 0, width: width, height: 44))
            var opens = 0, plays = 0
            header.configure(section: .init(id: "artists", title: channel.title, items: [],
                layout: .artistUpdates, avatars: urls, channel: channel),
                theme: PageTheme(), showsSectionPlayback: false, onPlay: { plays += 1 },
                onChannel: { opens += 1 }, onChoice: { _ in }, onRetry: {}, error: nil)
            header.layoutIfNeeded()
            let avatars = descendants(header).compactMap { $0 as? MusicArtworkView }.filter { !$0.isHidden }
            let title = try label(channel.title, in: header)
            XCTAssertEqual(avatars.count, 3)
            XCTAssertEqual(avatars.first?.frame.minX, 0)
            XCTAssertLessThan(try XCTUnwrap(avatars.last).frame.maxX, title.frame.minX)
            XCTAssertGreaterThan(title.frame.width, 0)
            let entry = try XCTUnwrap(descendants(header).compactMap { $0 as? UIButton }.first {
                $0.accessibilityIdentifier == "music.browse.channel.artists"
            })
            entry.sendActions(for: .touchUpInside)
            XCTAssertEqual(opens, 1)
            XCTAssertEqual(plays, 0)
        }
    }

    func testArtistAlbumRailUsesCompactHeightAndSeparatesOpenFromExplicitPlayback() async throws {
        let urls = try await artworkURLs()
        for count in [1, 8] {
            let openedQueue = expectation(description: "explicit album play reaches queue owner")
            let album = MusicPublicBrowseCollection(platform: .netease, nativeID: "42", kind: .album,
                title: count == 1 ? "留不住的夏天" : "宇宙回音", creator: "艺人",
                artwork: count == 1 ? nil : urls[0], count: count)
            let service = ArtistHomeUIKitService(album: album, avatars: urls) { openedQueue.fulfill() }
            let model = MusicViewModel(service: service, playback: PlaybackController())
            addTeardownBlock { await model.close() }
            await model.load()
            let router = ArtistHomeUIKitRouter()
            let controller = MusicBrowseViewController(title: "首页", model: model, browser: service,
                request: .discovery(.home), appearance: MusicAppearancePreferencesStore(),
                router: router, embedded: true)
            let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
            let window = scene.map { UIWindow(windowScene: $0) } ??
                UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
            controller.loadViewIfNeeded()
            window.rootViewController = controller
            window.makeKeyAndVisible()
            window.layoutIfNeeded()
            controller.view.layoutIfNeeded()
            NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
            let collection = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UICollectionView }.first)
            let path = IndexPath(item: 0, section: 0)
            let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
                collection.layoutIfNeeded()
                return controller.isPageVisible && collection.cellForItem(at: path) != nil
            } }, evaluatedWith: nil)
            await fulfillment(of: [ready], timeout: 4)
            defer {
                PageViewController.invalidatePages(in: controller)
                window.isHidden = true
                window.rootViewController = nil
            }
            let cell = try XCTUnwrap(collection.cellForItem(at: path))
            XCTAssertEqual(cell.bounds.width / cell.bounds.height, 2.9, accuracy: 0.01)
            XCTAssertEqual(try label("\(count) 首歌曲", in: cell).text, "\(count) 首歌曲")
            XCTAssertTrue(model.queue.isEmpty)
            let before = await service.requests()
            XCTAssertTrue(before.details.isEmpty)
            XCTAssertTrue(before.queues.isEmpty)
            controller.collectionView(collection, didSelectItemAt: path)
            XCTAssertEqual(router.collections, [album])
            let afterOpen = await service.requests()
            XCTAssertTrue(afterOpen.details.isEmpty, "The card routes to the shared detail rather than playing")
            XCTAssertTrue(afterOpen.queues.isEmpty)
            attach(controller.view, name: count == 1 ? "home-artist-gradient-empty-cover" : "home-artist-gradient-loaded-cover")
            try playButton(album.id, in: cell).sendActions(for: .touchUpInside)
            await fulfillment(of: [openedQueue], timeout: 4)
            let afterPlay = await service.requests()
            XCTAssertEqual(afterPlay.details, [album])
            XCTAssertEqual(afterPlay.queues, [album])
            XCTAssertEqual(router.collections, [album], "The play button must not also navigate")
            XCTAssertEqual(afterPlay.searches, 0)
            await model.close()
        }
    }

    private func artworkURLs() async throws -> [URL] {
        let colors: [UIColor] = [UIColor(red: 0.62, green: 0.53, blue: 0.36, alpha: 1), .systemTeal, .systemPink]
        var urls: [URL] = []
        for (index, color) in colors.enumerated() {
            let url = URL(string: "https://artist-home.example/\(index).png")!
            let image = UIGraphicsImageRenderer(size: CGSize(width: 120, height: 120)).image { context in
                color.setFill()
                context.fill(CGRect(x: 0, y: 0, width: 120, height: 120))
            }
            _ = try await MusicArtworkImages.shared.read(url, cachedData: XCTUnwrap(image.pngData()))
            urls.append(url)
        }
        return urls
    }

    private func label(_ text: String, in view: UIView) throws -> UILabel {
        try XCTUnwrap(descendants(view).compactMap { $0 as? UILabel }.first { $0.text == text })
    }

    private func playButton(_ item: String, in view: UIView) throws -> UIButton {
        try XCTUnwrap(descendants(view).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.browse.play.\(item)"
        })
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }

    private func gradient(in cell: MusicBrowseCell) throws -> CAGradientLayer {
        try XCTUnwrap(cell.contentView.layer.sublayers?.compactMap { $0 as? CAGradientLayer }.first {
            $0.name == "music.browse.text-gradient"
        })
    }

    private func render(_ view: UIView) -> UIImage {
        UIGraphicsImageRenderer(size: view.bounds.size).image { view.layer.render(in: $0.cgContext) }
    }

    private func color(in image: UIImage, at point: CGPoint) throws -> UIColor {
        let pixel = try XCTUnwrap(image.cgImage?.cropping(to: CGRect(
            x: floor(point.x * image.scale), y: floor(point.y * image.scale), width: 1, height: 1)))
        var rgba = [UInt8](repeating: 0, count: 4)
        try rgba.withUnsafeMutableBytes { bytes in
            let context = try XCTUnwrap(CGContext(data: bytes.baseAddress, width: 1, height: 1,
                bitsPerComponent: 8, bytesPerRow: 4, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            context.draw(pixel, in: CGRect(x: 0, y: 0, width: 1, height: 1))
        }
        return UIColor(red: CGFloat(rgba[0]) / 255, green: CGFloat(rgba[1]) / 255,
                       blue: CGFloat(rgba[2]) / 255, alpha: CGFloat(rgba[3]) / 255)
    }

    private func rgb(_ color: UIColor) -> [CGFloat] {
        var red: CGFloat = 0, green: CGFloat = 0, blue: CGFloat = 0, alpha: CGFloat = 0
        XCTAssertTrue(color.getRed(&red, green: &green, blue: &blue, alpha: &alpha))
        return [red, green, blue]
    }

    private func luminance(_ color: UIColor) -> CGFloat {
        let channels = rgb(color)
        return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2]
    }

    private func attach(_ view: UIView, name: String) {
        view.layoutIfNeeded()
        let attachment = XCTAttachment(image: render(view))
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}

@MainActor
private final class ArtistHomeUIKitRouter: MusicUIRouting {
    let primaryPlaybackPresentation = MusicMiniPlayerPresentation()
    var collections: [MusicPublicBrowseCollection] = []
    func showSearch() { XCTFail("Unexpected navigation") }
    func showSettings() { XCTFail("Unexpected navigation") }
    func showPublicBrowse(_ request: MusicPublicBrowseRequest, title: String) { XCTFail("Unexpected navigation") }
    func showPublicCollection(_ collection: MusicPublicBrowseCollection) { collections.append(collection) }
    func showLocalCollection(_ collection: MusicCollection) { XCTFail("Unexpected navigation") }
    func showPlayer(_ destination: MusicPlaybackDestination) { XCTFail("Unexpected navigation") }
    func selectChannel(_ channel: MusicPublicBrowseChannel) { XCTFail("Unexpected navigation") }
}

private actor ArtistHomeUIKitService: MusicServing, MusicAccountBrowsing, MusicPublicBrowsing, MusicPublicBrowseQueueLoading {
    let album: MusicPublicBrowseCollection
    let avatars: [URL]
    let onOpen: @Sendable () -> Void
    private var details: [MusicPublicBrowseCollection] = [], queues: [MusicPublicBrowseCollection] = []
    private var searches = 0
    init(album: MusicPublicBrowseCollection, avatars: [URL], onOpen: @escaping @Sendable () -> Void) {
        self.album = album; self.avatars = avatars; self.onOpen = onOpen
    }
    func requests() -> (details: [MusicPublicBrowseCollection], queues: [MusicPublicBrowseCollection], searches: Int) {
        (details, queues, searches)
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) -> MusicPublicBrowsePage {
        .init(sections: [.init(id: "artists", title: "你关注的艺人新动向", items: [
            .init(id: album.id, title: album.title, subtitle: album.creator, artwork: album.artwork,
                  content: .collection(album), coverLabel: album.count == 1 ? "新单曲" : "新专辑"),
            .init(id: "next-release", title: "下一张专辑", artwork: avatars[1], content: .collection(
                .init(platform: .netease, nativeID: "43", kind: .album, title: "下一张专辑", count: 2)))
        ], layout: .artistUpdates, avatars: avatars,
            channel: .init(id: "artist-activity", title: "你关注的艺人新动向", kind: .artistActivity))])
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) -> MusicPublicBrowseDetail {
        details.append(collection)
        let track = MusicRemoteTrack(ref: .init(kind: .handle, id: "wy:41"), songId: "41", title: "曲目",
            sources: [.init(sourceKey: "wy:41", platform: "wy", nativeId: "41")])
        return .init(collection: collection, members: [.init(nativeID: "41", sourcePosition: 0, track: track)],
                     total: 1, cursor: nil, snapshot: "artist-album")
    }
    func openPublicQueue(_ request: MusicPublicBrowseQueueRequest) throws -> MusicRemoteQueueSource {
        queues.append(request.collection); onOpen()
        throw MusicServiceError.unsupported
    }
    func publicQueueTracks(_ request: MusicPublicBrowseQueueRequest) throws -> [MusicNativeTrackData] {
        throw MusicServiceError.unsupported
    }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws
        -> MusicPublicBrowseCollectionPage { throw MusicServiceError.unsupported }
    func recommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicBrowsePage {
        XCTFail("Artist cards cannot read account recommendations"); throw MusicServiceError.unsupported
    }
    func accountCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage {
        XCTFail("Artist cards cannot read account collections"); throw MusicServiceError.unsupported
    }
    func accountTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicBrowsePage {
        XCTFail("Artist cards cannot expand account collections"); throw MusicServiceError.unsupported
    }
    func state() -> MusicPlaybackState { .init(revision: 0, currentId: nil, total: 0) }
    func queue() -> MusicQueueSnapshot { .init(revision: 0, currentId: nil, entries: []) }
    func platforms() -> [MusicPlatform] { [] }
    func collections() -> [MusicCollection] { [] }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage {
        searches += 1; throw MusicServiceError.unsupported
    }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws
        -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState {
        throw MusicServiceError.unsupported
    }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws
        -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func sourceAvailability(for ref: MusicReference) -> MusicSourceAvailability { .init(options: [], scriptState: .notConfigured) }
    func metadataPreferences() -> MusicMetadataPreferences {
        .init(revision: 0, platforms: [.netease], enabledPlatforms: [.netease])
    }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences {
        throw MusicServiceError.unsupported
    }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws
        -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}
#endif
