#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicCollectionStatusFooterView: UIView {
    private let label = UILabel()
    private let skeleton = MusicLoadingSkeletonView()
    private let previousButton = UIButton(type: .system)
    private let nextButton = UIButton(type: .system)
    private var hasContent = false
    private var isLoading = false
    private var skeletonVisible = false

    var onPrevious: (() -> Void)?
    var onNext: (() -> Void)?
    var preferredHeight: CGFloat { isLoading ? MusicLoadingSkeletonView.preferredHeight : hasContent ? 92 : 0.01 }

    override init(frame: CGRect) {
        super.init(frame: frame)
        label.textAlignment = .center
        label.numberOfLines = 0
        label.font = .systemFont(ofSize: 14)
        previousButton.accessibilityIdentifier = "music.collection.previous"
        nextButton.accessibilityIdentifier = "music.collection.next"
        for button in [previousButton, nextButton] {
            button.titleLabel?.font = .systemFont(ofSize: 14)
            button.layer.cornerRadius = 8
            button.contentEdgeInsets = UIEdgeInsets(top: 8, left: 16, bottom: 8, right: 16)
        }
        previousButton.addAction(
            UIAction { [weak self] _ in self?.onPrevious?() },
            for: .touchUpInside
        )
        nextButton.addAction(
            UIAction { [weak self] _ in self?.onNext?() },
            for: .touchUpInside
        )
        addSubview(label)
        addSubview(previousButton)
        addSubview(nextButton)
        addSubview(skeleton)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(text: String?, previousTitle: String? = nil, nextTitle: String? = nil,
                   loading: Bool = false, visible: Bool = false, theme: PageTheme) {
        isLoading = loading
        skeletonVisible = visible
        label.text = text
        label.isHidden = text == nil
        previousButton.setTitle(previousTitle, for: .normal)
        previousButton.isHidden = previousTitle == nil
        nextButton.setTitle(nextTitle, for: .normal)
        nextButton.isHidden = nextTitle == nil
        hasContent = loading || text != nil || previousTitle != nil || nextTitle != nil
        isHidden = !hasContent
        applyTheme(theme)
        setNeedsLayout()
    }

    func applyTheme(_ theme: PageTheme) {
        skeleton.configure(loading: isLoading, visible: skeletonVisible, theme: theme)
        label.textColor = UIColor(theme.muted)
        for button in [previousButton, nextButton] {
            button.tintColor = UIColor(theme.ink)
            button.backgroundColor = UIColor(theme.chip)
        }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        guard hasContent else { return }
        skeleton.frame = bounds
        if isLoading { return }
        let buttons = [previousButton, nextButton].filter { !$0.isHidden }
        let buttonHeight: CGFloat = buttons.isEmpty ? 0 : 40
        let labelHeight: CGFloat = label.isHidden ? 0 : 24
        let gap: CGFloat = labelHeight > 0 && buttonHeight > 0 ? 8 : 0
        let contentHeight = labelHeight + gap + buttonHeight
        var y = max(0, (bounds.height - contentHeight) / 2)
        if !label.isHidden {
            label.frame = CGRect(x: 16, y: y, width: max(0, bounds.width - 32), height: labelHeight)
            y += labelHeight + gap
        } else {
            label.frame = .zero
        }
        guard !buttons.isEmpty else {
            previousButton.frame = .zero
            nextButton.frame = .zero
            return
        }
        let spacing: CGFloat = buttons.count > 1 ? 10 : 0
        let width = min(132, max(96, (bounds.width - 32 - spacing) / CGFloat(buttons.count)))
        let total = width * CGFloat(buttons.count) + spacing
        var x = (bounds.width - total) / 2
        for button in buttons {
            button.frame = CGRect(x: x, y: y, width: width, height: buttonHeight)
            x += width + spacing
        }
        if previousButton.isHidden { previousButton.frame = .zero }
        if nextButton.isHidden { nextButton.frame = .zero }
    }

    func setDisplayVisible(_ visible: Bool) {
        skeletonVisible = visible
        skeleton.setDisplayVisible(visible)
    }
}

