#if os(iOS)
import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature

@MainActor
final class MusicSearchDiscoveryCellsTests: XCTestCase {
    func testCollapsedHistoryUsesActualOverflowAndNonoverlappingTouchTargets() throws {
        let cell = historyCell()
        cell.configure(entries: ["一", "二"].map { .init(text: $0) }, expanded: false,
                       theme: PageTheme(), onKeyword: { _ in }, onToggle: {}, onClear: {})
        layout(cell, width: 320, height: cell.height(for: 320))
        let toggle = try button("music.search.history.toggle", in: cell)
        XCTAssertTrue(toggle.isHidden)
        XCTAssertEqual(keywordButtons("music.search.history", in: cell).filter { !$0.isHidden }.count, 2)

        let words = ["一", "二", "三", "四", "五", "六"]
        cell.configure(entries: words.map { .init(text: $0) }, expanded: false,
                       theme: PageTheme(), onKeyword: { _ in }, onToggle: {}, onClear: {})
        let collapsedHeight = cell.height(for: 320)
        layout(cell, width: 320, height: collapsedHeight)
        let visible = keywordButtons("music.search.history", in: cell).filter { !$0.isHidden }
        XCTAssertFalse(toggle.isHidden)
        XCTAssertGreaterThan(visible.count, 2)
        XCTAssertLessThan(visible.count, words.count)
        XCTAssertEqual(Set(visible.map { frame($0, in: cell).minY }).count, 1)
        let clear = try button("music.search.history.clear", in: cell)
        for control in visible + [clear, toggle] {
            XCTAssertGreaterThanOrEqual(control.bounds.width, 44)
            XCTAssertGreaterThanOrEqual(control.bounds.height, 44)
            XCTAssertTrue(cell.bounds.contains(frame(control, in: cell)))
            let point = control.convert(CGPoint(x: control.bounds.midX, y: control.bounds.midY), to: cell)
            XCTAssertTrue(cell.hitTest(point, with: nil) === control)
        }
        for (index, control) in visible.enumerated() {
            XCTAssertFalse(frame(control, in: cell).intersects(frame(toggle, in: cell)))
            XCTAssertFalse(frame(control, in: cell).intersects(frame(clear, in: cell)))
            for other in visible.dropFirst(index + 1) {
                XCTAssertFalse(frame(control, in: cell).intersects(frame(other, in: cell)))
            }
        }
        XCTAssertFalse(frame(toggle, in: cell).intersects(frame(clear, in: cell)))
        layout(cell, width: 390, height: cell.height(for: 390))
        XCTAssertTrue(toggle.isHidden)
        XCTAssertEqual(keywordButtons("music.search.history", in: cell).filter { !$0.isHidden }.count, 6)
        XCTAssertEqual(cell.height(for: 390), collapsedHeight, accuracy: 0.5)
        XCTAssertEqual(try label("music.search.history.title", in: cell).font.pointSize, 16, accuracy: 0.1)
    }

    func testExpandedHistoryMeasuresSixtyEntriesAndSubmitsExactUntruncatedWords() throws {
        let cell = historyCell()
        let words = ["é", "e\u{301}", "TRACK", "track"] + (0..<56).map {
            "完整关键词\($0) " + String(repeating: "Long title 中文 ", count: 12)
        }
        let entries = words.map { SearchHistoryEntry(text: $0) }
        var submitted: [Data] = []
        cell.configure(entries: entries, expanded: false, theme: PageTheme(),
                       onKeyword: { submitted.append(Data($0.utf8)) }, onToggle: {}, onClear: {})
        let collapsedHeight = cell.height(for: 320)
        layout(cell, width: 320, height: collapsedHeight)
        XCTAssertTrue(keywordButtons("music.search.history", in: cell).contains { $0.isHidden })
        cell.configure(entries: entries, expanded: true, theme: PageTheme(),
                       onKeyword: { submitted.append(Data($0.utf8)) }, onToggle: {}, onClear: {})
        let expandedHeight = cell.height(for: 320)
        layout(cell, width: 320, height: expandedHeight)
        let controls = keywordButtons("music.search.history", in: cell)
        XCTAssertEqual(controls.count, 60)
        XCTAssertTrue(controls.allSatisfy { !$0.isHidden && cell.bounds.contains(frame($0, in: cell)) })
        XCTAssertGreaterThan(expandedHeight, collapsedHeight)
        XCTAssertGreaterThan(Set(controls.map { frame($0, in: cell).minY }).count, 1)
        let composed = try keywordButton("é", prefix: "music.search.history", in: cell)
        let decomposed = try keywordButton("e\u{301}", prefix: "music.search.history", in: cell)
        XCTAssertFalse(composed === decomposed)
        let longID = keywordID(words[4], prefix: "music.search.history")
        let longLabel = try label(longID + ".title", in: cell)
        XCTAssertEqual(longLabel.numberOfLines, 1)
        XCTAssertEqual(longLabel.lineBreakMode, .byTruncatingTail)
        XCTAssertEqual(longLabel.font.pointSize, 14, accuracy: 0.1)
        XCTAssertLessThan(longLabel.bounds.width, (words[4] as NSString).size(
            withAttributes: [.font: longLabel.font!]).width)
        for word in words {
            let control = try keywordButton(word, prefix: "music.search.history", in: cell)
            XCTAssertEqual(Data((control.accessibilityLabel ?? "").utf8), Data(word.utf8))
            control.sendActions(for: .touchUpInside)
        }
        XCTAssertEqual(submitted, words.map { Data($0.utf8) })
    }

