#if os(iOS)
import UIKit
import MeowUI

struct MusicSearchChartRowViewData: Equatable {
    let id: String
    let rank: Int
    let title: String
}

struct MusicSearchChartCardViewData: Equatable {
    enum State: Equatable { case idle, loading, loaded, failed }
    let id: String
    let title: String
    let rows: [MusicSearchChartRowViewData]
    let rowsAreMusic: Bool
    let showsPlayButton: Bool
    let state: State
}

@MainActor
final class MusicSearchChartRailCell: UITableViewCell, UIScrollViewDelegate {
    private static let preferredCardWidth: CGFloat = 244

    private struct Geometry: Equatable {
        let clip: CGRect
        let offset: CGPoint
        let musicIDs: [String]
        let members: [CGRect]
    }

    var onViewportChanged: (() -> Void)?
    private let scroll = UIScrollView()
    private var cards: [String: MusicSearchChartCardView] = [:]
    private var orderedCards: [MusicSearchChartCardView] = []
    private var onRow: ((String, String) -> Void)?
    private var onPlay: ((String) -> Void)?
    private var lastGeometry: Geometry?
    private var layingOut = false

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear; selectionStyle = .none
        accessibilityIdentifier = "music.search.charts"
        scroll.accessibilityIdentifier = "music.search.charts.scroll"
        scroll.showsHorizontalScrollIndicator = false; scroll.showsVerticalScrollIndicator = false
        scroll.isPagingEnabled = false
        scroll.alwaysBounceVertical = false; scroll.isDirectionalLockEnabled = true
        scroll.contentInsetAdjustmentBehavior = .never
        scroll.decelerationRate = .fast
        scroll.clipsToBounds = true; scroll.delegate = self
        contentView.addSubview(scroll)
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (cell: MusicSearchChartRailCell, _: UITraitCollection) in
            cell.invalidateIntrinsicContentSize(); cell.setNeedsLayout()
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(cards: [MusicSearchChartCardViewData], theme: PageTheme,
                   onRow: @escaping (String, String) -> Void, onPlay: @escaping (String) -> Void) {
        self.onRow = onRow; self.onPlay = onPlay
        let retained = Set(cards.map(\.id))
        for id in self.cards.keys.filter({ !retained.contains($0) }) {
            self.cards.removeValue(forKey: id)?.removeFromSuperview()
        }
        orderedCards = cards.map { data in
            let card: MusicSearchChartCardView
            if let existing = self.cards[data.id] {
                card = existing
            } else {
                card = MusicSearchChartCardView(frame: .zero)
                card.onRow = { [weak self] chartID, rowID in self?.onRow?(chartID, rowID) }
                card.onPlay = { [weak self] chartID in self?.onPlay?(chartID) }
                self.cards[data.id] = card
                scroll.addSubview(card)
            }
            card.configure(data: data, theme: theme)
            return card
        }
        scroll.alwaysBounceHorizontal = cards.count > 1
        setNeedsLayout()
    }

    func height(for width: CGFloat) -> CGFloat {
        guard !orderedCards.isEmpty else { return 0 }
        let width = cardWidth(for: width)
        return (orderedCards.map { $0.height(for: width) }.max() ?? 0) + 12
    }

