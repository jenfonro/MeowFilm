#if os(iOS)
import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature

@MainActor
final class MusicBrowseLoadingTests: XCTestCase {
    func testLoadingUsesExistingPlaySlotAndSurvivesRetainedCardConfiguration() throws {
        let album = MusicPublicBrowseCollection(platform: .netease, nativeID: "album", title: "专辑")
        let cases: [(MusicPublicBrowseSection.Layout, MusicPublicBrowseContent)] = [
            (.featureRail, .action(.fm(mode: .default, submode: "", entry: nil))),
            (.featureRail, .action(.similar(sourceIDs: ["41"], sourceName: "歌曲"))),
            (.featureRail, .action(.heartbeat)),
            (.playlistRail, .collection(album)),
            (.rankingStack, .collection(album)),
            (.artistUpdates, .collection(album)),
            (.list, .track(track("单曲"))),
        ]
        let host = UIViewController(), window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = host; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        for (layout, content) in cases {
            let cell = MusicBrowseCell(frame: CGRect(x: 16, y: 100, width: 300, height: 220))
            host.view.addSubview(cell)
            defer { cell.removeFromSuperview() }
            let item = MusicPublicBrowseItem(id: "origin", title: "播放入口", content: content)
            var plays = 0
            cell.configure(item: item, layout: layout, theme: PageTheme(), onPlay: { plays += 1 })
            cell.layoutIfNeeded()
            let play = try button("播放播放入口", in: cell)
            let spinner = try XCTUnwrap(play.subviews.compactMap { $0 as? UIActivityIndicatorView }.first)
            let frame = play.frame, contentBounds = cell.contentView.bounds
            XCTAssertFalse(spinner.isAnimating)
            cell.setPlaybackLoading(.card, visible: true)
            cell.layoutIfNeeded()
            XCTAssertTrue(spinner.isAnimating)
            XCTAssertNil(play.image(for: .normal))
            XCTAssertEqual(play.accessibilityValue, "加载中")
            XCTAssertEqual(spinner.color, play.tintColor)
            XCTAssertEqual(play.frame, frame)
            XCTAssertEqual(cell.contentView.bounds, contentBounds)
            XCTAssertEqual(spinner.convert(spinner.bounds, to: play).midX, play.bounds.midX, accuracy: 0.5)
            XCTAssertEqual(spinner.convert(spinner.bounds, to: play).midY, play.bounds.midY, accuracy: 0.5)
            cell.configure(item: item, layout: layout, theme: PageTheme(), onPlay: { plays += 1 })
            XCTAssertTrue(spinner.isAnimating)
            XCTAssertNil(play.image(for: .normal), "Retaining the same card must not restore a play glyph over its spinner")
            cell.setPlaybackLoading(nil, visible: true)
            XCTAssertFalse(spinner.isAnimating)
            XCTAssertTrue(play.isEnabled)
            XCTAssertNotNil(play.image(for: .normal))
            XCTAssertNil(play.accessibilityValue)
            play.sendActions(for: .touchUpInside)
            XCTAssertEqual(plays, 1)
        }
    }

    func testCardLoadingStopsWhenHiddenDetachedOrReusedAndCannotLeakToReplacement() throws {
        let host = UIViewController(), window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        let cell = MusicBrowseCell(frame: CGRect(x: 16, y: 100, width: 220, height: 260))
        let item = MusicPublicBrowseItem(id: "card", title: "FM",
                                        content: .action(.fm(mode: .default, submode: "", entry: nil)))
        cell.configure(item: item, layout: .featureRail, theme: PageTheme(), onPlay: {})
        cell.setPlaybackLoading(.card, visible: true)
        let play = try button("播放FM", in: cell)
        let spinner = try XCTUnwrap(play.subviews.compactMap { $0 as? UIActivityIndicatorView }.first)
        XCTAssertFalse(spinner.isAnimating)
        host.view.addSubview(cell); window.rootViewController = host; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        XCTAssertTrue(spinner.isAnimating)
        cell.setDisplayVisible(false)
        XCTAssertFalse(spinner.isAnimating)
        cell.setDisplayVisible(true)
        XCTAssertTrue(spinner.isAnimating)
        cell.isHidden = true
        XCTAssertFalse(spinner.isAnimating)
        cell.isHidden = false
        XCTAssertTrue(spinner.isAnimating)
        cell.removeFromSuperview()
        XCTAssertFalse(spinner.isAnimating)
        host.view.addSubview(cell)
        XCTAssertTrue(spinner.isAnimating)
        cell.prepareForReuse()
        XCTAssertFalse(spinner.isAnimating)
        XCTAssertTrue(play.isEnabled)
        XCTAssertNotNil(play.image(for: .normal))
        cell.configure(item: .init(id: "replacement", title: "另一首", content: .track(track("另一首"))),
                       layout: .list, theme: PageTheme(), onPlay: {})
        cell.setDisplayVisible(true)
        XCTAssertFalse(spinner.isAnimating)
        XCTAssertNil(play.accessibilityValue)
    }