    func testHistoryRemeasuresOverflowAndExpandedHeightForDynamicType() throws {
        let cell = historyCell()
        cell.configure(entries: ["搜索关键词A", "搜索关键词B"].map { .init(text: $0) }, expanded: false,
                       theme: PageTheme(), onKeyword: { _ in }, onToggle: {}, onClear: {})
        let normalHeight = cell.height(for: 292)
        layout(cell, width: 292, height: normalHeight)
        let toggle = try button("music.search.history.toggle", in: cell)
        XCTAssertTrue(toggle.isHidden)
        setContentSizeCategory(.accessibilityExtraExtraExtraLarge, on: cell)
        let enlargedHeight = cell.height(for: 292)
        layout(cell, width: 292, height: enlargedHeight)
        XCTAssertGreaterThan(enlargedHeight, normalHeight)
        XCTAssertFalse(toggle.isHidden)
        cell.configure(entries: ["搜索关键词A", "搜索关键词B"].map { .init(text: $0) }, expanded: true,
                       theme: PageTheme(), onKeyword: { _ in }, onToggle: {}, onClear: {})
        let expandedHeight = cell.height(for: 292)
        layout(cell, width: 292, height: expandedHeight)
        XCTAssertGreaterThan(expandedHeight, enlargedHeight)
        XCTAssertTrue(keywordButtons("music.search.history", in: cell).allSatisfy {
            !$0.isHidden && cell.bounds.contains(frame($0, in: cell))
        })
        setContentSizeCategory(.large, on: cell)
        cell.configure(entries: ["搜索关键词A", "搜索关键词B"].map { .init(text: $0) }, expanded: false,
                       theme: PageTheme(), onKeyword: { _ in }, onToggle: {}, onClear: {})
        XCTAssertEqual(cell.height(for: 292), normalHeight, accuracy: 0.5)
    }

    func testHistoryReusesUnchangedButtonsAndConsumesUpdatedCallbacksAndTheme() throws {
        let cell = historyCell()
        let a = "保留关键词A", b = "替换关键词B", c = "保留关键词C", d = "当前关键词D"
        var oldCalls = 0, submitted: [String] = [], toggles = 0, clears = 0
        cell.configure(entries: [a, b, c].map { .init(text: $0) }, expanded: false, theme: PageTheme(),
                       onKeyword: { _ in oldCalls += 1 }, onToggle: { oldCalls += 1 },
                       onClear: { oldCalls += 1 })
        layout(cell, width: 320, height: cell.height(for: 320))
        let first = try keywordButton(a, prefix: "music.search.history", in: cell)
        let third = try keywordButton(c, prefix: "music.search.history", in: cell)
        let theme = PageTheme(palette: .mint)
        cell.configure(entries: [a, d, c].map { .init(text: $0) }, expanded: false, theme: theme,
                       onKeyword: { submitted.append($0) }, onToggle: { toggles += 1 },
                       onClear: { clears += 1 })
        layout(cell, width: 320, height: cell.height(for: 320))
        XCTAssertTrue(try keywordButton(a, prefix: "music.search.history", in: cell) === first)
        XCTAssertTrue(try keywordButton(c, prefix: "music.search.history", in: cell) === third)
        XCTAssertNil(identified(keywordID(b, prefix: "music.search.history"), in: cell))
        XCTAssertEqual(try label(keywordID(a, prefix: "music.search.history") + ".title", in: cell).textColor,
                       UIColor(theme.ink))
        first.sendActions(for: .touchUpInside)
        try button("music.search.history.toggle", in: cell).sendActions(for: .touchUpInside)
        try button("music.search.history.clear", in: cell).sendActions(for: .touchUpInside)
        XCTAssertEqual(submitted, [a]); XCTAssertEqual(toggles, 1); XCTAssertEqual(clears, 1)
        XCTAssertEqual(oldCalls, 0)
    }

