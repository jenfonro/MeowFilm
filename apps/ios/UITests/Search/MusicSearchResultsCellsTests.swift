#if os(iOS)
import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature

@MainActor
final class MusicSearchResultsCellsTests: XCTestCase {
    func testTabsKeepStableFramesWhileIndicatorAndColorsFollowPaging() throws {
        let theme = PageTheme(palette: .mint)
        let tabs = MusicSearchResultTabsView(frame: CGRect(x: 0, y: 0, width: 358, height: 48))
        tabs.setTitles(["综合", "单曲", "歌单"], theme: theme)
        tabs.setSelectedIndex(0)
        tabs.setPosition(0)
        tabs.layoutIfNeeded()
        let buttons = try (0..<3).map {
            try XCTUnwrap(identified("music.search.results.tab.\($0)", in: tabs) as? UIButton)
        }
        let indicator = try XCTUnwrap(identified("music.search.results.tab.indicator", in: tabs))
        let buttonFrames = buttons.map(\.frame)
        let start = indicator.frame
        XCTAssertEqual(buttons.compactMap { $0.titleLabel?.font.pointSize },
                       [CGFloat(17), CGFloat(17), CGFloat(17)])
        XCTAssertTrue(buttons.allSatisfy { $0.titleLabel?.numberOfLines == 1 })
        XCTAssertEqual(buttons[1].frame.minX - buttons[0].frame.maxX, 32, accuracy: 0.5)
        XCTAssertEqual(indicator.bounds.height, 2.5, accuracy: 0.01)
        XCTAssertEqual(indicator.backgroundColor, UIColor(theme.accent))
        XCTAssertEqual(buttons[0].titleColor(for: .normal), UIColor(theme.ink))
        XCTAssertEqual(buttons[1].titleColor(for: .normal), UIColor(theme.muted))

        tabs.setPosition(1)
        tabs.layoutIfNeeded()
        let end = indicator.frame
        XCTAssertEqual(buttons.map(\.frame), buttonFrames)
        XCTAssertEqual(buttons[0].titleColor(for: .normal), UIColor(theme.muted))
        XCTAssertEqual(buttons[1].titleColor(for: .normal), UIColor(theme.ink))

        tabs.setPosition(0.5)
        tabs.layoutIfNeeded()
        XCTAssertEqual(buttons.map(\.frame), buttonFrames)
        XCTAssertEqual(indicator.frame.minX, (start.minX + end.minX) / 2, accuracy: 0.5)
        XCTAssertEqual(indicator.frame.width, (start.width + end.width) / 2, accuracy: 0.5)
        let mixed = UIColor(theme.muted.mixed(with: theme.ink, ratio: 0.5))
        XCTAssertEqual(buttons[0].titleColor(for: .normal), mixed)
        XCTAssertEqual(buttons[1].titleColor(for: .normal), mixed)

        var selected: Int?
        tabs.onSelect = { selected = $0 }
        buttons[2].sendActions(for: .touchUpInside)
        XCTAssertEqual(selected, 2)
        tabs.setSelectedIndex(2)
        XCTAssertTrue(buttons[2].accessibilityTraits.contains(.selected))
        XCTAssertFalse(buttons[0].accessibilityTraits.contains(.selected))
    }