    func visibleMusicChartIDs(in viewport: CGRect) -> Set<String> {
        guard !isHidden, alpha > 0.01, !contentView.isHidden, contentView.alpha > 0.01,
              !scroll.isHidden, scroll.alpha > 0.01 else { return [] }
        let clip = viewport.intersection(bounds)
            .intersection(contentView.convert(contentView.bounds, to: self))
            .intersection(scroll.convert(scroll.bounds, to: self))
        guard !clip.isNull, !clip.isEmpty else { return [] }
        return Set(orderedCards.compactMap { card in
            guard card.rowsAreMusic, !card.isHidden, card.alpha > 0.01,
                  !card.members.isHidden, card.members.alpha > 0.01 else { return nil }
            let members = card.members.convert(card.members.bounds, to: self)
            let visible = members.intersection(clip).intersection(card.convert(card.bounds, to: self))
            return !visible.isNull && !visible.isEmpty ? card.id : nil
        })
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        layingOut = true
        let oldOffset = scroll.contentOffset
        let oldWidth = scroll.bounds.width
        let oldContentWidth = scroll.contentSize.width
        let available = max(0, contentView.bounds.width - 32)
        scroll.frame = CGRect(x: 16, y: 0, width: available,
                              height: max(0, contentView.bounds.height - 12))
        let width = cardWidth(for: contentView.bounds.width)
        let stride = width + 12
        let height = orderedCards.map { $0.height(for: width) }.max() ?? 0
        for (index, card) in orderedCards.enumerated() {
            card.frame = CGRect(x: CGFloat(index) * stride, y: 0,
                                width: width, height: height)
            card.setNeedsLayout(); card.layoutIfNeeded()
        }
        // The trailing inset lets even the final card settle at its own start.
        let contentWidth = available + CGFloat(max(0, orderedCards.count - 1)) * stride
        scroll.contentSize = CGSize(width: contentWidth, height: scroll.bounds.height)
        let resized = oldWidth != available || oldContentWidth != contentWidth
        let x = resized ? min(max(0, oldOffset.x), max(0, contentWidth - available)) : oldOffset.x
        let offset = CGPoint(x: x, y: 0)
        if scroll.contentOffset != offset { scroll.setContentOffset(offset, animated: false) }
        layingOut = false
        reportViewportChange()
    }

    func scrollViewDidScroll(_ scrollView: UIScrollView) {
        guard !layingOut else { return }
        reportViewportChange()
    }

    func scrollViewWillEndDragging(_ scrollView: UIScrollView, withVelocity velocity: CGPoint,
                                  targetContentOffset: UnsafeMutablePointer<CGPoint>) {
        guard !orderedCards.isEmpty, scroll.bounds.width > 0 else { return }
        let stride = cardWidth(for: contentView.bounds.width) + 12
        let index = min(max(0, (targetContentOffset.pointee.x / stride).rounded()),
                        CGFloat(orderedCards.count - 1))
        let start = index * stride
        if abs(targetContentOffset.pointee.x - start) <= min(44, stride * 0.2) {
            targetContentOffset.pointee.x = start
        }
        targetContentOffset.pointee.y = 0
    }

    override func prepareForReuse() {
        super.prepareForReuse()
        onRow = nil; onPlay = nil; lastGeometry = nil
    }

    private func cardWidth(for width: CGFloat) -> CGFloat {
        min(Self.preferredCardWidth, floor(max(0, width - 32)))
    }

    private func reportViewportChange() {
        let music = orderedCards.filter(\.rowsAreMusic)
        let geometry = Geometry(clip: scroll.convert(scroll.bounds, to: self).intersection(bounds),
                                offset: scroll.contentOffset, musicIDs: music.map(\.id),
                                members: music.map { $0.members.convert($0.members.bounds, to: self) })
        guard geometry != lastGeometry else { return }
        lastGeometry = geometry
        onViewportChanged?()
    }
}

@MainActor
private final class MusicSearchChartCardView: UIView {
    private static let maximumRows = 20
    private static let waitingRows = 5
    private static let horizontalInset: CGFloat = 12
    private static let topInset: CGFloat = 8
    private static let separatorTopGap: CGFloat = 2
    private static let membersTopGap: CGFloat = 8
    private static let bottomInset: CGFloat = 12
    private static let titlePlayGap: CGFloat = 6
    let members = UIView()
    var id: String { data?.id ?? "" }
    var rowsAreMusic: Bool { data?.rowsAreMusic == true }
    var onRow: ((String, String) -> Void)?
    var onPlay: ((String) -> Void)?
    private let heading = UILabel()
    private let playBackground = UIView()
    private let play = UIButton(type: .system)
    private let separator = UIView()
    private let spinner = UIActivityIndicatorView(style: .medium)
    private let empty = UILabel()
    private var data: MusicSearchChartCardViewData?
    private var rows: [String: MusicSearchChartRowButton] = [:]
    private var orderedRows: [MusicSearchChartRowButton] = []
    private var theme = PageTheme()
    private var titleFont = UIFont.systemFont(ofSize: 16)
    private var songFont = UIFont.systemFont(ofSize: 14)
    private var rankFont = UIFont.monospacedDigitSystemFont(ofSize: 14, weight: .regular)
    private var playFont = UIFont.systemFont(ofSize: 12)
    private var fontCategory: UIContentSizeCategory?