    func testSuggestionsUseSixWordsInTwoColumnsAndDoNotPadShortOrEmptyInputs() throws {
        let cell = suggestionsCell()
        let words = ["被伪造的偶像", "Chains", "Collide (Astronaut & Barely Alive Remix)",
                     "HOYO-MiX", "Fall Into Me", "黄霄雲"]
        var submitted: [String] = []
        cell.configure(keywords: words, theme: PageTheme(), onKeyword: { submitted.append($0) }, onRotate: {})
        layout(cell, width: 390, height: cell.height(for: 390))
        XCTAssertEqual(keywordButtons("music.search.suggestions", in: cell).count, 6)
        let controls = try words.map { try keywordButton($0, prefix: "music.search.suggestions", in: cell) }
        for row in 0..<3 {
            let left = frame(controls[row * 2], in: cell)
            let right = frame(controls[row * 2 + 1], in: cell)
            XCTAssertEqual(left.minY, right.minY, accuracy: 0.5)
            XCTAssertEqual(left.width, right.width, accuracy: 0.5)
            XCTAssertEqual(right.minX - left.maxX, 16, accuracy: 0.5)
            XCTAssertGreaterThanOrEqual(left.height, 44)
            if row > 0 {
                XCTAssertGreaterThanOrEqual(left.minY, frame(controls[(row - 1) * 2], in: cell).maxY)
            }
        }
        let long = try label(keywordID(words[2], prefix: "music.search.suggestions") + ".title", in: cell)
        XCTAssertEqual(long.font.pointSize, 14, accuracy: 0.1)
        XCTAssertEqual(long.lineBreakMode, .byTruncatingTail)
        controls[2].sendActions(for: .touchUpInside)
        XCTAssertEqual(submitted, [words[2]])
        XCTAssertEqual(try label("music.search.suggestions.title", in: cell).font.pointSize, 16, accuracy: 0.1)
        let normalHeight = cell.height(for: 390)
        setContentSizeCategory(.accessibilityExtraExtraExtraLarge, on: cell)
        let enlargedHeight = cell.height(for: 390)
        layout(cell, width: 390, height: enlargedHeight)
        XCTAssertGreaterThan(enlargedHeight, normalHeight)
        XCTAssertTrue(controls.allSatisfy { cell.bounds.contains(frame($0, in: cell)) })
        setContentSizeCategory(.large, on: cell)
        cell.configure(keywords: Array(words.prefix(3)), theme: PageTheme(), onKeyword: { _ in }, onRotate: {})
        layout(cell, width: 390, height: cell.height(for: 390))
        XCTAssertEqual(keywordButtons("music.search.suggestions", in: cell).count, 3)
        cell.configure(keywords: [], theme: PageTheme(), onKeyword: { _ in }, onRotate: {})
        layout(cell, width: 390, height: cell.height(for: 390))
        XCTAssertTrue(keywordButtons("music.search.suggestions", in: cell).isEmpty)
        let empty = try label("music.search.suggestions.empty", in: cell)
        XCTAssertFalse(empty.isHidden); XCTAssertEqual(empty.text, "暂无推荐关键词")
        XCTAssertTrue(cell.bounds.contains(frame(empty, in: cell)))
        XCTAssertGreaterThanOrEqual(try button("music.search.suggestions.rotate", in: cell).bounds.height, 44)
        cell.configure(keywords: words + ["第七词"], theme: PageTheme(), onKeyword: { _ in }, onRotate: {})
        layout(cell, width: 390, height: cell.height(for: 390))
        XCTAssertEqual(keywordButtons("music.search.suggestions", in: cell).count, 6)
        XCTAssertNil(identified(keywordID("第七词", prefix: "music.search.suggestions"), in: cell))
    }

    func testSuggestionsReuseUnchangedViewsAndUseCurrentKeywordAndRotateCallbacks() throws {
        let cell = suggestionsCell()
        var oldCalls = 0, submitted: [String] = [], rotations = 0
        cell.configure(keywords: ["A", "B", "C"], theme: PageTheme(),
                       onKeyword: { _ in oldCalls += 1 }, onRotate: { oldCalls += 1 })
        layout(cell, width: 320, height: cell.height(for: 320))
        let first = try keywordButton("A", prefix: "music.search.suggestions", in: cell)
        let third = try keywordButton("C", prefix: "music.search.suggestions", in: cell)
        let theme = PageTheme(palette: .mint)
        cell.configure(keywords: ["A", "D", "C"], theme: theme,
                       onKeyword: { submitted.append($0) }, onRotate: { rotations += 1 })
        layout(cell, width: 320, height: cell.height(for: 320))
        XCTAssertTrue(try keywordButton("A", prefix: "music.search.suggestions", in: cell) === first)
        XCTAssertTrue(try keywordButton("C", prefix: "music.search.suggestions", in: cell) === third)
        XCTAssertNil(identified(keywordID("B", prefix: "music.search.suggestions"), in: cell))
        XCTAssertEqual(try label(keywordID("A", prefix: "music.search.suggestions") + ".title", in: cell).textColor,
                       UIColor(theme.ink))
        first.sendActions(for: .touchUpInside)
        try button("music.search.suggestions.rotate", in: cell).sendActions(for: .touchUpInside)
        XCTAssertEqual(submitted, ["A"]); XCTAssertEqual(rotations, 1); XCTAssertEqual(oldCalls, 0)
    }