@MainActor
final class MusicCollectionTrackCell: UITableViewCell {
    static let reuseIdentifier = "MusicCollectionTrackCell"
    private(set) var representedReference: MusicReference?
    private(set) var representedActive = false

    private let info = UIButton(type: .custom)
    private let artwork = MusicArtworkView(size: 0)
    private let titleLabel = UILabel()
    private let subtitleLabel = UILabel()
    private let favorite = UIButton(type: .system)
    private let append = UIButton(type: .system)
    private let more = UIButton(type: .system)
    private var currentTheme = PageTheme()
    private var favoriteSelected = false
    private var configuredTrack: MusicRemoteTrack?
    private var configuredActionRevision: UUID?
    private var configuredFilteredIndex: Int?
    private var configuredOriginalIndex: Int?

    var onPlay: (() -> Void)?
    var onFavorite: (() -> Void)?
    var onAppend: (() -> Void)?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        selectionStyle = .none
        contentView.addSubview(info)
        info.layer.cornerRadius = 10
        info.addSubview(artwork)
        info.addSubview(titleLabel)
        info.addSubview(subtitleLabel)
        info.addAction(UIAction { [weak self] _ in self?.onPlay?() }, for: .touchUpInside)
        info.configurationUpdateHandler = { [weak self] button in
            guard let self else { return }
            button.backgroundColor = button.isHighlighted ? UIColor(self.currentTheme.chip) : .clear
        }

        artwork.layer.cornerRadius = 6
        artwork.isAccessibilityElement = false
        titleLabel.font = .systemFont(ofSize: 16)
        titleLabel.lineBreakMode = .byTruncatingTail
        subtitleLabel.font = .systemFont(ofSize: 12)
        subtitleLabel.lineBreakMode = .byTruncatingTail
        for label in [titleLabel, subtitleLabel] { label.isAccessibilityElement = false }