    override init(frame: CGRect) {
        super.init(frame: frame)
        isOpaque = true
        layer.cornerRadius = 12; layer.cornerCurve = .continuous
        layer.borderWidth = 0.5; clipsToBounds = true
        heading.numberOfLines = 1; heading.lineBreakMode = .byTruncatingTail
        heading.accessibilityTraits = .header
        play.titleLabel?.numberOfLines = 1
        play.titleLabel?.lineBreakMode = .byClipping
        playBackground.isUserInteractionEnabled = false
        separator.isUserInteractionEnabled = false
        empty.text = "暂无榜单歌曲"; empty.numberOfLines = 0
        spinner.hidesWhenStopped = true
        for view in [heading, playBackground, play, separator, members] { addSubview(view) }
        members.addSubview(empty); members.addSubview(spinner)
        play.addAction(UIAction { [weak self] _ in
            guard let self, let data = self.data, data.showsPlayButton,
                  data.state == .loaded, !self.orderedRows.isEmpty else { return }
            self.onPlay?(data.id)
        }, for: .touchUpInside)
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (view: MusicSearchChartCardView, _: UITraitCollection) in view.updateFonts()
        }
        updateFonts()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(data: MusicSearchChartCardViewData, theme: PageTheme) {
        self.data = data; self.theme = theme
        let displayedRows = Array(data.rows.prefix(Self.maximumRows))
        let retained = Set(displayedRows.map(\.id))
        for id in rows.keys.filter({ !retained.contains($0) }) {
            rows.removeValue(forKey: id)?.removeFromSuperview()
        }
        updateFonts()
        orderedRows = displayedRows.map { row in
            let button: MusicSearchChartRowButton
            if let existing = rows[row.id] {
                button = existing
            } else {
                button = MusicSearchChartRowButton(frame: .zero)
                button.onSelect = { [weak self] rowID in
                    guard let self, let data = self.data, data.state == .loaded,
                          self.orderedRows.contains(where: { $0.id == rowID }) else { return }
                    self.onRow?(data.id, rowID)
                }
                rows[row.id] = button
                members.addSubview(button)
            }
            button.configure(row: row, chartID: data.id, isMusic: data.rowsAreMusic,
                             font: songFont, rankFont: rankFont, theme: theme)
            button.isHidden = data.state != .loaded
            button.isEnabled = data.state == .loaded
            return button
        }
        heading.text = data.title; heading.textColor = UIColor(theme.ink)
        backgroundColor = UIColor(theme.surface).withAlphaComponent(1)
        layer.borderColor = UIColor(theme.border).cgColor
        separator.backgroundColor = UIColor(theme.border)
        playBackground.backgroundColor = UIColor(theme.chip)
        empty.textColor = UIColor(theme.muted); spinner.color = UIColor(theme.muted)
        play.isHidden = !data.showsPlayButton; playBackground.isHidden = play.isHidden
        play.isEnabled = data.showsPlayButton && data.state == .loaded && !orderedRows.isEmpty
        play.accessibilityLabel = "播放" + data.title
        empty.text = data.rowsAreMusic ? "暂无榜单歌曲" : "暂无热搜关键词"
        empty.isHidden = data.state != .loaded || !orderedRows.isEmpty
        if data.state == .loading {
            if !spinner.isAnimating { spinner.startAnimating() }
        } else {
            spinner.stopAnimating()
        }
        accessibilityIdentifier = "music.search.chart.card.\(data.id)"
        heading.accessibilityIdentifier = "music.search.chart.title.\(data.id)"
        play.accessibilityIdentifier = "music.search.chart.play.\(data.id)"
        separator.accessibilityIdentifier = "music.search.chart.separator.\(data.id)"
        members.accessibilityIdentifier = "music.search.chart.members.\(data.id)"
        empty.accessibilityIdentifier = "music.search.chart.empty.\(data.id)"
        spinner.accessibilityIdentifier = "music.search.chart.loading.\(data.id)"
        spinner.accessibilityLabel = data.title + "加载中"
        updatePlayStyle()
        setNeedsLayout()
    }

