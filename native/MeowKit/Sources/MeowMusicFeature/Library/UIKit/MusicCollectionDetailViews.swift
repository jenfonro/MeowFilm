#if os(iOS)
import UIKit
import MeowUI

struct MusicCollectionDetailContent {
    let title: String
    let owner: String
    let count: Int
    let artwork: URL?
}

@MainActor
final class MusicCollectionDetailActionBar: UIView {
    static let actionHeight: CGFloat = 62
    static let searchHeight: CGFloat = 52

    private let background: WindowGradientView
    fileprivate let play = MusicCollectionPlayAllButton()
    let edit = UIButton(type: .system)
    let searchField = UITextField()
    fileprivate(set) var searchVisible = false
    var preferredHeight: CGFloat {
        Self.actionHeight + (searchVisible ? Self.searchHeight : 0)
    }

    init(themeProvider: any PageThemeProviding) {
        background = WindowGradientView(themeProvider: themeProvider)
        super.init(frame: .zero)
        clipsToBounds = true
        accessibilityIdentifier = "music.collection.actions"
        background.isUserInteractionEnabled = false
        background.accessibilityElementsHidden = true
        addSubview(background)

        play.accessibilityIdentifier = "music.collection.playAll"
        addSubview(play)

        edit.setImage(PageIcon.image(.multiSelect), for: .normal)
        edit.accessibilityLabel = "多选"
        edit.accessibilityIdentifier = "music.collection.edit"
        addSubview(edit)

        searchField.placeholder = "歌单内搜索"
        searchField.accessibilityLabel = "歌单内搜索"
        searchField.accessibilityIdentifier = "music.collection.searchField"
        searchField.clearButtonMode = .whileEditing
        searchField.returnKeyType = .done
        searchField.autocorrectionType = .no
        searchField.autocapitalizationType = .none
        searchField.layer.cornerRadius = 20
        searchField.layer.borderWidth = 1
        let searchIcon = UIImageView(image: PageIcon.image(.search, size: 16))
        let searchInset = UIView(frame: CGRect(x: 0, y: 0, width: 36, height: 40))
        searchIcon.frame = CGRect(x: 12, y: 12, width: 16, height: 16)
        searchInset.addSubview(searchIcon)
        searchField.leftView = searchInset
        searchField.leftViewMode = .always
        searchField.font = .systemFont(ofSize: 15)
        searchField.isHidden = true
        addSubview(searchField)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    /// The pinned bar paints the same window-projected canvas as the page.
    func updateBackgroundPosition() {
        background.setNeedsLayout()
        background.layoutIfNeeded()
    }

    func applyTheme(_ theme: PageTheme) {
        edit.tintColor = UIColor(theme.muted)
        searchField.tintColor = UIColor(theme.accent)
        searchField.textColor = UIColor(theme.ink)
        searchField.backgroundColor = UIColor(theme.control)
        searchField.layer.borderColor = UIColor(theme.border).cgColor
        searchField.leftView?.tintColor = UIColor(theme.muted)
        play.applyTheme(theme)
        background.applyTheme()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        background.frame = bounds
        let width = bounds.width
        let editWidth: CGFloat = edit.isHidden ? 0 : 60
        play.frame = CGRect(x: 0, y: 0, width: max(0, width - editWidth), height: Self.actionHeight)
        edit.frame = edit.isHidden ? .zero :
            CGRect(x: width - 16 - 44, y: 9, width: 44, height: 44)
        searchField.frame = searchVisible ?
            CGRect(x: 16, y: Self.actionHeight + 6, width: max(0, width - 32), height: 40) : .zero
        background.layoutIfNeeded()
    }
}

@MainActor
final class MusicCollectionDetailHeaderView: UIView, UITextFieldDelegate {
    private enum Metrics {
        static let informationInset: CGFloat = 12
        static let informationTop: CGFloat = 10
        static let informationBottom: CGFloat = 12
        static let informationPadding: CGFloat = 12
        static let coverRatio: CGFloat = 0.3
        static let copyGap: CGFloat = 16
        static let saveSide: CGFloat = 44
    }

    let actionBar: MusicCollectionDetailActionBar
    private let information = UIView()
    private let cover = UIView()
    private let artwork = MusicArtworkView(size: 0)
    private let titleLabel = UILabel()
    private let ownerLabel = UILabel()
    private let countLabel = UILabel()
    private let save = UIButton(type: .system)

    private let styleChoice = UIButton(type: .system)
    var onStyle: (() -> Void)?
    var onPlay: (() -> Void)?
    var onSave: (() -> Void)?
    var onEdit: (() -> Void)?
    var onRename: (() -> Void)?
    var onSearchChange: ((String) -> Void)?
    var onSearchVisibilityChange: (() -> Void)?