    func testHotCardPlayStaysBesideTitleWhileRowsRemainSearchActions() throws {
        let cell = railCell()
        let rows: [MusicSearchChartRowViewData] = [
            .init(id: "one", rank: 1, title: "第一词"),
            .init(id: "three", rank: 3, title: "第三词"),
            .init(id: "four", rank: 4, title: "第四词"),
        ]
        var selected: [String] = [], played: [String] = []
        let theme = PageTheme()
        cell.configure(cards: [card("hot", rows: rows, isMusic: false, showsPlayButton: true),
                               card("music", title: "ACG榜", rows: rows)],
                       theme: theme, onRow: { selected.append($0 + ":" + $1) }, onPlay: { played.append($0) })
        layout(cell, width: 390, height: cell.height(for: 390))
        let hotPlay = try button("music.search.chart.play.hot", in: cell)
        let hotTitle = try label("music.search.chart.title.hot", in: cell)
        let hotCard = try view("music.search.chart.card.hot", in: cell)
        XCTAssertFalse(hotPlay.isHidden); XCTAssertTrue(hotPlay.isEnabled)
        XCTAssertEqual(frame(hotPlay, in: hotCard).minX,
                       frame(hotTitle, in: hotCard).maxX + 6, accuracy: 0.5)
        hotPlay.sendActions(for: .touchUpInside)
        XCTAssertEqual(played, ["hot"])
        for (row, text) in zip(rows, ["1", "3", "4"]) {
            let rank = try label("music.search.chart.rank.hot.\(row.id)", in: cell)
            XCTAssertEqual(rank.text, text)
            XCTAssertEqual(rank.textColor, UIColor(row.rank <= 3 ? theme.accent : theme.muted))
            XCTAssertEqual(rank.textAlignment, .right)
            XCTAssertEqual(("1" as NSString).size(withAttributes: [.font: rank.font!]).width,
                           ("8" as NSString).size(withAttributes: [.font: rank.font!]).width, accuracy: 0.1)
        }
        XCTAssertTrue(try XCTUnwrap(button("music.search.chart.row.hot.one", in: cell)
            .accessibilityLabel).hasPrefix("搜索"))
        XCTAssertEqual(try label("music.search.chart.song.hot.one", in: cell).font.pointSize, 14, accuracy: 0.1)
        XCTAssertEqual(try label("music.search.chart.title.hot", in: cell).font.pointSize, 16, accuracy: 0.1)
        let scroll = try scrollView(in: cell)
        let music = try view("music.search.chart.card.music", in: cell)
        scroll.setContentOffset(CGPoint(x: music.frame.minX, y: 0), animated: false)
        let musicPlay = try button("music.search.chart.play.music", in: cell)
        let musicTitle = try label("music.search.chart.title.music", in: cell)
        XCTAssertFalse(musicPlay.isHidden); XCTAssertTrue(musicPlay.isEnabled)
        XCTAssertEqual(musicPlay.configuration?.title, "播放")
        XCTAssertGreaterThanOrEqual(musicPlay.bounds.width, 44)
        XCTAssertGreaterThanOrEqual(musicPlay.bounds.height, 44)
        XCTAssertEqual(musicPlay.configuration?.titleLineBreakMode, .byClipping)
        XCTAssertFalse(frame(musicPlay, in: cell).intersects(frame(musicTitle, in: cell)))
        let musicCard = try view("music.search.chart.card.music", in: cell)
        XCTAssertEqual(frame(musicTitle, in: musicCard).minY, 8, accuracy: 0.5)
        XCTAssertEqual(frame(musicPlay, in: musicCard).minX,
                       frame(musicTitle, in: musicCard).maxX + 6, accuracy: 0.5)
        XCTAssertLessThanOrEqual(frame(musicPlay, in: musicCard).maxX,
                                 musicCard.bounds.width - 12)
        let separator = try view("music.search.chart.separator.music", in: musicCard)
        let members = try view("music.search.chart.members.music", in: musicCard)
        XCTAssertEqual(frame(separator, in: musicCard).minY,
                       frame(musicTitle, in: musicCard).maxY + 2, accuracy: 0.5)
        XCTAssertEqual(frame(members, in: musicCard).minY,
                       frame(musicTitle, in: musicCard).maxY + 8, accuracy: 0.5)
        let point = musicPlay.convert(CGPoint(x: musicPlay.bounds.midX, y: musicPlay.bounds.midY), to: cell)
        XCTAssertTrue(cell.hitTest(point, with: nil) === musicPlay)
        try button("music.search.chart.row.hot.four", in: cell).sendActions(for: .touchUpInside)
        musicPlay.sendActions(for: .touchUpInside)
        XCTAssertEqual(selected, ["hot:four"]); XCTAssertEqual(played, ["hot", "music"])

        let longTitle = String(repeating: "超长榜单标题", count: 8)
        setContentSizeCategory(.accessibilityExtraExtraExtraLarge, on: cell)
        cell.configure(cards: [card("hot", rows: rows, isMusic: false),
                               card("music", title: longTitle, rows: rows)],
                       theme: theme, onRow: { _, _ in }, onPlay: { _ in })
        layout(cell, width: 292, height: cell.height(for: 292))
        let longMusicCard = try view("music.search.chart.card.music", in: cell)
        let longMusicTitle = try label("music.search.chart.title.music", in: cell)
        let longMusicPlay = try button("music.search.chart.play.music", in: cell)
        XCTAssertEqual(longMusicTitle.numberOfLines, 1)
        XCTAssertEqual(longMusicTitle.lineBreakMode, .byTruncatingTail)
        XCTAssertLessThan(longMusicTitle.bounds.width, (longTitle as NSString).size(
            withAttributes: [.font: longMusicTitle.font!]).width)
        XCTAssertEqual(frame(longMusicPlay, in: longMusicCard).minX,
                       frame(longMusicTitle, in: longMusicCard).maxX + 6, accuracy: 0.5)
        XCTAssertEqual(frame(longMusicPlay, in: longMusicCard).minY,
                       frame(longMusicTitle, in: longMusicCard).minY, accuracy: 0.5)
        XCTAssertLessThanOrEqual(frame(longMusicPlay, in: longMusicCard).maxX,
                                 longMusicCard.bounds.width - 12)
    }

