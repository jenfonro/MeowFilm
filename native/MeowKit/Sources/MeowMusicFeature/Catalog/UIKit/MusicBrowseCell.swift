#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicBrowseCell: UICollectionViewCell, UIGestureRecognizerDelegate {
    static let reuseIdentifier = "MusicBrowseCell"
    private let artwork = MusicArtworkView(size: 0)
    private let title = UILabel(), subtitle = UILabel(), preview = UILabel()
    private let reason = UILabel()
    private let previews = UIStackView()
    private let rankingCovers = (0..<2).map { _ in MusicArtworkView(size: 0) }
    private let play = UIButton(type: .system)
    private var action: (() -> Void)?
    private var style = MusicPublicBrowseSection.Layout.list
    private var openAction: (() -> Void)?
    private(set) var itemID = ""

    override init(frame: CGRect) {
        super.init(frame: frame)
        for item in [artwork, title, subtitle, preview, reason, previews] + rankingCovers + [play] {
            contentView.addSubview(item)
        }
        title.numberOfLines = 2; subtitle.numberOfLines = 1; preview.numberOfLines = 3
        title.font = .systemFont(ofSize: 13, weight: .medium)
        subtitle.font = .systemFont(ofSize: 11)
        preview.font = .systemFont(ofSize: 13)
        reason.font = .systemFont(ofSize: 10)
        previews.axis = .vertical; previews.spacing = 3; previews.distribution = .fillEqually
        play.setImage(PageIcon.image(.play, size: 17), for: .normal)
        play.backgroundColor = UIColor.white.withAlphaComponent(0.88)
        play.layer.cornerRadius = 16
        play.addAction(UIAction { [weak self] _ in self?.action?() }, for: .touchUpInside)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func updateArtwork(_ url: URL?) { artwork.load(url) }

    func configure(item: MusicPublicBrowseItem, layout: MusicPublicBrowseSection.Layout,
                   theme: PageTheme, artworkURL: URL? = nil,
                   onPreview: ((MusicRemoteTrack) -> Void)? = nil, onPlay: @escaping () -> Void) {
        style = layout; action = onPlay; itemID = item.id
        artwork.load(artworkURL ?? item.artwork)
        title.text = item.coverLabel.isEmpty ? item.title : item.title + "\n" + item.coverLabel
        subtitle.text = item.subtitle
        preview.text = item.ranking?.preview.enumerated().map { index, entry in
            let change = entry.lastRank.flatMap { last in entry.rank.map {
                last == 0 ? "新" : last == $0 ? "−" : last > $0 ? "▴" : "▾"
            }} ?? ""
            return "\(entry.rank ?? index + 1). \(entry.title)" +
                (entry.artist.isEmpty ? "" : " − " + entry.artist) + (change.isEmpty ? "" : "  " + change)
        }.joined(separator: "\n")
            ?? item.previewSongs.map(\.title).joined(separator: "\n")
        reason.text = item.recommendationReason
        reason.textColor = UIColor(theme.muted)
        if let frequency = item.ranking?.updateFrequency, !frequency.isEmpty {
            subtitle.text = [item.subtitle, frequency].filter { !$0.isEmpty }.joined(separator: " · ")
        }
        previews.isHidden = !(layout == .rankedSongs || layout == .vipRankedSongs)
        var available = previews.arrangedSubviews.compactMap { $0 as? MusicBrowsePreviewButton }
        let tracks = previews.isHidden ? [] : Array(item.previewSongs.prefix(3))
        let buttons = tracks.map { track -> MusicBrowsePreviewButton in
            if let index = available.firstIndex(where: { $0.reference == track.ref }) {
                let button = available.remove(at: index)
                button.configure(track: track, theme: theme) { onPreview?(track) }
                return button
            }
            return MusicBrowsePreviewButton(track: track, theme: theme) { onPreview?(track) }
        }
        for button in available {
            previews.removeArrangedSubview(button); button.removeFromSuperview()
        }
        for (index, button) in buttons.enumerated() {
            if previews.arrangedSubviews.indices.contains(index), previews.arrangedSubviews[index] === button { continue }
            previews.removeArrangedSubview(button)
            previews.insertArrangedSubview(button, at: index)
        }
        let covers = item.ranking?.preview.compactMap(\.artwork) ?? []
        for (index, view) in rankingCovers.enumerated() {
            view.isHidden = layout != .rankingStack || covers.count <= index + 1
            if !view.isHidden { view.load(covers[index + 1]) }
        }
        if layout == .rankingStack { artwork.load(covers.first ?? item.ranking?.songArtwork ?? item.artwork) }
        title.textColor = UIColor(theme.ink); subtitle.textColor = UIColor(theme.muted)
        preview.textColor = UIColor(theme.ink)
        play.tintColor = UIColor(theme.ink)
        switch item.content {
        case .track, .collection, .action, .daily: play.isHidden = false
        default: play.isHidden = true
        }
        if layout == .featureRail, case .action(let action) = item.content, action.isPersonalFM {
            play.isHidden = true
        }
        accessibilityLabel = [item.title, item.subtitle].filter { !$0.isEmpty }.joined(separator: "，")
        play.accessibilityLabel = "播放" + item.title
        play.accessibilityIdentifier = "music.browse.play.\(item.id)"
        contentView.backgroundColor = layout == .featureRail ? UIColor(theme.accent).withAlphaComponent(0.18) : .clear
        setNeedsLayout()
    }

    func setOpenAction(_ action: @escaping () -> Void) {
        openAction = action
        let tap = UITapGestureRecognizer(target: self, action: #selector(open))
        tap.delegate = self
        contentView.addGestureRecognizer(tap)
    }

    @objc private func open() { openAction?() }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldReceive touch: UITouch) -> Bool {
        var node = touch.view
        while let current = node, current !== contentView {
            if current is UIControl { return false }
            node = current.superview
        }
        return true
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let width = bounds.width, height = bounds.height
        let row = style == .songColumns || style == .vipSongs || style == .list
        if row {
            artwork.frame = CGRect(x: 0, y: 8, width: 46, height: 46)
            title.frame = CGRect(x: 56, y: 10, width: max(0, width - 100), height: 23)
            subtitle.frame = CGRect(x: 56, y: 35, width: max(0, width - 100), height: 18)
            play.frame = CGRect(x: width - 36, y: 16, width: 32, height: 32)
            preview.frame = .zero
            reason.frame = CGRect(x: 56, y: 51, width: max(0, width - 100), height: 14)
        } else if style == .rankingStack || style == .rankedSongs || style == .vipRankedSongs ||
                    style == .artistUpdates {
            artwork.frame = CGRect(x: 0, y: 4, width: 94, height: 94)
            title.frame = CGRect(x: 106, y: 8, width: max(0, width - 108), height: 36)
            subtitle.frame = CGRect(x: 106, y: 46, width: max(0, width - 108), height: 18)
            preview.frame = CGRect(x: 0, y: 104, width: width - 38, height: max(0, height - 104))
            play.frame = CGRect(x: width - 34, y: height - 34, width: 32, height: 32)
            reason.frame = .zero
            previews.frame = CGRect(x: 0, y: 44, width: width, height: max(0, height - 44))
            if !previews.isHidden {
                artwork.isHidden = true
                subtitle.frame = .zero; preview.frame = .zero
                title.frame = CGRect(x: 0, y: 0, width: width - 36, height: 36)
                play.frame = CGRect(x: width - 34, y: 2, width: 32, height: 32)
            }
            for (index, cover) in rankingCovers.enumerated() {
                cover.frame = CGRect(x: CGFloat(index + 1) * 28, y: 18, width: 66, height: 80)
            }
        } else {
            let artHeight = style == .featureRail || style == .banner ? height : min(width, max(0, height - 54))
            artwork.frame = CGRect(x: 0, y: 0, width: width, height: artHeight)
            title.frame = CGRect(x: 0, y: artHeight + 4, width: width, height: 34)
            subtitle.frame = CGRect(x: 0, y: artHeight + 39, width: width, height: 14)
            if style == .featureRail || style == .banner {
                title.frame = CGRect(x: 10, y: artHeight - 44, width: max(0, width - 50), height: 32)
                title.textColor = .white
                subtitle.frame = CGRect(x: 10, y: artHeight - 17, width: max(0, width - 50), height: 14)
                subtitle.textColor = .white
            }
            preview.frame = .zero
            play.frame = CGRect(x: width - 38, y: artHeight - 38, width: 32, height: 32)
            reason.frame = .zero
        }
        if previews.isHidden { artwork.isHidden = false }
    }
}