        favorite.setImage(PageIcon.image(.heart, size: 20), for: .normal)
        append.setImage(PageIcon.image(.append, size: 20), for: .normal)
        more.setImage(PageIcon.image(.more, size: 20), for: .normal)
        favorite.addAction(UIAction { [weak self] _ in self?.onFavorite?() }, for: .touchUpInside)
        append.addAction(UIAction { [weak self] _ in self?.onAppend?() }, for: .touchUpInside)
        more.showsMenuAsPrimaryAction = true
        for button in [favorite, append, more] { contentView.addSubview(button) }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        artwork.cancelPendingArtworkLoad()
        representedReference = nil
        representedActive = false
        configuredTrack = nil
        configuredActionRevision = nil
        configuredFilteredIndex = nil
        configuredOriginalIndex = nil
        onPlay = nil
        onFavorite = nil
        onAppend = nil
        more.menu = nil
    }

    func cancelPendingArtworkLoad() {
        artwork.cancelPendingArtworkLoad()
    }

    func resumePendingArtworkLoad() {
        artwork.resumePendingLoadIfNeeded()
    }

    func configure(
        track: MusicRemoteTrack,
        active: Bool,
        isFavorite: Bool,
        favoriteEnabled: Bool,
        actionsEnabled: Bool,
        theme: PageTheme,
        images: MusicArtworkImages,
        actionRevision: UUID,
        menu: @autoclosure () -> UIMenu?,
        filteredIndex: Int?,
        originalIndex: Int?,
        onPlay: @escaping () -> Void,
        onFavorite: @escaping () -> Void,
        onAppend: @escaping () -> Void
    ) {
        let contentChanged = configuredTrack != track
        let actionsChanged = contentChanged || configuredActionRevision != actionRevision ||
            configuredFilteredIndex != filteredIndex || configuredOriginalIndex != originalIndex
        let stateChanged = representedActive != active || favoriteSelected != isFavorite ||
            favorite.isEnabled != favoriteEnabled || info.isEnabled != actionsEnabled ||
            currentTheme != theme
        self.onPlay = onPlay
        self.onFavorite = onFavorite
        self.onAppend = onAppend
        guard contentChanged || actionsChanged || stateChanged else { return }
        representedReference = track.ref
        representedActive = active
        if contentChanged {
            configuredTrack = track
            artwork.load(track, images: images)
            titleLabel.text = track.title
            let subtitle = [track.artist, track.album].filter { !$0.isEmpty }.joined(separator: " · ")
            subtitleLabel.text = subtitle
            subtitleLabel.isHidden = subtitle.isEmpty
            info.accessibilityLabel = ["播放 \(track.title)", track.artist, track.album]
                .filter { !$0.isEmpty }.joined(separator: "，")
            info.accessibilityIdentifier = "music.collection.play.\(track.songId)"
            accessibilityIdentifier = "music.collection.track.\(track.songId)"
            favorite.accessibilityIdentifier = "music.collection.favorite.\(track.songId)"
            append.accessibilityLabel = "添加 \(track.title) 到播放队列"
            append.accessibilityIdentifier = "music.collection.append.\(track.songId)"
            more.accessibilityLabel = "\(track.title) 更多操作"
            more.accessibilityIdentifier = "music.collection.more.\(track.songId)"
        }
        if actionsChanged {
            configuredActionRevision = actionRevision
            configuredFilteredIndex = filteredIndex
            configuredOriginalIndex = originalIndex
            more.menu = menu()
        }
        favoriteSelected = isFavorite

        info.isEnabled = actionsEnabled
        info.accessibilityTraits = active ? [.button, .selected] : .button

        favorite.isEnabled = favoriteEnabled
        favorite.accessibilityLabel = "\(isFavorite ? "取消收藏" : "收藏") \(track.title)"
        favorite.accessibilityTraits = isFavorite ? [.button, .selected] : .button
        favorite.setImage(PageIcon.image(.heart, filled: isFavorite, size: 20), for: .normal)
        favorite.alpha = favoriteEnabled ? 1 : 0.38
        append.isEnabled = actionsEnabled
        append.alpha = actionsEnabled ? 1 : 0.38
        more.isEnabled = actionsEnabled && more.menu != nil
        more.alpha = more.isEnabled ? 1 : 0.38

        applyTheme(theme, active: active)
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let width = contentView.bounds.width
        let compact = width < 359
        let buttonWidth: CGFloat = compact ? 32 : 36
        let buttonGap: CGFloat = compact ? 2 : 3
        let actionsWidth = buttonWidth * 3 + buttonGap * 2
        let actionsX = max(0, width - 16 - actionsWidth)
        info.frame = CGRect(x: 0, y: 0, width: actionsX, height: contentView.bounds.height)
        artwork.frame = CGRect(x: 16, y: (contentView.bounds.height - 42) / 2,
                               width: 42, height: 42)
        let copyX: CGFloat = 69
        let copyWidth = max(0, actionsX - copyX - 4)
        if subtitleLabel.isHidden {
            titleLabel.frame = CGRect(x: copyX, y: (contentView.bounds.height - 26) / 2,
                                      width: copyWidth, height: 26)
            subtitleLabel.frame = .zero
        } else {
            titleLabel.frame = CGRect(x: copyX, y: (contentView.bounds.height - 46) / 2,
                                      width: copyWidth, height: 26)
            subtitleLabel.frame = CGRect(x: copyX, y: titleLabel.frame.maxY,
                                         width: copyWidth, height: 20)
        }
        for (index, button) in [favorite, append, more].enumerated() {
            button.frame = CGRect(x: actionsX + CGFloat(index) * (buttonWidth + buttonGap),
                                  y: (contentView.bounds.height - 44) / 2,
                                  width: buttonWidth, height: 44)
        }
    }

    private func applyTheme(_ theme: PageTheme, active: Bool) {
        currentTheme = theme
        artwork.backgroundColor = UIColor(theme.chip)
        titleLabel.textColor = UIColor(active ? theme.accent : theme.ink)
        subtitleLabel.textColor = UIColor(theme.muted)
        favorite.tintColor = UIColor(favoriteSelected ? theme.accent : theme.muted)
        append.tintColor = UIColor(theme.muted)
        more.tintColor = UIColor(theme.muted)
        info.configurationUpdateHandler?(info)
    }
}
#endif