    func testChartDataAndThemeUpdatesPreserveCardsRowsAndOffsetAndUseNewestCallbacks() throws {
        let cell = railCell()
        let a = MusicSearchChartRowViewData(id: "a", rank: 1, title: "旧标题")
        let b = MusicSearchChartRowViewData(id: "b", rank: 2, title: "替换")
        let c = MusicSearchChartRowViewData(id: "c", rank: 3, title: "保留")
        var oldCalls = 0, selected: [String] = [], played: [String] = []
        cell.configure(cards: [card("hot", rows: [a, b, c], isMusic: false), card("music", rows: [a])],
                       theme: PageTheme(), onRow: { _, _ in oldCalls += 1 }, onPlay: { _ in oldCalls += 1 })
        layout(cell, width: 390, height: cell.height(for: 390))
        let hot = try view("music.search.chart.card.hot", in: cell)
        let music = try view("music.search.chart.card.music", in: cell)
        let first = try button("music.search.chart.row.hot.a", in: cell)
        let third = try button("music.search.chart.row.hot.c", in: cell)
        let scroll = try scrollView(in: cell)
        scroll.setContentOffset(CGPoint(x: 40, y: 0), animated: false)
        let updated = MusicSearchChartRowViewData(id: "a", rank: 4, title: "当前完整标题")
        let replacement = MusicSearchChartRowViewData(id: "d", rank: 2, title: "当前词")
        let theme = PageTheme(palette: .mint)
        cell.configure(cards: [card("hot", rows: [updated, replacement, c], isMusic: false),
                               card("music", title: "当前榜单", rows: [updated])],
                       theme: theme, onRow: { selected.append($0 + ":" + $1) }, onPlay: { played.append($0) })
        layout(cell, width: 390, height: cell.height(for: 390))
        XCTAssertTrue(try view("music.search.chart.card.hot", in: cell) === hot)
        XCTAssertTrue(try view("music.search.chart.card.music", in: cell) === music)
        XCTAssertTrue(try button("music.search.chart.row.hot.a", in: cell) === first)
        XCTAssertTrue(try button("music.search.chart.row.hot.c", in: cell) === third)
        XCTAssertTrue(try scrollView(in: cell) === scroll)
        XCTAssertNil(identified("music.search.chart.row.hot.b", in: cell))
        XCTAssertEqual(scroll.contentOffset.x, 40, accuracy: 0.5)
        XCTAssertEqual(try label("music.search.chart.song.hot.a", in: cell).text, "当前完整标题")
        XCTAssertEqual(try label("music.search.chart.rank.hot.a", in: cell).text, "4")
        XCTAssertEqual(try label("music.search.chart.rank.hot.a", in: cell).textColor, UIColor(theme.muted))
        XCTAssertEqual(hot.backgroundColor, UIColor(theme.surface))
        XCTAssertEqual(UIColor(cgColor: try XCTUnwrap(hot.layer.borderColor)), UIColor(theme.border))
        XCTAssertEqual(try button("music.search.chart.play.music", in: cell).accessibilityLabel, "播放当前榜单")
        first.sendActions(for: .touchUpInside)
        try button("music.search.chart.play.music", in: cell).sendActions(for: .touchUpInside)
        XCTAssertEqual(selected, ["hot:a"]); XCTAssertEqual(played, ["music"]); XCTAssertEqual(oldCalls, 0)
    }

    func testVisibleMembersRequireOuterViewportAndHorizontalClipAndExcludeHiddenCards() throws {
        let cell = railCell()
        let cards = [card("hot", rows: [.init(id: "hot-row", rank: 1, title: "热词")], isMusic: false),
                     card("first", state: .idle), card("second", state: .loading)]
        cell.configure(cards: cards, theme: PageTheme(), onRow: { _, _ in }, onPlay: { _ in })
        layout(cell, width: 390, height: cell.height(for: 390))
        let first = try view("music.search.chart.card.first", in: cell)
        let members = try view("music.search.chart.members.first", in: cell)
        let memberFrame = frame(members, in: cell)
        XCTAssertGreaterThan(memberFrame.height, 0)
        XCTAssertEqual(cell.visibleMusicChartIDs(in: cell.bounds), Set(["first"]))
        let titleOnly = CGRect(x: 0, y: 0, width: cell.bounds.width, height: memberFrame.minY - 0.5)
        XCTAssertTrue(cell.visibleMusicChartIDs(in: titleOnly).isEmpty)
        XCTAssertEqual(cell.visibleMusicChartIDs(in: CGRect(
            x: 0, y: memberFrame.minY, width: cell.bounds.width, height: 44)), Set(["first"]))
        XCTAssertTrue(cell.visibleMusicChartIDs(in: .zero).isEmpty)
        let scroll = try scrollView(in: cell)
        let stride = first.frame.minX
        scroll.setContentOffset(CGPoint(x: stride + 30, y: 0), animated: false)
        XCTAssertEqual(cell.visibleMusicChartIDs(in: cell.bounds), Set(["first", "second"]))
        let outsideClip = CGRect(x: 0, y: frame(members, in: cell).minY,
                                 width: scroll.frame.minX, height: 44)
        XCTAssertFalse(frame(members, in: cell).intersection(outsideClip).isEmpty)
        XCTAssertTrue(cell.visibleMusicChartIDs(in: outsideClip).isEmpty)
        first.isHidden = true
        XCTAssertEqual(cell.visibleMusicChartIDs(in: cell.bounds), Set(["second"]))
        cell.isHidden = true
        XCTAssertTrue(cell.visibleMusicChartIDs(in: cell.bounds).isEmpty)
    }