    private var isRecommendation = false
    private(set) var hasContent = false
    private var currentTheme = PageTheme()
    private(set) var collapseProgress: CGFloat = 0
    var isSearchVisible: Bool { actionBar.searchVisible }
    var searchField: UITextField { actionBar.searchField }

    init(themeProvider: any PageThemeProviding) {
        actionBar = MusicCollectionDetailActionBar(themeProvider: themeProvider)
        super.init(frame: .zero)
        clipsToBounds = false
        backgroundColor = .clear

        information.accessibilityIdentifier = "music.collection.information"
        addSubview(information)
        styleChoice.accessibilityIdentifier = "music.dailyStyle.current"
        styleChoice.titleLabel?.font = .systemFont(ofSize: 13)
        styleChoice.titleLabel?.lineBreakMode = .byTruncatingTail
        styleChoice.layer.cornerRadius = 15
        styleChoice.isHidden = true
        styleChoice.addAction(UIAction { [weak self] _ in self?.onStyle?() }, for: .touchUpInside)
        addSubview(styleChoice)

        cover.accessibilityIdentifier = "music.collection.cover"
        cover.isAccessibilityElement = true
        cover.accessibilityTraits = .image
        cover.layer.cornerRadius = 10
        cover.layer.shadowColor = UIColor.black.cgColor
        cover.layer.shadowOpacity = 0.14
        cover.layer.shadowRadius = 10
        cover.layer.shadowOffset = CGSize(width: 0, height: 5)
        artwork.layer.cornerRadius = 10
        artwork.isAccessibilityElement = false
        cover.addSubview(artwork)
        information.addSubview(cover)

        titleLabel.font = .systemFont(ofSize: 20, weight: .bold)
        titleLabel.numberOfLines = 2
        titleLabel.lineBreakMode = .byTruncatingTail
        ownerLabel.font = .systemFont(ofSize: 13, weight: .medium)
        countLabel.font = .systemFont(ofSize: 13)
        for label in [titleLabel, ownerLabel, countLabel] {
            label.isAccessibilityElement = false
            information.addSubview(label)
        }

        save.setImage(PageIcon.image(.heart, size: 22), for: .normal)
        save.accessibilityLabel = "收藏歌单"
        save.accessibilityIdentifier = "music.collection.save"
        save.addAction(UIAction { [weak self] _ in self?.onSave?() }, for: .touchUpInside)
        information.addSubview(save)

        actionBar.play.addAction(UIAction { [weak self] _ in self?.onPlay?() }, for: .touchUpInside)
        actionBar.edit.addAction(UIAction { [weak self] _ in self?.onEdit?() }, for: .touchUpInside)
        actionBar.searchField.delegate = self
        actionBar.searchField.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.onSearchChange?(self.actionBar.searchField.text ?? "")
        }, for: .editingChanged)

        applyTheme(currentTheme)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(
        content: MusicCollectionDetailContent?,
        filteredCount: Int,
        searchQuery: String,
        playTitle: String,
        isPlaying: Bool,
        canPlay: Bool,
        showsSave: Bool,
        canSave: Bool,
        showsEdit: Bool,
        canEdit: Bool,
        showsRename: Bool,
        canRename: Bool,
        theme: PageTheme,
        recommendation: Bool = false
    ) {
        guard let content else {
            hasContent = false
            isHidden = true
            actionBar.isHidden = true
            artwork.load(nil)
            setNeedsLayout()
            return
        }
        isRecommendation = recommendation
        hasContent = true
        isHidden = false
        actionBar.isHidden = false

        information.isHidden = recommendation
        titleLabel.text = content.title
        ownerLabel.text = content.owner
        countLabel.text = "\(content.count)首"
        cover.accessibilityLabel = [content.title, content.owner, countLabel.text]
            .compactMap { $0 }.joined(separator: "，")
        accessibilityIdentifier = "music.collection.header"

        artwork.load(recommendation ? nil : content.artwork)

        if actionBar.searchField.text != searchQuery { actionBar.searchField.text = searchQuery }
        actionBar.play.configure(title: playTitle, count: "\(filteredCount)首", playing: isPlaying,
                                 enabled: canPlay, theme: theme)
        save.isHidden = !showsSave
        save.isEnabled = canSave
        save.alpha = canSave ? 1 : 0.38
        actionBar.edit.isHidden = !showsEdit
        actionBar.edit.isEnabled = canEdit
        configureRenameAction(shows: showsRename, enabled: canRename)
        applyTheme(theme)
        setNeedsLayout()
        actionBar.setNeedsLayout()
    }

