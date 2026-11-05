#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicSearchResultsTableView: UITableView {
    override func touchesShouldCancel(in view: UIView) -> Bool {
        view is UIButton || super.touchesShouldCancel(in: view)
    }
}

@MainActor
final class MusicSearchResultTabsView: UIView {
    var onSelect: ((Int) -> Void)?
    private let indicator = UIView()
    private var buttons: [UIButton] = []
    private var titles: [String] = []
    private var theme = PageTheme()
    private var position: CGFloat = 0
    private var selectedIndex = 0

    override init(frame: CGRect) {
        super.init(frame: frame)
        accessibilityIdentifier = "music.search.results.tabs"
        indicator.isUserInteractionEnabled = false
        indicator.layer.cornerRadius = 1.5
        indicator.accessibilityIdentifier = "music.search.results.tab.indicator"
        addSubview(indicator)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var intrinsicContentSize: CGSize { CGSize(width: UIView.noIntrinsicMetric, height: 48) }

    func setTitles(_ titles: [String], theme: PageTheme) {
        self.theme = theme
        if self.titles != titles {
            self.titles = titles
            for button in buttons { button.removeFromSuperview() }
            buttons = titles.enumerated().map { index, title in
                let button = MusicSearchHitButton(type: .system)
                button.setTitle(title, for: .normal)
                button.titleLabel?.font = .systemFont(ofSize: 17, weight: .semibold)
                button.titleLabel?.numberOfLines = 1
                button.titleLabel?.lineBreakMode = .byClipping
                button.contentEdgeInsets = .zero
                button.accessibilityIdentifier = "music.search.results.tab.\(index)"
                button.addAction(UIAction { [weak self] _ in self?.onSelect?(index) }, for: .touchUpInside)
                addSubview(button)
                return button
            }
            selectedIndex = min(selectedIndex, max(0, titles.count - 1))
            position = min(position, CGFloat(max(0, titles.count - 1)))
        }
        applyTheme(theme)
        setNeedsLayout()
    }

    func applyTheme(_ theme: PageTheme) {
        self.theme = theme
        indicator.backgroundColor = UIColor(theme.accent)
        applyPosition()
    }

    func setSelectedIndex(_ index: Int) {
        guard !buttons.isEmpty else { selectedIndex = 0; return }
        selectedIndex = min(buttons.count - 1, max(0, index))
        for (buttonIndex, button) in buttons.enumerated() {
            button.accessibilityTraits = buttonIndex == selectedIndex ? [.button, .selected] : .button
        }
    }

    func setPosition(_ position: CGFloat) {
        self.position = min(CGFloat(max(0, buttons.count - 1)), max(0, position))
        applyPosition()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        var x: CGFloat = 0
        let font = UIFont.systemFont(ofSize: 17, weight: .semibold)
        for (index, button) in buttons.enumerated() {
            let width = ceil((titles[index] as NSString).size(withAttributes: [.font: font]).width)
            button.frame = CGRect(x: x, y: 0, width: width, height: bounds.height)
            x += width + 32
        }
        applyPosition()
    }

    private func applyPosition() {
        guard let interval = MusicPagingTabTransition.interval(position: position, count: buttons.count) else {
            indicator.frame = .zero
            return
        }
        for (index, button) in buttons.enumerated() {
            let amount = MusicPagingTabTransition.selectionAmount(position: position, index: index)
            button.setTitleColor(UIColor(theme.muted.mixed(with: theme.ink, ratio: Double(amount))), for: .normal)
        }
        guard buttons[interval.lower].bounds.width > 0 else { return }
        let lower = indicatorFrame(for: buttons[interval.lower])
        let upper = indicatorFrame(for: buttons[interval.upper])
        indicator.frame = CGRect(
            x: MusicPagingTabTransition.interpolate(lower.minX, upper.minX, progress: interval.progress),
            y: max(0, bounds.height - 8.5),
            width: MusicPagingTabTransition.interpolate(lower.width, upper.width, progress: interval.progress),
            height: 2.5
        )
    }

    private func indicatorFrame(for button: UIButton) -> CGRect {
        let width = max(20, button.bounds.width * 0.68)
        return CGRect(x: button.frame.midX - width / 2, y: 0, width: width, height: 2.5)
    }
}

@MainActor
final class MusicSearchSongCell: UITableViewCell {
    static let reuseIdentifier = "MusicSearchSongCell"
    private let info = MusicSearchHitButton(type: .custom)
    private let titleLabel = UILabel(), subtitleLabel = UILabel()
    private let favorite = MusicSearchHitButton(type: .system)
    private let append = MusicSearchHitButton(type: .system)
    private let more = MusicSearchHitButton(type: .system)
    private var onPlay: (() -> Void)?
    private var onFavorite: (() -> Void)?
    private var onAppend: (() -> Void)?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        selectionStyle = .none
        titleLabel.font = .systemFont(ofSize: 14, weight: .medium)
        subtitleLabel.font = .systemFont(ofSize: 12)
        titleLabel.numberOfLines = 1; subtitleLabel.numberOfLines = 1
        titleLabel.lineBreakMode = .byTruncatingTail; subtitleLabel.lineBreakMode = .byTruncatingTail
        titleLabel.isAccessibilityElement = false; subtitleLabel.isAccessibilityElement = false
        info.contentHorizontalAlignment = .leading
        info.addSubview(titleLabel); info.addSubview(subtitleLabel)
        favorite.setImage(PageIcon.image(.heart, size: 20), for: .normal)
        append.setImage(PageIcon.image(.append, size: 20), for: .normal)
        more.setImage(PageIcon.image(.more, size: 20), for: .normal)
        more.showsMenuAsPrimaryAction = true
        info.addAction(UIAction { [weak self] _ in self?.onPlay?() }, for: .touchUpInside)
        favorite.addAction(UIAction { [weak self] _ in self?.onFavorite?() }, for: .touchUpInside)
        append.addAction(UIAction { [weak self] _ in self?.onAppend?() }, for: .touchUpInside)
        for view in [info, favorite, append, more] { contentView.addSubview(view) }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(song: MusicSearchSong, active: Bool, favorite isFavorite: Bool, enabled: Bool,
                   theme: PageTheme, onPlay: @escaping () -> Void,
                   onFavorite: @escaping () -> Void, onAppend: @escaping () -> Void, menu: UIMenu) {
        self.onPlay = onPlay; self.onFavorite = onFavorite; self.onAppend = onAppend
        titleLabel.text = song.track.title
        subtitleLabel.text = [song.track.artist, song.track.album].filter { !$0.isEmpty }.joined(separator: " · ")
        titleLabel.textColor = UIColor(active ? theme.accent : theme.ink)
        subtitleLabel.textColor = UIColor(theme.muted)
        favorite.setImage(PageIcon.image(.heart, filled: isFavorite, size: 20), for: .normal)
        favorite.tintColor = UIColor(isFavorite ? theme.accent : theme.muted)
        append.tintColor = UIColor(theme.muted); more.tintColor = UIColor(theme.muted)
        info.isEnabled = enabled; favorite.isEnabled = enabled; append.isEnabled = enabled; more.isEnabled = enabled
        more.menu = menu
        let suffix = Data(song.id.utf8).base64EncodedString()
        accessibilityIdentifier = "music.search.song.\(suffix)"
        info.accessibilityIdentifier = "music.search.song.info.\(suffix)"
        favorite.accessibilityIdentifier = "music.search.song.favorite.\(suffix)"
        append.accessibilityIdentifier = "music.search.song.append.\(suffix)"
        more.accessibilityIdentifier = "music.search.song.more.\(suffix)"
        titleLabel.accessibilityIdentifier = "music.search.song.title.\(suffix)"
        subtitleLabel.accessibilityIdentifier = "music.search.song.subtitle.\(suffix)"
        info.accessibilityLabel = "播放" + song.track.title
        favorite.accessibilityLabel = isFavorite ? "取消收藏" : "收藏"
        favorite.accessibilityTraits = isFavorite ? [.button, .selected] : .button
        append.accessibilityLabel = "添加到播放队列"
        more.accessibilityLabel = "更多"
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let actionWidth: CGFloat = 36, gap: CGFloat = 3
        let actionsWidth = actionWidth * 3 + gap * 2
        let actionsX = max(16, bounds.width - 12 - actionsWidth)
        info.frame = CGRect(x: 16, y: 0, width: max(0, actionsX - 24), height: bounds.height)
        titleLabel.frame = CGRect(x: 0, y: 14, width: info.bounds.width, height: 21)
        subtitleLabel.frame = CGRect(x: 0, y: 37, width: info.bounds.width, height: 18)
        for (index, button) in [favorite, append, more].enumerated() {
            button.frame = CGRect(x: actionsX + CGFloat(index) * (actionWidth + gap),
                                  y: 14, width: actionWidth, height: 44)
        }
    }