    func testIdleAndLoadingHaveStableMemberGeometryAndFailureDoesNotBecomeEmpty() throws {
        let cell = railCell()
        var rows = 0, plays = 0
        cell.configure(cards: [card("music", state: .idle)], theme: PageTheme(),
                       onRow: { _, _ in rows += 1 }, onPlay: { _ in plays += 1 })
        let idleHeight = cell.height(for: 320)
        layout(cell, width: 320, height: idleHeight)
        let members = try view("music.search.chart.members.music", in: cell)
        let idleFrame = frame(members, in: cell)
        let empty = try label("music.search.chart.empty.music", in: cell)
        let spinner = try XCTUnwrap(identified("music.search.chart.loading.music", in: cell)
            as? UIActivityIndicatorView)
        XCTAssertTrue(empty.isHidden); XCTAssertFalse(spinner.isAnimating)
        XCTAssertEqual(cell.visibleMusicChartIDs(in: idleFrame), Set(["music"]))
        cell.configure(cards: [card("music", state: .loading)], theme: PageTheme(),
                       onRow: { _, _ in rows += 1 }, onPlay: { _ in plays += 1 })
        layout(cell, width: 320, height: cell.height(for: 320))
        XCTAssertTrue(try view("music.search.chart.members.music", in: cell) === members)
        XCTAssertEqual(frame(members, in: cell), idleFrame)
        XCTAssertEqual(cell.height(for: 320), idleHeight, accuracy: 0.5)
        XCTAssertTrue(spinner.isAnimating); XCTAssertTrue(empty.isHidden)
        cell.configure(cards: [card("music", state: .failed)], theme: PageTheme(),
                       onRow: { _, _ in rows += 1 }, onPlay: { _ in plays += 1 })
        layout(cell, width: 320, height: cell.height(for: 320))
        XCTAssertFalse(spinner.isAnimating); XCTAssertTrue(empty.isHidden)
        let play = try button("music.search.chart.play.music", in: cell)
        XCTAssertFalse(play.isHidden); XCTAssertFalse(play.isEnabled)
        play.sendActions(for: .touchUpInside)
        XCTAssertEqual(rows, 0); XCTAssertEqual(plays, 0)
        cell.configure(cards: [card("hot", isMusic: false), card("music")], theme: PageTheme(),
                       onRow: { _, _ in }, onPlay: { _ in })
        layout(cell, width: 320, height: cell.height(for: 320))
        XCTAssertFalse(empty.isHidden); XCTAssertEqual(empty.text, "暂无榜单歌曲")
        XCTAssertFalse(play.isEnabled)
        let hotEmpty = try label("music.search.chart.empty.hot", in: cell)
        XCTAssertFalse(hotEmpty.isHidden); XCTAssertEqual(hotEmpty.text, "暂无热搜关键词")
        XCTAssertTrue(try button("music.search.chart.play.hot", in: cell).isHidden)
    }

    func testLoadingRetainsUnchangedRowViewsWithoutEnablingOldActions() throws {
        let cell = railCell()
        let row = MusicSearchChartRowViewData(id: "a", rank: 3, title: "当前歌曲")
        var selected = 0, played = 0
        cell.configure(cards: [card("music", rows: [row])], theme: PageTheme(),
                       onRow: { _, _ in selected += 1 }, onPlay: { _ in played += 1 })
        layout(cell, width: 320, height: cell.height(for: 320))
        let control = try button("music.search.chart.row.music.a", in: cell)
        cell.configure(cards: [card("music", rows: [row], state: .loading)], theme: PageTheme(),
                       onRow: { _, _ in selected += 1 }, onPlay: { _ in played += 1 })
        layout(cell, width: 320, height: cell.height(for: 320))
        XCTAssertTrue(try button("music.search.chart.row.music.a", in: cell) === control)
        XCTAssertTrue(control.isHidden); XCTAssertFalse(control.isEnabled)
        control.sendActions(for: .touchUpInside)
        try button("music.search.chart.play.music", in: cell).sendActions(for: .touchUpInside)
        XCTAssertEqual(selected, 0); XCTAssertEqual(played, 0)
    }

