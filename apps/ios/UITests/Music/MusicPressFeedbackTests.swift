#if os(iOS)
import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature

@MainActor
final class MusicPressFeedbackTests: XCTestCase {
    func testSharedFeedbackIsImmediateIdempotentAndRestoresOriginalVisuals() {
        let view = UIView()
        let original = CGAffineTransform(translationX: 8, y: 4)
        view.transform = original
        view.alpha = 0.7
        let feedback = PressFeedback(view: view, scale: 0.96)

        feedback.setPressed(true)
        let expected = UIAccessibility.isReduceMotionEnabled ? original : original.scaledBy(x: 0.96, y: 0.96)
        XCTAssertEqual(view.transform, expected)
        XCTAssertLessThan(view.alpha, 0.7)
        let pressedAlpha = view.alpha
        feedback.setPressed(true)
        XCTAssertEqual(view.transform, expected)
        XCTAssertEqual(view.alpha, pressedAlpha)
        XCTAssertTrue(view.gestureRecognizers?.isEmpty ?? true)
        XCTAssertTrue(view.isUserInteractionEnabled)

        feedback.setPressed(false, animated: false)
        XCTAssertEqual(view.transform, original)
        XCTAssertEqual(view.alpha, 0.7, accuracy: 0.001)
        feedback.setPressed(true)
        feedback.reset()
        XCTAssertEqual(view.transform, original)
        XCTAssertEqual(view.alpha, 0.7, accuracy: 0.001)
    }