    func height(for width: CGFloat) -> CGFloat {
        updateFonts()
        return Self.topInset + headerHeight + Self.membersTopGap
            + membersHeight(for: max(0, width - Self.horizontalInset * 2)) + Self.bottomInset
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        updateFonts()
        let width = max(0, bounds.width - Self.horizontalInset * 2)
        if data?.showsPlayButton == true {
            let playWidth = min(width, max(44, ceil(("播放" as NSString).size(
                withAttributes: [.font: playFont]).width) + 30))
            let maximumTitleWidth = max(0, width - playWidth - Self.titlePlayGap)
            let titleWidth = min(maximumTitleWidth, max(0, ceil(heading.intrinsicContentSize.width)))
            let gap = titleWidth > 0 ? Self.titlePlayGap : 0
            heading.frame = CGRect(x: Self.horizontalInset, y: Self.topInset,
                                   width: titleWidth, height: headerHeight)
            play.frame = CGRect(x: heading.frame.maxX + gap, y: Self.topInset,
                                width: playWidth, height: headerHeight)
            let pillHeight = min(headerHeight, max(28, ceil(playFont.lineHeight + 10)))
            playBackground.frame = CGRect(x: play.frame.minX,
                                          y: Self.topInset + (headerHeight - pillHeight) / 2,
                                          width: playWidth, height: pillHeight)
            playBackground.layer.cornerRadius = pillHeight / 2
        } else {
            heading.frame = CGRect(x: Self.horizontalInset, y: Self.topInset,
                                   width: width, height: headerHeight)
            play.frame = .zero
            playBackground.frame = .zero
        }
        separator.frame = CGRect(x: Self.horizontalInset,
                                 y: Self.topInset + headerHeight + Self.separatorTopGap,
                                 width: width, height: 0.5)
        let membersY = Self.topInset + headerHeight + Self.membersTopGap
        members.frame = CGRect(x: Self.horizontalInset, y: membersY, width: width,
                               height: max(0, bounds.height - membersY - Self.bottomInset))
        let rankWidth = max(20, orderedRows.map { $0.rankWidth }.max() ?? 0)
        for (index, button) in orderedRows.enumerated() {
            button.rankColumnWidth = rankWidth
            button.frame = CGRect(x: 0, y: CGFloat(index) * rowHeight, width: width, height: rowHeight)
            button.setNeedsLayout(); button.layoutIfNeeded()
        }
        empty.frame = members.bounds
        let spinnerSize = spinner.sizeThatFits(members.bounds.size)
        spinner.frame = CGRect(x: (members.bounds.width - spinnerSize.width) / 2,
                               y: (members.bounds.height - spinnerSize.height) / 2,
                               width: spinnerSize.width, height: spinnerSize.height)
    }

    private var headerHeight: CGFloat {
        max(44, ceil(titleFont.lineHeight + 12), ceil(playFont.lineHeight + 16))
    }
    private var rowHeight: CGFloat { max(44, ceil(songFont.lineHeight + 16)) }

    private func membersHeight(for width: CGFloat) -> CGFloat {
        guard data?.state == .loaded else { return CGFloat(Self.waitingRows) * rowHeight }
        guard orderedRows.isEmpty else { return CGFloat(orderedRows.count) * rowHeight }
        guard width > 0 else { return rowHeight }
        return max(rowHeight, ceil(empty.sizeThatFits(
            CGSize(width: width, height: .greatestFiniteMagnitude)).height) + 12)
    }