@MainActor
private final class MusicBrowsePreviewButton: UIButton {
    private let cover = MusicArtworkView(size: 40)
    private let title = UILabel(), artist = UILabel()
    private var action: (() -> Void)?
    private(set) var reference: MusicReference

    init(track: MusicRemoteTrack, theme: PageTheme, action: @escaping () -> Void) {
        reference = track.ref
        super.init(frame: .zero)
        title.font = .systemFont(ofSize: 14); artist.font = .systemFont(ofSize: 11)
        let copy = UIStackView(arrangedSubviews: [title, artist]); copy.axis = .vertical; copy.spacing = 3
        let row = UIStackView(arrangedSubviews: [cover, copy]); row.spacing = 10; row.alignment = .center
        row.isUserInteractionEnabled = false
        addSubview(row); row.pinEdges(to: self, insets: UIEdgeInsets(top: 3, left: 0, bottom: 3, right: 8))
        addAction(UIAction { [weak self] _ in self?.action?() }, for: .touchUpInside)
        configure(track: track, theme: theme, action: action)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    func configure(track: MusicRemoteTrack, theme: PageTheme, action: @escaping () -> Void) {
        reference = track.ref; self.action = action
        cover.load(track.artwork)
        title.text = track.title; title.textColor = UIColor(theme.ink)
        artist.text = track.artist; artist.textColor = UIColor(theme.muted)
        accessibilityLabel = "播放" + track.title
    }
}

@MainActor
final class MusicBrowseHeader: UICollectionReusableView {
    static let reuseIdentifier = "MusicBrowseHeader"
    private let title = UILabel(), creator = UILabel()
    private let artwork = MusicArtworkView(size: 0)
    private let play = UIButton(type: .system), choices = UIButton(type: .system)
    private let tabs = UIStackView()
    private let avatars = (0..<3).map { _ in MusicArtworkView(size: 0) }
    private let errorLabel = UILabel(), retry = UIButton(type: .system)
    private var playAction: (() -> Void)?
    private var headingHeight: CGFloat = 0