    func testPreviewLoadingUsesExactOccurrenceAndStopsWithParentWithoutCollapsingArtwork() throws {
        let songID = UUID().uuidString
        let first = track("first", songID: songID, occurrence: "first")
        let second = track("second", songID: songID, occurrence: "second")
        let host = UIViewController(), window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        let cell = MusicBrowseCell(frame: CGRect(x: 16, y: 100, width: 320, height: 210))
        host.view.addSubview(cell); window.rootViewController = host; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        var played: [MusicReference] = []
        cell.configure(item: .init(id: "previews", title: "推荐", content: .unavailable, previewSongs: [first, second]),
                       layout: .rankedSongs, theme: PageTheme(), onPreview: { played.append($0.ref) }, onPlay: {})
        cell.layoutIfNeeded()
        let firstRow = try button("播放first", in: cell), secondRow = try button("播放second", in: cell)
        let firstSpinner = try XCTUnwrap(firstRow.subviews.compactMap { $0 as? UIActivityIndicatorView }.first)
        let secondSpinner = try XCTUnwrap(secondRow.subviews.compactMap { $0 as? UIActivityIndicatorView }.first)
        let cover = try XCTUnwrap(descendants(secondRow).compactMap { $0 as? MusicArtworkView }.first)
        let frame = cover.frame, rowFrame = secondRow.frame
        cell.setPlaybackLoading(.preview(second.ref), visible: true)
        cell.layoutIfNeeded()
        XCTAssertFalse(firstSpinner.isAnimating)
        XCTAssertTrue(firstRow.isEnabled)
        XCTAssertTrue(secondSpinner.isAnimating)
        XCTAssertEqual(cover.alpha, 0)
        XCTAssertEqual(cover.frame, frame)
        XCTAssertEqual(secondRow.frame, rowFrame)
        cell.setDisplayVisible(false)
        XCTAssertFalse(secondSpinner.isAnimating)
        XCTAssertEqual(cover.alpha, 1)
        cell.setDisplayVisible(true)
        XCTAssertTrue(secondSpinner.isAnimating)
        cell.isHidden = true
        XCTAssertFalse(secondSpinner.isAnimating)
        cell.isHidden = false
        XCTAssertTrue(secondSpinner.isAnimating)
        cell.prepareForReuse()
        cell.setDisplayVisible(true)
        XCTAssertFalse(firstSpinner.isAnimating)
        XCTAssertFalse(secondSpinner.isAnimating)
        XCTAssertTrue(secondRow.isEnabled)
        XCTAssertNil(secondRow.accessibilityValue)
        XCTAssertEqual(cover.alpha, 1)
        secondRow.sendActions(for: .touchUpInside)
        XCTAssertEqual(played, [second.ref])
    }

    private func track(_ title: String, songID: String = UUID().uuidString, occurrence: String? = nil) -> MusicRemoteTrack {
        .init(ref: .init(kind: .library, id: songID, occurrence: occurrence), songId: songID, title: title)
    }
    private func descendants(_ view: UIView) -> [UIView] { view.subviews.flatMap { [$0] + descendants($0) } }
    private func button(_ label: String, in view: UIView) throws -> UIButton {
        try XCTUnwrap(descendants(view).compactMap { $0 as? UIButton }.first { $0.accessibilityLabel == label })
    }
}
#endif