    override func prepareForReuse() {
        super.prepareForReuse()
        onPlay = nil; onFavorite = nil; onAppend = nil
        more.menu = nil
    }
}

@MainActor
final class MusicSearchSectionHeaderCell: UITableViewCell {
    static let reuseIdentifier = "MusicSearchSectionHeaderCell"
    private let titleLabel = UILabel()
    private let play = MusicSearchHitButton(type: .system)
    private var action: (() -> Void)?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear; selectionStyle = .none
        titleLabel.font = .systemFont(ofSize: 18, weight: .semibold)
        titleLabel.numberOfLines = 1
        titleLabel.lineBreakMode = .byTruncatingTail
        titleLabel.accessibilityIdentifier = "music.search.results.section.title"
        play.setImage(PageIcon.image(.play, size: 18), for: .normal)
        play.layer.cornerRadius = 16
        play.accessibilityLabel = "播放全部"
        play.accessibilityIdentifier = "music.search.results.section.play"
        play.addAction(UIAction { [weak self] _ in self?.action?() }, for: .touchUpInside)
        contentView.addSubview(titleLabel); contentView.addSubview(play)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(title: String, showsPlay: Bool, theme: PageTheme, action: @escaping () -> Void) {
        self.action = action
        titleLabel.text = title
        titleLabel.textColor = UIColor(theme.ink)
        play.backgroundColor = UIColor(theme.chip)
        play.tintColor = UIColor(theme.ink)
        play.isHidden = !showsPlay
        play.isEnabled = showsPlay
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let playSize: CGFloat = play.isHidden ? 0 : 32
        let maximum = max(0, bounds.width - 32 - playSize - (play.isHidden ? 0 : 3))
        let width = min(maximum, ceil(titleLabel.intrinsicContentSize.width))
        titleLabel.frame = CGRect(x: 16, y: 0, width: width, height: bounds.height)
        play.frame = play.isHidden ? .zero : CGRect(
            x: titleLabel.frame.maxX + 3,
            y: (bounds.height - playSize) / 2,
            width: playSize,
            height: playSize
        )
    }
}

@MainActor
final class MusicSearchHitButton: UIButton {
    override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        let horizontal = max(0, (44 - bounds.width) / 2)
        let vertical = max(0, (44 - bounds.height) / 2)
        return bounds.insetBy(dx: -horizontal, dy: -vertical).contains(point)
    }
}
#endif