    func testSongCellUsesTextAndThreeActionsWithoutArtworkOrSequence() throws {
        let track = MusicRemoteTrack(
            ref: .init(kind: .handle, id: "handle"),
            songId: "native-song",
            title: "搜索歌曲",
            artist: "歌手",
            album: "专辑"
        )
        let song = MusicSearchSong(
            id: "search-song",
            track: track,
            variants: [track],
            matchedPlatforms: ["wy"],
            score: 1
        )
        var played = 0, favorited = 0, appended = 0
        let menu = UIMenu(children: [UIAction(title: "音源") { _ in }])
        let cell = MusicSearchSongCell(style: .default, reuseIdentifier: nil)
        cell.frame = CGRect(x: 0, y: 0, width: 390, height: 72)
        cell.configure(
            song: song,
            active: true,
            favorite: true,
            enabled: true,
            theme: PageTheme(),
            onPlay: { played += 1 },
            onFavorite: { favorited += 1 },
            onAppend: { appended += 1 },
            menu: menu
        )
        cell.layoutIfNeeded(); cell.contentView.layoutIfNeeded()
        let suffix = Data(song.id.utf8).base64EncodedString()
        let info = try button("music.search.song.info.\(suffix)", in: cell)
        let favorite = try button("music.search.song.favorite.\(suffix)", in: cell)
        let append = try button("music.search.song.append.\(suffix)", in: cell)
        let more = try button("music.search.song.more.\(suffix)", in: cell)
        let title = try label("music.search.song.title.\(suffix)", in: cell)
        let subtitle = try label("music.search.song.subtitle.\(suffix)", in: cell)
        XCTAssertEqual(title.text, "搜索歌曲")
        XCTAssertEqual(subtitle.text, "歌手 · 专辑")
        XCTAssertEqual(title.font.pointSize, 14)
        XCTAssertEqual(subtitle.font.pointSize, 12)
        XCTAssertFalse(title.text?.hasPrefix("1.") == true)
        XCTAssertTrue(descendants(cell).compactMap { $0 as? MusicArtworkView }.isEmpty)
        XCTAssertEqual([favorite, append, more].map(\.bounds.height), [44, 44, 44])
        XCTAssertEqual(favorite.frame.maxX + 3, append.frame.minX, accuracy: 0.5)
        XCTAssertEqual(append.frame.maxX + 3, more.frame.minX, accuracy: 0.5)
        XCTAssertTrue(favorite.accessibilityTraits.contains(.selected))
        XCTAssertEqual(favorite.accessibilityLabel, "取消收藏")
        XCTAssertEqual(append.accessibilityLabel, "添加到播放队列")
        XCTAssertEqual(more.accessibilityLabel, "更多")
        XCTAssertTrue(more.showsMenuAsPrimaryAction)
        XCTAssertEqual(more.menu?.children.count, 1)
        info.sendActions(for: .touchUpInside)
        favorite.sendActions(for: .touchUpInside)
        append.sendActions(for: .touchUpInside)
        XCTAssertEqual(played, 1)
        XCTAssertEqual(favorited, 1)
        XCTAssertEqual(appended, 1)
    }

    func testSectionHeadingKeepsPlayBesideTitleAndCanRemoveIt() throws {
        let theme = PageTheme()
        let cell = MusicSearchSectionHeaderCell(style: .default, reuseIdentifier: nil)
        cell.frame = CGRect(x: 0, y: 0, width: 390, height: 44)
        var plays = 0
        cell.configure(title: "单曲", showsPlay: true, theme: theme) { plays += 1 }
        cell.layoutIfNeeded(); cell.contentView.layoutIfNeeded()
        let title = try label("music.search.results.section.title", in: cell)
        let play = try button("music.search.results.section.play", in: cell)
        XCTAssertEqual(title.numberOfLines, 1)
        XCTAssertEqual(play.frame.minX, title.frame.maxX + 3, accuracy: 0.5)
        XCTAssertEqual(play.bounds.size, CGSize(width: 32, height: 32))
        XCTAssertEqual(play.center.y, cell.bounds.midY, accuracy: 0.5)
        XCTAssertEqual(play.layer.cornerRadius, 16)
        XCTAssertEqual(play.backgroundColor, UIColor(theme.chip))
        XCTAssertEqual(play.tintColor, UIColor(theme.ink))
        XCTAssertFalse(play.isHidden)
        play.sendActions(for: .touchUpInside)
        XCTAssertEqual(plays, 1)

        cell.configure(title: "歌单", showsPlay: false, theme: theme) { plays += 1 }
        cell.layoutIfNeeded()
        XCTAssertTrue(play.isHidden)
        XCTAssertEqual(play.frame, .zero)
        XCTAssertEqual(title.text, "歌单")
    }

    func testSearchPlaylistCellHidesBrowsePlayControl() throws {
        let item = MusicPublicBrowseCollection(
            platform: .netease,
            nativeID: "playlist",
            title: "搜索歌单",
            creator: "创建者",
            count: 20
        )
        let cell = MusicSearchPlaylistCell(style: .default, reuseIdentifier: nil)
        cell.frame = CGRect(x: 0, y: 0, width: 390, height: 80)
        cell.configure([item], columns: 1, theme: PageTheme(), open: { _ in })
        cell.layoutIfNeeded(); cell.contentView.layoutIfNeeded()
        let play = try button("music.browse.play.\(item.id)", in: cell)
        XCTAssertTrue(play.isHidden)
        XCTAssertEqual(play.frame, .zero)
    }

    private func button(_ id: String, in root: UIView) throws -> UIButton {
        try XCTUnwrap(identified(id, in: root) as? UIButton, id)
    }

    private func label(_ id: String, in root: UIView) throws -> UILabel {
        try XCTUnwrap(identified(id, in: root) as? UILabel, id)
    }

    private func identified(_ id: String, in root: UIView) -> UIView? {
        ([root] + descendants(root)).first { $0.accessibilityIdentifier == id }
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }
}
#endif