    func testInterruptedReleaseDoesNotAccumulateScaleOrRemoveOtherAnimations() {
        let root = UIViewController()
        let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 600))
        window.rootViewController = root
        window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        let view = UIView(frame: CGRect(x: 16, y: 100, width: 120, height: 160))
        root.view.addSubview(view)
        let animation = CABasicAnimation(keyPath: "backgroundColor")
        animation.fromValue = UIColor.clear.cgColor
        animation.toValue = UIColor.gray.cgColor
        animation.duration = 30
        view.layer.add(animation, forKey: "existing.background")
        let feedback = PressFeedback(view: view)

        for _ in 0..<3 {
            feedback.setPressed(true)
            assertPressed(view)
            feedback.setPressed(false)
        }
        feedback.reset()
        assertRestored(view)
        XCTAssertNotNil(view.layer.animation(forKey: "existing.background"))
    }

    func testBrowseCardsAndSongRowsRestoreOnCancelAndReuseAndKeepPlayActions() throws {
        let theme = PageTheme()
        for layout in [MusicPublicBrowseSection.Layout.playlistRail, .featureRail, .songColumns, .vipSongs, .list] {
            let cell = MusicBrowseCell(frame: CGRect(x: 0, y: 0, width: 320, height: 210))
            let track = makeTrack("song")
            var plays = 0
            cell.configure(item: .init(id: "song", title: track.title, content: .track(track)),
                           layout: layout, theme: theme, onPlay: { plays += 1 })
            cell.layoutIfNeeded()
            let play = try XCTUnwrap(descendants(cell).compactMap { $0 as? UIButton }.first {
                $0.accessibilityIdentifier == "music.browse.play.song"
            })
            let originalBounds = cell.bounds

            cell.isHighlighted = true
            assertPressed(cell.contentView)
            XCTAssertEqual(cell.bounds, originalBounds)
            XCTAssertEqual(play.transform, .identity, "A row scales its play button only through its content view")
            if layout == .songColumns || layout == .vipSongs || layout == .list {
                XCTAssertEqual(cell.contentView.backgroundColor, UIColor(theme.chip))
            }
            play.sendActions(for: .touchUpInside)
            XCTAssertEqual(plays, 1)
            cell.touchesCancelled([], with: nil)
            assertRestored(cell.contentView)
            XCTAssertFalse(cell.isHighlighted)
            if layout == .songColumns || layout == .vipSongs || layout == .list {
                XCTAssertEqual(cell.contentView.backgroundColor, .clear)
            }

            cell.isHighlighted = true
            cell.prepareForReuse()
            assertRestored(cell.contentView)
            XCTAssertFalse(cell.isHighlighted)
            XCTAssertEqual(plays, 1, "Cancelling or reusing a card cannot trigger playback")
        }
    }

    func testGroupedSongPreviewScalesOnlyItsOwnRowAndResetsWithParent() throws {
        let tracks = [makeTrack("first"), makeTrack("second"), makeTrack("third")]
        for layout in [MusicPublicBrowseSection.Layout.rankedSongs, .vipRankedSongs] {
            let cell = MusicBrowseCell(frame: CGRect(x: 0, y: 0, width: 320, height: 210))
            var played: [String] = []
            cell.configure(item: .init(id: "previews", title: "推荐", content: .unavailable, previewSongs: tracks),
                           layout: layout, theme: PageTheme(), onPreview: { played.append($0.songId) }, onPlay: {})
            cell.layoutIfNeeded()
            let button = try XCTUnwrap(descendants(cell).compactMap { $0 as? UIButton }
                .first { $0.accessibilityLabel == "播放second" })
            let row = try XCTUnwrap(button.subviews.compactMap { $0 as? UIStackView }.first)

            cell.isHighlighted = true
            assertPressed(cell.contentView)
            button.isHighlighted = true
            assertPressed(row)
            XCTAssertEqual(button.transform, .identity)
            assertRestored(cell.contentView)
            button.sendActions(for: .touchUpInside)
            XCTAssertEqual(played, ["second"])
            cell.resetPressFeedback()
            assertRestored(row)
            XCTAssertFalse(button.isHighlighted)
            XCTAssertEqual(button.backgroundColor, .clear)
            cell.isHighlighted = true
            button.isHighlighted = true
            cell.prepareForReuse()
            assertRestored(row)
            assertRestored(cell.contentView)
        }
    }

    func testEmbeddedSearchPlaylistTilesRemoveTouchDelaysAndResetOnReuse() throws {
        let root = UIViewController()
        let scroll = UIScrollView(frame: CGRect(x: 0, y: 0, width: 390, height: 600))
        let table = MusicSearchResultsTableView(frame: scroll.bounds, style: .plain)
        let cell = MusicSearchPlaylistCell(style: .default, reuseIdentifier: "playlist")
        let item = MusicPublicBrowseCollection(platform: .netease, nativeID: "playlist", title: "歌单")
        cell.configure([item], columns: 1, theme: PageTheme(), open: { _ in XCTFail("Reset cannot open a playlist") })
        let source = PressPlaylistDataSource(cell: cell)
        table.dataSource = source
        table.rowHeight = 64
        scroll.addSubview(table)
        root.view.addSubview(scroll)
        let window = UIWindow(frame: scroll.bounds)
        window.rootViewController = root
        window.makeKeyAndVisible()
        defer {
            window.isHidden = true; window.rootViewController = nil
            withExtendedLifetime(source) {}
        }
        table.layoutIfNeeded()
        let tile = try XCTUnwrap(cell.contentView.subviews.compactMap { $0 as? MusicBrowseCell }.first)

        XCTAssertFalse(table.delaysContentTouches)
        XCTAssertFalse(scroll.delaysContentTouches)
        XCTAssertTrue(table.canCancelContentTouches)
        XCTAssertTrue(scroll.canCancelContentTouches)
        XCTAssertTrue(table.touchesShouldCancel(in: UIButton(type: .custom)))
        tile.isHighlighted = true
        assertPressed(tile.contentView)
        tile.touchesCancelled([], with: nil)
        assertRestored(tile.contentView)
        tile.isHighlighted = true
        cell.prepareForReuse()
        assertRestored(tile.contentView)
        XCTAssertFalse(tile.isHighlighted)
    }

    func testBrowseCollectionAllowsDraggingToCancelChildButtonTouches() {
        let collection = MusicBrowseCollectionView(frame: .zero, collectionViewLayout: UICollectionViewFlowLayout())
        XCTAssertTrue(collection.touchesShouldCancel(in: UIButton(type: .custom)))
        XCTAssertTrue(collection.touchesShouldCancel(in: UIButton(type: .system)))
    }

    func testPressedCardsShrinkInPlaceWithoutResizingTheirContentInACollection() throws {
        let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 600))
        window.makeKeyAndVisible()
        defer { window.isHidden = true }
        let cases: [(MusicPublicBrowseSection.Layout, CGSize)] = [
            (.playlistRail, CGSize(width: 110, height: 160)),
            (.featureRail, CGSize(width: 150, height: 200)),
            (.songColumns, CGSize(width: 320, height: 64)),
        ]
        for (layout, size) in cases {
            let flow = UICollectionViewFlowLayout()
            flow.itemSize = size
            let collection = UICollectionView(frame: window.bounds, collectionViewLayout: flow)
            collection.register(MusicBrowseCell.self, forCellWithReuseIdentifier: "card")
            let track = MusicRemoteTrack(ref: .init(kind: .handle, id: "wrap"), songId: "wrap",
                                         title: "一首名字很长需要折成两行的歌曲")
            let source = PressCardDataSource { cell in
                cell.configure(item: .init(id: "wrap", title: track.title, content: .track(track)),
                               layout: layout, theme: PageTheme(), onPlay: {})
            }
            collection.dataSource = source
            window.addSubview(collection)
            defer {
                collection.removeFromSuperview()
                withExtendedLifetime(source) {}
            }
            collection.layoutIfNeeded()
            let cell = try XCTUnwrap(collection.cellForItem(at: IndexPath(item: 0, section: 0)) as? MusicBrowseCell)
            let title = try XCTUnwrap(descendants(cell).compactMap { $0 as? UILabel }.first { $0.text == track.title })
            let restingContent = cell.contentView.bounds
            let restingTitle = title.frame

            cell.isHighlighted = true
            cell.setNeedsLayout()
            cell.layoutIfNeeded()
            assertPressed(cell.contentView)
            XCTAssertEqual(cell.contentView.bounds, restingContent, "\(layout) must not resize its content")
            XCTAssertEqual(title.frame, restingTitle, "\(layout) title must not re-wrap while pressed")
            let visible = cell.contentView.convert(cell.contentView.bounds, to: cell)
            let scale: CGFloat = UIAccessibility.isReduceMotionEnabled ? 1 : 0.97
            XCTAssertEqual(visible.width, cell.bounds.width * scale, accuracy: 0.01, "\(layout) must visibly shrink")
            XCTAssertEqual(visible.midX, cell.bounds.midX, accuracy: 0.01)
            XCTAssertEqual(visible.midY, cell.bounds.midY, accuracy: 0.01)

            cell.resetPressFeedback()
            cell.layoutIfNeeded()
            assertRestored(cell.contentView)
            XCTAssertEqual(cell.contentView.bounds, restingContent)
        }
    }

    private func makeTrack(_ id: String) -> MusicRemoteTrack {
        .init(ref: .init(kind: .handle, id: id), songId: id, title: id)
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }

    private func assertPressed(_ view: UIView, file: StaticString = #filePath, line: UInt = #line) {
        if UIAccessibility.isReduceMotionEnabled {
            XCTAssertEqual(view.transform, .identity, file: file, line: line)
        } else {
            XCTAssertGreaterThanOrEqual(view.transform.a, 0.96, file: file, line: line)
            XCTAssertLessThanOrEqual(view.transform.a, 0.98, file: file, line: line)
            XCTAssertEqual(view.transform.a, view.transform.d, file: file, line: line)
        }
        XCTAssertLessThan(view.alpha, 1, file: file, line: line)
    }

    private func assertRestored(_ view: UIView, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(view.transform, .identity, file: file, line: line)
        XCTAssertEqual(view.alpha, 1, accuracy: 0.001, file: file, line: line)
    }
}

@MainActor
private final class PressPlaylistDataSource: NSObject, UITableViewDataSource {
    private let cell: UITableViewCell
    init(cell: UITableViewCell) { self.cell = cell; super.init() }
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { 1 }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell { cell }
}

@MainActor
private final class PressCardDataSource: NSObject, UICollectionViewDataSource {
    private let configure: (MusicBrowseCell) -> Void
    init(configure: @escaping (MusicBrowseCell) -> Void) { self.configure = configure; super.init() }
    func collectionView(_ collectionView: UICollectionView, numberOfItemsInSection section: Int) -> Int { 1 }
    func collectionView(_ collectionView: UICollectionView,
                        cellForItemAt indexPath: IndexPath) -> UICollectionViewCell {
        let cell = collectionView.dequeueReusableCell(withReuseIdentifier: "card", for: indexPath)
        if let cell = cell as? MusicBrowseCell { configure(cell) }
        return cell
    }
}
#endif