    private func updateFonts() {
        let category = traitCollection.preferredContentSizeCategory
        guard fontCategory != category else { return }
        fontCategory = category
        titleFont = UIFontMetrics(forTextStyle: .headline).scaledFont(
            for: .systemFont(ofSize: 16), compatibleWith: traitCollection)
        songFont = UIFontMetrics(forTextStyle: .subheadline).scaledFont(
            for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
        rankFont = UIFontMetrics(forTextStyle: .subheadline).scaledFont(
            for: .monospacedDigitSystemFont(ofSize: 14, weight: .regular), compatibleWith: traitCollection)
        playFont = UIFontMetrics(forTextStyle: .caption1).scaledFont(
            for: .systemFont(ofSize: 12), compatibleWith: traitCollection)
        heading.font = titleFont; empty.font = songFont
        for button in orderedRows { button.updateFonts(font: songFont, rankFont: rankFont) }
        updatePlayStyle()
        invalidateIntrinsicContentSize(); setNeedsLayout()
    }

    private func updatePlayStyle() {
        var configuration = UIButton.Configuration.plain()
        configuration.title = "播放"
        configuration.image = UIImage(systemName: "play.fill", withConfiguration:
            UIImage.SymbolConfiguration(pointSize: 10))
        configuration.imagePadding = 4
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 0, leading: 8, bottom: 0, trailing: 8)
        configuration.titleLineBreakMode = .byClipping
        configuration.baseForegroundColor = UIColor(theme.ink)
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            [font = playFont] incoming in
            var attributes = incoming; attributes.font = font; return attributes
        }
        play.configuration = configuration
        play.titleLabel?.numberOfLines = 1
        play.titleLabel?.lineBreakMode = .byClipping
    }
}

@MainActor
private final class MusicSearchChartRowButton: UIButton {
    private(set) var id = ""
    private(set) var rankWidth: CGFloat = 0
    var onSelect: ((String) -> Void)?
    var rankColumnWidth: CGFloat = 20
    private let rank = UILabel()
    private let song = UILabel()

    override init(frame: CGRect) {
        super.init(frame: frame)
        rank.textAlignment = .right
        rank.isAccessibilityElement = false; song.isAccessibilityElement = false
        rank.isUserInteractionEnabled = false; song.isUserInteractionEnabled = false
        song.numberOfLines = 1; song.lineBreakMode = .byTruncatingTail
        addSubview(rank); addSubview(song)
        isAccessibilityElement = true; accessibilityTraits = .button
        addAction(UIAction { [weak self] _ in
            guard let self, self.isEnabled else { return }
            self.onSelect?(self.id)
        }, for: .touchUpInside)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(row: MusicSearchChartRowViewData, chartID: String, isMusic: Bool,
                   font: UIFont, rankFont: UIFont, theme: PageTheme) {
        id = row.id
        rank.text = String(row.rank); song.text = row.title
        rank.textColor = UIColor(row.rank <= 3 ? theme.accent : theme.muted)
        song.textColor = UIColor(theme.ink)
        rank.accessibilityIdentifier = "music.search.chart.rank.\(chartID).\(row.id)"
        song.accessibilityIdentifier = "music.search.chart.song.\(chartID).\(row.id)"
        accessibilityIdentifier = "music.search.chart.row.\(chartID).\(row.id)"
        accessibilityLabel = (isMusic ? "播放" : "搜索") + "第\(row.rank)名，" + row.title
        updateFonts(font: font, rankFont: rankFont)
    }

    func updateFonts(font: UIFont, rankFont: UIFont) {
        song.font = font; rank.font = rankFont
        rankWidth = ceil(((rank.text ?? "") as NSString).size(withAttributes: [.font: rankFont]).width)
        setNeedsLayout()
    }

    override var isHighlighted: Bool {
        didSet { alpha = isHighlighted ? 0.65 : 1 }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let width = min(bounds.width, rankColumnWidth)
        rank.frame = CGRect(x: 0, y: 0, width: width, height: bounds.height)
        song.frame = CGRect(x: width + 8, y: 0,
                            width: max(0, bounds.width - width - 8), height: bounds.height)
    }
}
#endif