    static func height(for section: MusicPublicBrowseSection, collection: MusicPublicBrowseCollection?) -> CGFloat {
        if collection != nil { return 190 }
        return headingHeight(for: section) + (section.tabs.isEmpty ? 0 : 36)
    }

    private static func headingHeight(for section: MusicPublicBrowseSection) -> CGFloat {
        let hasTrack = section.items.contains {
            if case .track = $0.content { return true }; return false
        }
        return !section.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || hasTrack ||
            !section.filters.isEmpty || section.channel != nil || !section.avatars.isEmpty ? 44 : 0
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        title.font = .systemFont(ofSize: 16, weight: .semibold)
        creator.font = .systemFont(ofSize: 12)
        title.numberOfLines = 2
        play.setImage(PageIcon.image(.play), for: .normal)
        play.addAction(UIAction { [weak self] _ in self?.playAction?() }, for: .touchUpInside)
        choices.setImage(UIImage(systemName: "chevron.down"), for: .normal)
        choices.showsMenuAsPrimaryAction = true
        tabs.axis = .horizontal; tabs.spacing = 9; tabs.distribution = .fillEqually
        errorLabel.numberOfLines = 0; errorLabel.font = .systemFont(ofSize: 13)
        retry.setTitle("重试", for: .normal)
        for item in [title, creator, artwork, play, choices, tabs, errorLabel, retry] + avatars { addSubview(item) }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(section: MusicPublicBrowseSection, collection: MusicPublicBrowseCollection?,
                   theme: PageTheme, onPlay: @escaping () -> Void,
                   onChannel: @escaping () -> Void, onChoice: @escaping (String) -> Void,
                   onRetry: @escaping () -> Void, error: String?) {
        headingHeight = Self.headingHeight(for: section)
        title.text = collection?.title ?? section.title
        title.textColor = UIColor(theme.ink)
        creator.textColor = UIColor(theme.muted)
        artwork.isHidden = collection == nil
        creator.isHidden = collection == nil
        if let collection {
            artwork.load(collection.artwork)
            creator.text = [collection.creator, collection.count.map { "\($0)首" }]
                .compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · ")
        }
        playAction = onPlay
        play.isHidden = collection == nil && !section.items.contains {
            if case .track = $0.content { return true }; return false
        }
        for (index, avatar) in avatars.enumerated() {
            avatar.isHidden = index >= section.avatars.count
            if !avatar.isHidden { avatar.load(section.avatars[index]); avatar.layer.cornerRadius = 12.5 }
        }
        errorLabel.text = error; errorLabel.textColor = UIColor(theme.muted)
        retry.isHidden = error == nil; retry.tintColor = UIColor(theme.ink)
        retry.removeAction(identifiedBy: UIAction.Identifier("retry"), for: .touchUpInside)
        retry.addAction(UIAction(identifier: UIAction.Identifier("retry")) { _ in onRetry() }, for: .touchUpInside)
        play.tintColor = UIColor(theme.ink); choices.tintColor = UIColor(theme.ink)
        var actions: [UIMenuElement] = section.filters.map { filter in
            UIAction(title: filter.title, state: filter.id == section.selectedFilter ? .on : .off) {
                _ in onChoice(filter.id)
            }
        }
        if section.channel != nil {
            actions.append(UIAction(title: "查看更多") { _ in onChannel() })
        }
        choices.menu = UIMenu(children: actions)
        choices.isHidden = actions.isEmpty
        for item in tabs.arrangedSubviews { tabs.removeArrangedSubview(item); item.removeFromSuperview() }
        for tab in section.tabs {
            let button = UIButton(type: .system)
            button.setTitle(tab.title, for: .normal)
            button.tintColor = UIColor(tab.id == section.selectedTab ? theme.accent : theme.ink)
            button.accessibilityTraits = tab.id == section.selectedTab ? [.button, .selected] : .button
            button.addAction(UIAction { _ in onChoice(tab.id) }, for: .touchUpInside)
            tabs.addArrangedSubview(button)
        }
        accessibilityIdentifier = "music.browse.heading.\(section.id)"
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let hero = !artwork.isHidden
        if hero {
            artwork.frame = CGRect(x: 0, y: 20, width: 110, height: 110)
            title.frame = CGRect(x: 128, y: 26, width: max(0, bounds.width - 128), height: 60)
            creator.frame = CGRect(x: 128, y: 92, width: max(0, bounds.width - 128), height: 30)
            play.frame = CGRect(x: 0, y: 142, width: 44, height: 44)
            choices.frame = CGRect(x: bounds.width - 44, y: 142, width: 44, height: 44)
            tabs.frame = .zero
        } else {
            title.frame = CGRect(x: 0, y: 2, width: max(0, bounds.width - 88),
                                 height: max(0, headingHeight - 6))
            play.frame = CGRect(x: bounds.width - 88, y: 0, width: 44, height: headingHeight)
            choices.frame = CGRect(x: bounds.width - 44, y: 0, width: 44, height: headingHeight)
            tabs.frame = CGRect(x: 0, y: headingHeight, width: bounds.width,
                                height: max(0, bounds.height - headingHeight))
        }
        if errorLabel.text != nil {
            let y = bounds.height - 64
            errorLabel.frame = CGRect(x: 0, y: y, width: max(0, bounds.width - 84), height: 60)
            retry.frame = CGRect(x: bounds.width - 80, y: y + 8, width: 76, height: 44)
            if !hero { tabs.frame.size.height = max(0, y - headingHeight) }
        } else { errorLabel.frame = .zero }
        for (index, avatar) in avatars.enumerated() {
            avatar.frame = CGRect(x: bounds.width - 150 + CGFloat(index) * 18, y: 8, width: 25, height: 25)
        }
    }
}
#endif