    func configureStyle(title: String?, enabled: Bool) {
        styleChoice.isHidden = title == nil
        styleChoice.setTitle(title.map { $0 + "  ▾" }, for: .normal)
        styleChoice.accessibilityLabel = title.map { "选择推荐风格，" + $0 }
        styleChoice.isEnabled = enabled
        setNeedsLayout()
    }

    func preferredHeight(for width: CGFloat) -> CGFloat {
        guard hasContent else { return 0 }
        return heroHeight(for: width) + actionBar.preferredHeight + (styleChoice.isHidden ? 0 : 52)
    }

    /// Height occupied by the scrolling information; the remaining header space reserves the pinned bar.
    func heroHeight(for width: CGFloat) -> CGFloat {
        hasContent && !isRecommendation ? ceil(Self.heroHeight(for: width)) : 0
    }

    /// 0 shows the complete information; 1 means it has left the visible content.
    func setCollapseProgress(_ progress: CGFloat) {
        let value = min(1, max(0, progress))
        guard abs(value - collapseProgress) > 0.001 else { return }
        collapseProgress = value
        information.alpha = max(0, 1 - value * 1.25)
        let scale = 1 - value * 0.04
        information.transform = CGAffineTransform(translationX: 0, y: -value * 10).scaledBy(x: scale, y: scale)
    }

    func setSearchVisible(_ visible: Bool, focus: Bool = false) {
        let field = actionBar.searchField
        if actionBar.searchVisible == visible {
            if visible, focus { field.becomeFirstResponder() }
            return
        }
        actionBar.searchVisible = visible
        field.isHidden = !visible
        if !visible {
            field.resignFirstResponder()
            if field.text?.isEmpty == false {
                field.text = ""
                onSearchChange?("")
            }
        }
        actionBar.setNeedsLayout()
        onSearchVisibilityChange?()
        if visible, focus { field.becomeFirstResponder() }
    }

    func applyTheme(_ theme: PageTheme) {
        currentTheme = theme
        let accent = UIColor(theme.accent)
        titleLabel.textColor = UIColor(theme.ink)
        ownerLabel.textColor = accent
        countLabel.textColor = UIColor(theme.muted)
        artwork.backgroundColor = UIColor(theme.chip)
        save.tintColor = accent
        styleChoice.tintColor = accent
        styleChoice.backgroundColor = UIColor(theme.chip)
        actionBar.applyTheme(theme)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        guard hasContent else { return }
        let width = bounds.width
        if !styleChoice.isHidden {
            let size = styleChoice.sizeThatFits(CGSize(width: width - 56, height: 30))
            styleChoice.frame = CGRect(x: 16, y: heroHeight(for: width) + actionBar.preferredHeight + 8,
                                       width: min(width - 32, size.width + 24), height: 30)
        }
        let informationFrame = CGRect(x: Metrics.informationInset, y: Metrics.informationTop,
                               width: max(0, width - Metrics.informationInset * 2),
                               height: Self.informationHeight(for: width))
        let transform = information.transform
        information.transform = .identity
        information.frame = informationFrame
        information.transform = transform
        let coverSide = Self.coverSide(for: width)
        cover.frame = CGRect(x: Metrics.informationPadding, y: Metrics.informationPadding,
                             width: coverSide, height: coverSide)
        artwork.frame = cover.bounds
        cover.layer.shadowPath = UIBezierPath(roundedRect: cover.bounds, cornerRadius: 10).cgPath

        let informationWidth = information.bounds.width
        let copyX = cover.frame.maxX + Metrics.copyGap
        let copyWidth = max(0, informationWidth - copyX - Metrics.informationPadding)
        let measuredTitle = titleLabel.sizeThatFits(CGSize(width: copyWidth, height: 60)).height
        let titleHeight: CGFloat = measuredTitle > 30 ? 56 : 28
        let metaHeight: CGFloat = 19.5
        let copyHeight = titleHeight + 8 + metaHeight * 2
        let copyY = max(Metrics.informationPadding, cover.frame.midY - copyHeight / 2)
        let metaWidth = save.isHidden ? copyWidth : max(0, copyWidth - Metrics.saveSide - 4)
        titleLabel.frame = CGRect(x: copyX, y: copyY, width: copyWidth, height: titleHeight)
        ownerLabel.frame = CGRect(x: copyX, y: titleLabel.frame.maxY + 8,
                                  width: metaWidth, height: metaHeight)
        countLabel.frame = CGRect(x: copyX, y: ownerLabel.frame.maxY,
                                  width: metaWidth, height: metaHeight)
        save.frame = save.isHidden ? .zero : CGRect(
            x: informationWidth - Metrics.informationPadding - Metrics.saveSide + 6,
            y: (ownerLabel.frame.minY + countLabel.frame.maxY - Metrics.saveSide) / 2,
            width: Metrics.saveSide,
            height: Metrics.saveSide
        )
    }