    func testRailFitsTwentyRowsWithNativeCardSurfaceAndNoInnerVerticalScroll() throws {
        let cell = railCell()
        let rows = (1...20).map { MusicSearchChartRowViewData(id: String($0), rank: $0, title: "歌曲\($0)") }
        let theme = PageTheme(palette: .mint)
        cell.configure(cards: [card("hot", rows: rows, isMusic: false), card("music", rows: rows)],
                       theme: theme, onRow: { _, _ in }, onPlay: { _ in })
        var measuredWidths: [CGFloat] = []
        for width in [CGFloat(292), CGFloat(390)] {
            layout(cell, width: width, height: cell.height(for: width))
            let scroll = try scrollView(in: cell)
            let hot = try view("music.search.chart.card.hot", in: cell)
            let music = try view("music.search.chart.card.music", in: cell)
            measuredWidths.append(hot.bounds.width)
            XCTAssertEqual(hot.bounds.width, 244, accuracy: 0.5)
            XCTAssertEqual(music.frame.minX - hot.frame.maxX, 12, accuracy: 0.5)
            XCTAssertLessThan(music.frame.minX, scroll.bounds.maxX)
            XCTAssertGreaterThan(music.frame.maxX, scroll.bounds.maxX)
            XCTAssertEqual(hot.layer.cornerRadius, 12, accuracy: 0.1)
            XCTAssertGreaterThan(hot.layer.borderWidth, 0)
            XCTAssertLessThanOrEqual(hot.layer.borderWidth, 1)
            XCTAssertEqual(hot.backgroundColor, UIColor(theme.surface))
            XCTAssertEqual(try XCTUnwrap(hot.backgroundColor).cgColor.alpha, 1, accuracy: 0.01)
            XCTAssertGreaterThanOrEqual(cell.height(for: width), 20 * 44)
            XCTAssertEqual(scroll.contentSize.height, scroll.bounds.height, accuracy: 0.5)
            XCTAssertEqual(scroll.contentOffset.y, 0, accuracy: 0.5)
            XCTAssertFalse(scroll.isPagingEnabled); XCTAssertFalse(scroll.alwaysBounceVertical)
            var previous: CGRect?
            for row in rows {
                let control = try button("music.search.chart.row.hot.\(row.id)", in: cell)
                let current = frame(control, in: hot)
                XCTAssertTrue(hot.bounds.contains(current))
                XCTAssertGreaterThanOrEqual(control.bounds.height, 44)
                if let previous { XCTAssertGreaterThanOrEqual(current.minY, previous.maxY) }
                previous = current
            }
            XCTAssertLessThanOrEqual(frame(hot, in: cell).maxY, cell.bounds.maxY)
        }
        XCTAssertEqual(try XCTUnwrap(measuredWidths.first),
                       try XCTUnwrap(measuredWidths.last), accuracy: 0.5)
        let normalHeight = cell.height(for: 292)
        setContentSizeCategory(.accessibilityExtraExtraExtraLarge, on: cell)
        let enlargedHeight = cell.height(for: 292)
        layout(cell, width: 292, height: enlargedHeight)
        XCTAssertGreaterThan(enlargedHeight, normalHeight)
        let last = try button("music.search.chart.row.hot.20", in: cell)
        XCTAssertTrue(try view("music.search.chart.card.hot", in: cell).bounds.contains(
            frame(last, in: try view("music.search.chart.card.hot", in: cell))))
    }

    func testRailSnapsByProximityToCardStartsRatherThanPaging() throws {
        let cell = railCell()
        cell.configure(cards: [card("hot", isMusic: false), card("one"), card("two"), card("three")],
                       theme: PageTheme(), onRow: { _, _ in }, onPlay: { _ in })
        layout(cell, width: 390, height: cell.height(for: 390))
        let scroll = try scrollView(in: cell)
        let stride = try view("music.search.chart.card.one", in: cell).frame.minX
        var near = CGPoint(x: stride + 8, y: 10)
        withUnsafeMutablePointer(to: &near) {
            cell.scrollViewWillEndDragging(scroll, withVelocity: .zero, targetContentOffset: $0)
        }
        XCTAssertEqual(near.x, stride, accuracy: 0.5); XCTAssertEqual(near.y, 0)
        let distantX = stride * 1.45
        var distant = CGPoint(x: distantX, y: 0)
        withUnsafeMutablePointer(to: &distant) {
            cell.scrollViewWillEndDragging(scroll, withVelocity: .zero, targetContentOffset: $0)
        }
        XCTAssertEqual(distant.x, distantX, accuracy: 0.5)
        var final = CGPoint(x: stride * 3 + 8, y: 0)
        withUnsafeMutablePointer(to: &final) {
            cell.scrollViewWillEndDragging(scroll, withVelocity: .zero, targetContentOffset: $0)
        }
        XCTAssertEqual(final.x, stride * 3, accuracy: 0.5)
        XCTAssertEqual(final.x, scroll.contentSize.width - scroll.bounds.width, accuracy: 0.5)
        XCTAssertFalse(scroll.isPagingEnabled)
    }

    func testUnchangedGeometryAndMeasurementDoNotEmitRepeatedViewportEventsOrActions() throws {
        let cell = railCell()
        let cards = [card("hot", isMusic: false), card("music", state: .idle)]
        var changes = 0, actions = 0
        cell.onViewportChanged = { changes += 1 }
        cell.configure(cards: cards, theme: PageTheme(), onRow: { _, _ in actions += 1 },
                       onPlay: { _ in actions += 1 })
        layout(cell, width: 390, height: cell.height(for: 390))
        XCTAssertGreaterThan(changes, 0)
        let baseline = changes
        _ = cell.height(for: 390)
        _ = cell.visibleMusicChartIDs(in: cell.bounds)
        cell.setNeedsLayout(); cell.layoutIfNeeded()
        cell.configure(cards: cards, theme: PageTheme(palette: .mint), onRow: { _, _ in actions += 1 },
                       onPlay: { _ in actions += 1 })
        layout(cell, width: 390, height: cell.height(for: 390))
        XCTAssertEqual(changes, baseline); XCTAssertEqual(actions, 0)
        let scroll = try scrollView(in: cell)
        scroll.setContentOffset(CGPoint(x: 40, y: 0), animated: false)
        cell.scrollViewDidScroll(scroll)
        XCTAssertGreaterThan(changes, baseline)
        let afterScroll = changes
        cell.scrollViewDidScroll(scroll)
        XCTAssertEqual(changes, afterScroll); XCTAssertEqual(actions, 0)
    }

    func testPrepareForReuseKeepsPageOwnedViewportCallback() throws {
        let cell = railCell()
        let cards = [card("hot", isMusic: false), card("music", state: .idle)]
        var changes = 0
        cell.onViewportChanged = { changes += 1 }
        cell.configure(cards: cards, theme: PageTheme(), onRow: { _, _ in }, onPlay: { _ in })
        layout(cell, width: 390, height: cell.height(for: 390))
        let baseline = changes

        cell.prepareForReuse()
        cell.configure(cards: cards, theme: PageTheme(), onRow: { _, _ in }, onPlay: { _ in })
        layout(cell, width: 390, height: cell.height(for: 390))

        XCTAssertGreaterThan(changes, baseline)
    }

    private func historyCell() -> MusicSearchHistoryCell {
        let cell = MusicSearchHistoryCell(style: .default, reuseIdentifier: nil)
        setContentSizeCategory(.large, on: cell)
        return cell
    }

    private func suggestionsCell() -> MusicSearchSuggestionsCell {
        let cell = MusicSearchSuggestionsCell(style: .default, reuseIdentifier: nil)
        setContentSizeCategory(.large, on: cell)
        return cell
    }

    private func railCell() -> MusicSearchChartRailCell {
        let cell = MusicSearchChartRailCell(style: .default, reuseIdentifier: nil)
        setContentSizeCategory(.large, on: cell)
        return cell
    }

    private func setContentSizeCategory(_ category: UIContentSizeCategory, on view: UIView) {
        view.traitOverrides.preferredContentSizeCategory = category
        view.updateTraitsIfNeeded()
    }

    private func card(_ id: String, title: String = "榜单", rows: [MusicSearchChartRowViewData] = [],
                      isMusic: Bool = true, showsPlayButton: Bool? = nil,
                      state: MusicSearchChartCardViewData.State = .loaded)
        -> MusicSearchChartCardViewData {
        .init(id: id, title: title, rows: rows, rowsAreMusic: isMusic,
              showsPlayButton: showsPlayButton ?? isMusic, state: state)
    }

    private func layout(_ cell: UITableViewCell, width: CGFloat, height: CGFloat) {
        cell.frame = CGRect(x: 0, y: 0, width: width, height: height)
        cell.setNeedsLayout(); cell.layoutIfNeeded(); cell.contentView.layoutIfNeeded()
    }

    private func frame(_ view: UIView, in parent: UIView) -> CGRect { view.convert(view.bounds, to: parent) }

    private func keywordID(_ word: String, prefix: String) -> String {
        prefix + ".keyword." + Data(word.utf8).base64EncodedString()
    }

    private func keywordButton(_ word: String, prefix: String, in root: UIView) throws -> UIButton {
        try button(keywordID(word, prefix: prefix), in: root)
    }

    private func keywordButtons(_ prefix: String, in root: UIView) -> [UIButton] {
        descendants(root).compactMap { $0 as? UIButton }.filter {
            $0.accessibilityIdentifier?.hasPrefix(prefix + ".keyword.") == true
        }
    }

    private func scrollView(in root: UIView) throws -> UIScrollView {
        try XCTUnwrap(identified("music.search.charts.scroll", in: root) as? UIScrollView)
    }

    private func button(_ id: String, in root: UIView) throws -> UIButton {
        try XCTUnwrap(identified(id, in: root) as? UIButton, id)
    }

    private func label(_ id: String, in root: UIView) throws -> UILabel {
        try XCTUnwrap(identified(id, in: root) as? UILabel, id)
    }

    private func view(_ id: String, in root: UIView) throws -> UIView { try XCTUnwrap(identified(id, in: root), id) }

    private func identified(_ id: String, in root: UIView) -> UIView? {
        ([root] + descendants(root)).first { $0.accessibilityIdentifier == id }
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }
}
#endif