    func textFieldShouldReturn(_ textField: UITextField) -> Bool {
        textField.resignFirstResponder()
        return true
    }

    private func configureRenameAction(shows: Bool, enabled: Bool) {
        let edit = actionBar.edit
        guard shows else {
            edit.menu = nil
            edit.accessibilityCustomActions = []
            return
        }
        var attributes: UIMenuElement.Attributes = []
        if !enabled { attributes.insert(.disabled) }
        edit.menu = UIMenu(children: [
            UIAction(title: "重命名", image: UIImage(systemName: "pencil"),
                     attributes: attributes) { [weak self] _ in self?.onRename?() },
        ])
        edit.showsMenuAsPrimaryAction = false
        edit.accessibilityCustomActions = [
            UIAccessibilityCustomAction(name: "重命名") { [weak self] _ in
                guard enabled else { return false }
                self?.onRename?()
                return true
            },
        ]
    }

    private static func coverSide(for width: CGFloat) -> CGFloat {
        max(0, width - (Metrics.informationInset + Metrics.informationPadding) * 2) * Metrics.coverRatio
    }

    private static func informationHeight(for width: CGFloat) -> CGFloat {
        coverSide(for: width) + Metrics.informationPadding * 2
    }

    private static func heroHeight(for width: CGFloat) -> CGFloat {
        Metrics.informationTop + informationHeight(for: width) + Metrics.informationBottom
    }
}
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
            button.titleLabel?.font = .systemFont(ofSize: 14, weight: .medium)
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
private final class MusicCollectionPlayAllButton: UIButton {
    private let disc = UIView()
    private let glyph = UIImageView()
    private let titleLabelView = UILabel()
    private let countLabelView = UILabel()
    private var currentTheme = PageTheme()

    override init(frame: CGRect) {
        super.init(frame: frame)
        disc.isUserInteractionEnabled = false
        disc.layer.cornerRadius = 17
        glyph.contentMode = .center
        glyph.isUserInteractionEnabled = false
        titleLabelView.font = .systemFont(ofSize: 17)
        countLabelView.font = .systemFont(ofSize: 12)
        for view in [disc, glyph, titleLabelView, countLabelView] {
            view.isAccessibilityElement = false
            addSubview(view)
        }
        layer.cornerRadius = 10
        configurationUpdateHandler = { [weak self] button in
            guard let self else { return }
            self.backgroundColor = button.isHighlighted ? UIColor(self.currentTheme.chip) : .clear
            self.alpha = button.isEnabled ? 1 : 0.42
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(title: String, count: String, playing: Bool, enabled: Bool, theme: PageTheme) {
        titleLabelView.text = title
        countLabelView.text = count
        glyph.image = PageIcon.image(playing ? .pause : .play, size: 16)
        isEnabled = enabled
        accessibilityLabel = title
        accessibilityValue = count
        accessibilityTraits = playing ? [.button, .selected] : .button
        applyTheme(theme)
        setNeedsLayout()
    }

    func applyTheme(_ theme: PageTheme) {
        currentTheme = theme
        disc.backgroundColor = UIColor(theme.accent)
        glyph.tintColor = UIColor(theme.onAccent)
        titleLabelView.textColor = UIColor(theme.ink)
        countLabelView.textColor = UIColor(theme.muted)
        configurationUpdateHandler?(self)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        disc.frame = CGRect(x: 16, y: (bounds.height - 34) / 2, width: 34, height: 34)
        glyph.frame = disc.frame
        let titleSize = titleLabelView.sizeThatFits(CGSize(width: max(0, bounds.width - 92),
                                                          height: bounds.height))
        let countSize = countLabelView.sizeThatFits(CGSize(width: max(0, bounds.width - 92),
                                                          height: bounds.height))
        let copyWidth = min(max(0, bounds.width - 70), titleSize.width + 10 + countSize.width)
        let titleWidth = min(titleSize.width, max(0, copyWidth - countSize.width - 10))
        titleLabelView.frame = CGRect(x: 60, y: (bounds.height - 26) / 2,
                                      width: titleWidth, height: 26)
        countLabelView.frame = CGRect(x: titleLabelView.frame.maxX + 10,
                                      y: (bounds.height - 20) / 2,
                                      width: min(countSize.width, max(0, bounds.width - titleLabelView.frame.maxX - 10)),
                                      height: 20)
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
        titleLabel.font = .systemFont(ofSize: 16, weight: .medium)
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

    func configure(
        track: MusicRemoteTrack,
        active: Bool,
        isFavorite: Bool,
        favoriteEnabled: Bool,
        actionsEnabled: Bool,
        theme: PageTheme,
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
            artwork.load(track.artwork)
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
