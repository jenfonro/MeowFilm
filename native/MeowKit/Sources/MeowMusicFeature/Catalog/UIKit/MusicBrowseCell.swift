#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicBrowseCell: UICollectionViewCell, UIGestureRecognizerDelegate {
    static let reuseIdentifier = "MusicBrowseCell"
    private let artwork = MusicArtworkView(size: 0)
    private let textGradient = CAGradientLayer()
    private let title = UILabel(), subtitle = UILabel(), preview = UILabel()
    private let releaseType = UILabel()
    private let reason = UILabel()
    private let previews = UIStackView()
    private let rankingPreview = UIView()
    private let rankingRows = (0..<3).map { _ in MusicBrowseRankingPreviewRow() }
    private let play = UIButton(type: .system)
    private let regularPlayImage = PageIcon.image(.play, size: 17)
    private let rankingPlayImage = PageIcon.image(.play, size: 23)
    private var action: (() -> Void)?
    private var style = MusicPublicBrowseSection.Layout.list
    private var representedArtworkURL: URL?
    private var featurePalette = MusicBrowseFeaturePalette.fallback
    private var featurePaletteRole = MusicBrowseFeaturePalette.Role.standard
    private var rankingShowsFallback = false
    private var currentTheme = PageTheme()
    private var openAction: (() -> Void)?
    private lazy var pressFeedback = PressFeedback(view: contentView)
    private var embeddedTouchPressed = false
    private(set) var itemID = ""
    var bannerTrailingInset: CGFloat = 14 {
        didSet {
            guard oldValue != bannerTrailingInset else { return }
            setNeedsLayout()
        }
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        for row in rankingRows { rankingPreview.addSubview(row) }
        for item in [artwork, title, subtitle, preview, releaseType, reason, previews, rankingPreview, play] {
            contentView.addSubview(item)
        }
        textGradient.name = "music.browse.text-gradient"
        textGradient.colors = [
            UIColor.clear.cgColor,
            UIColor.clear.cgColor,
            UIColor.black.withAlphaComponent(160.0 / 255.0).cgColor,
        ]
        textGradient.locations = [0, 0.52, 1]
        textGradient.startPoint = CGPoint(x: 0.5, y: 0)
        textGradient.endPoint = CGPoint(x: 0.5, y: 1)
        textGradient.cornerRadius = artwork.layer.cornerRadius
        textGradient.masksToBounds = true
        textGradient.isHidden = true
        contentView.layer.insertSublayer(textGradient, above: artwork.layer)
        artwork.onImage = { [weak self] image, _ in self?.applyFeatureColor(from: image) }
        title.numberOfLines = 2; subtitle.numberOfLines = 1; preview.numberOfLines = 3
        title.font = .systemFont(ofSize: 13)
        subtitle.font = .systemFont(ofSize: 11)
        preview.font = .systemFont(ofSize: 13)
        releaseType.font = .systemFont(ofSize: 11)
        releaseType.textColor = .white
        releaseType.textAlignment = .center
        releaseType.backgroundColor = UIColor.black.withAlphaComponent(0.55)
        releaseType.layer.cornerRadius = 3
        releaseType.clipsToBounds = true
        releaseType.isAccessibilityElement = false
        releaseType.isHidden = true
        reason.font = .systemFont(ofSize: 10)
        previews.axis = .vertical; previews.spacing = 3; previews.distribution = .fillEqually
        play.setImage(regularPlayImage, for: .normal)
        play.backgroundColor = UIColor.white.withAlphaComponent(0.88)
        play.layer.cornerRadius = 16
        play.layer.shadowColor = UIColor.black.cgColor
        play.layer.shadowOffset = .zero
        play.layer.shadowRadius = 3
        play.addAction(UIAction { [weak self] _ in self?.action?() }, for: .touchUpInside)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var isHighlighted: Bool {
        didSet { applyPressFeedback() }
    }

    override func prepareForReuse() {
        super.prepareForReuse()
        resetPressFeedback()
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        guard window != nil else { resetPressFeedback(); return }
        var ancestor = superview
        while let view = ancestor {
            if let scroll = view as? UIScrollView {
                scroll.delaysContentTouches = false
                scroll.canCancelContentTouches = true
            }
            ancestor = view.superview
        }
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesBegan(touches, with: event)
        guard openAction != nil else { return }
        embeddedTouchPressed = true
        applyPressFeedback()
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesMoved(touches, with: event)
        guard openAction != nil, let touch = touches.first else { return }
        embeddedTouchPressed = bounds.contains(touch.location(in: self))
        applyPressFeedback()
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesEnded(touches, with: event)
        resetPressFeedback()
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesCancelled(touches, with: event)
        resetPressFeedback()
    }

    func resetPressFeedback() {
        embeddedTouchPressed = false
        isHighlighted = false
        pressFeedback.reset()
        for button in previews.arrangedSubviews.compactMap({ $0 as? MusicBrowsePreviewButton }) {
            button.resetPressFeedback()
        }
    }

    private func applyPressFeedback() {
        // Grouped song previews own their feedback so their rows never scale twice.
        if previews.arrangedSubviews.contains(where: { ($0 as? MusicBrowsePreviewButton)?.isHighlighted == true }) {
            pressFeedback.reset()
        } else {
            pressFeedback.setPressed(isHighlighted || embeddedTouchPressed)
        }
        if style == .songColumns || style == .vipSongs || style == .list { applyCardSurface() }
    }

    func updateArtwork(_ url: URL?) { loadArtwork(url) }

    func configure(item: MusicPublicBrowseItem, layout: MusicPublicBrowseSection.Layout,
                   theme: PageTheme, artworkURL: URL? = nil,
                   onPreview: ((MusicRemoteTrack) -> Void)? = nil, showsPlayButton: Bool = true,
                   onPlay: @escaping () -> Void) {
        if itemID != item.id || style != layout { resetPressFeedback() }
        style = layout; action = onPlay; itemID = item.id; currentTheme = theme
        artwork.contentMode = layout == .banner ? .scaleAspectFit : .scaleAspectFill
        artwork.layer.cornerRadius = layout == .banner || layout == .artistUpdates ? 0 : 8
        artwork.backgroundColor = layout == .artistUpdates ? .clear : UIColor.white.withAlphaComponent(0.18)
        featurePaletteRole = MusicBrowseFeaturePalette.Role(item)
        rankingShowsFallback = false
        if layout != .rankingStack { loadArtwork(artworkURL ?? item.artwork) }
        title.text = layout == .rankingStack || layout == .artistUpdates || item.coverLabel.isEmpty ?
            item.title : item.title + "\n" + item.coverLabel
        title.font = .systemFont(ofSize: layout == .rankingStack ? 20 :
                                    layout == .banner || layout == .artistUpdates ? 14 : 13)
        title.numberOfLines = layout == .rankingStack || layout == .artistUpdates ? 1 : 2
        subtitle.text = layout == .rankingStack ? item.ranking?.updateFrequency ?? "" : item.subtitle
        subtitle.font = .systemFont(ofSize: layout == .rankingStack ? 12 : 11)
        subtitle.textAlignment = layout == .rankingStack ? .right : .natural
        subtitle.isHidden = layout == .playlistRail || layout == .grid || layout == .rankingSmall || layout == .banner
        preview.text = item.ranking?.preview.enumerated().map { index, entry in
            let change = entry.rankingMovement?.symbol ?? ""
            return "\(entry.rank ?? index + 1). \(entry.title)" +
                (entry.artist.isEmpty ? "" : " − " + entry.artist) + (change.isEmpty ? "" : "  " + change)
        }.joined(separator: "\n")
            ?? item.previewSongs.map(\.title).joined(separator: "\n")
        if layout == .artistUpdates {
            if case .collection(let collection) = item.content, let count = collection.count {
                preview.text = "\(count) 首歌曲"
            } else { preview.text = nil }
        }
        preview.numberOfLines = layout == .artistUpdates ? 1 : 3
        preview.font = .systemFont(ofSize: layout == .artistUpdates ? 11 : 13)
        releaseType.text = item.coverLabel
        releaseType.isHidden = layout != .artistUpdates || item.coverLabel.isEmpty
        reason.text = item.recommendationReason
        reason.textColor = UIColor(theme.muted)
        if layout != .rankingStack, let frequency = item.ranking?.updateFrequency, !frequency.isEmpty {
            subtitle.text = [item.subtitle, frequency].filter { !$0.isEmpty }.joined(separator: " · ")
        }
        subtitle.isHidden = subtitle.isHidden || subtitle.text?.isEmpty != false
        previews.isHidden = !(layout == .rankedSongs || layout == .vipRankedSongs)
        var available = previews.arrangedSubviews.compactMap { $0 as? MusicBrowsePreviewButton }
        let tracks = previews.isHidden ? [] : Array(item.previewSongs.prefix(3))
        let buttons = tracks.map { track -> MusicBrowsePreviewButton in
            if let index = available.firstIndex(where: { $0.reference == track.ref }) {
                let button = available.remove(at: index)
                button.configure(track: track, theme: theme) { onPreview?(track) }
                return button
            }
            let button = MusicBrowsePreviewButton(track: track, theme: theme) { onPreview?(track) }
            button.onHighlightChanged = { [weak self] in self?.applyPressFeedback() }
            return button
        }
        for button in available {
            button.resetPressFeedback()
            previews.removeArrangedSubview(button); button.removeFromSuperview()
        }
        for (index, button) in buttons.enumerated() {
            if previews.arrangedSubviews.indices.contains(index), previews.arrangedSubviews[index] === button { continue }
            previews.removeArrangedSubview(button)
            previews.insertArrangedSubview(button, at: index)
        }
        let entries = item.ranking?.preview ?? item.previewSongs.enumerated().map { index, track in
            MusicPublicBrowseRanking.Entry(id: track.songId, title: track.title, artist: track.artist,
                                           artwork: track.artwork, rank: index + 1)
        }
        rankingShowsFallback = layout == .rankingStack && entries.isEmpty
        rankingPreview.isHidden = layout != .rankingStack || rankingShowsFallback
        if rankingShowsFallback { preview.text = item.title }
        for (index, row) in rankingRows.enumerated() {
            row.configure(entries.indices.contains(index) ? entries[index] : nil, fallbackRank: index + 1, theme: theme)
        }
        if layout == .rankingStack {
            let covers = entries.compactMap(\.artwork)
            let url = covers.first ?? item.ranking?.songArtwork ?? item.artwork
            loadArtwork(url)
        }
        title.textColor = UIColor(theme.ink); subtitle.textColor = UIColor(theme.muted)
        preview.textColor = UIColor(theme.ink)
        play.setImage(layout == .rankingStack ? rankingPlayImage : regularPlayImage, for: .normal)
        CATransaction.begin(); CATransaction.setDisableActions(true)
        if layout == .rankingStack || layout == .artistUpdates {
            play.tintColor = UIColor.white.withAlphaComponent(0.9)
            play.backgroundColor = .clear
            play.layer.cornerRadius = 0
            play.layer.shadowOpacity = layout == .rankingStack ? 0.35 : 0
        } else {
            play.tintColor = UIColor(theme.ink)
            play.backgroundColor = UIColor.white.withAlphaComponent(0.88)
            play.layer.cornerRadius = 16
            play.layer.shadowOpacity = 0
        }
        CATransaction.commit()
        switch item.content {
        case .track, .collection, .action, .daily: play.isHidden = !showsPlayButton
        default: play.isHidden = true
        }
        if layout == .banner { play.isHidden = true }
        accessibilityLabel = ([item.title, subtitle.isHidden ? "" : subtitle.text ?? ""] +
            (layout == .artistUpdates ? [preview.text ?? ""] : []) +
            (layout == .rankingStack ? entries.map(\.title) : []))
            .filter { !$0.isEmpty }.joined(separator: "，")
        play.accessibilityLabel = "播放" + item.title
        play.accessibilityIdentifier = "music.browse.play.\(item.id)"
        let overlaysArtwork = layout == .featureRail || layout == .banner
        let hasOverlayText = [title.text, subtitle.text].contains {
            $0?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false
        }
        CATransaction.begin(); CATransaction.setDisableActions(true)
        textGradient.isHidden = !(layout == .artistUpdates || (overlaysArtwork && hasOverlayText))
        CATransaction.commit()
        applyTextGradient()
        applyCardSurface()
        applyPressFeedback()
        setNeedsLayout()
    }

    private func loadArtwork(_ url: URL?) {
        if representedArtworkURL != url {
            representedArtworkURL = url
            featurePalette = MusicBrowseFeaturePalette.fallback
            applyTextGradient()
            applyCardSurface()
        }
        artwork.load(url)
    }

    private func applyFeatureColor(from image: UIImage) {
        guard let palette = MusicBrowseFeaturePalette.sample(image) else { return }
        featurePalette = palette
        applyTextGradient()
        applyCardSurface()
    }

    private func applyTextGradient() {
        let visible = !textGradient.isHidden
        let corners: CACornerMask = style == .featureRail && visible
            ? [.layerMinXMinYCorner, .layerMaxXMinYCorner]
            : [.layerMinXMinYCorner, .layerMaxXMinYCorner, .layerMinXMaxYCorner, .layerMaxXMaxYCorner]
        let colors: [CGColor], locations: [NSNumber]
        if style == .artistUpdates {
            colors = MusicBrowseFeaturePalette.artistGradientColors(featurePalette)
            locations = [0, 1]
        } else if style == .featureRail && visible {
            let color = MusicBrowseFeaturePalette.color(featurePalette, role: featurePaletteRole)
            colors = [
                color.withAlphaComponent(0).cgColor,
                color.withAlphaComponent(112.0 / 255.0).cgColor,
                color.cgColor,
            ]
            locations = [0.62, 0.82, 1]
        } else {
            colors = [
                UIColor.clear.cgColor,
                UIColor.clear.cgColor,
                UIColor.black.withAlphaComponent(160.0 / 255.0).cgColor,
            ]
            locations = [0, 0.52, 1]
        }
        CATransaction.begin(); CATransaction.setDisableActions(true)
        artwork.layer.maskedCorners = corners
        textGradient.maskedCorners = corners
        textGradient.cornerRadius = style == .artistUpdates ? 0 : 8
        textGradient.startPoint = style == .artistUpdates ? CGPoint(x: 0, y: 0.5) : CGPoint(x: 0.5, y: 0)
        textGradient.endPoint = style == .artistUpdates ? CGPoint(x: 1, y: 0.5) : CGPoint(x: 0.5, y: 1)
        textGradient.colors = colors
        textGradient.locations = locations
        CATransaction.commit()
    }

    private func applyCardSurface() {
        if style == .artistUpdates {
            contentView.backgroundColor = .clear
            contentView.layer.cornerRadius = 8
            contentView.clipsToBounds = true
            title.textColor = .white
            subtitle.textColor = UIColor.white.withAlphaComponent(0.85)
            preview.textColor = UIColor.white.withAlphaComponent(0.8)
        } else if style == .featureRail, !textGradient.isHidden {
            contentView.backgroundColor =
                MusicBrowseFeaturePalette.color(featurePalette, role: featurePaletteRole)
            contentView.layer.cornerRadius = artwork.layer.cornerRadius
            contentView.clipsToBounds = true
        } else if style == .rankingStack {
            contentView.backgroundColor = UIColor(currentTheme.surface)
            contentView.layer.cornerRadius = 18
            contentView.clipsToBounds = true
        } else {
            let row = style == .songColumns || style == .vipSongs || style == .list
            contentView.backgroundColor = row && (isHighlighted || embeddedTouchPressed)
                ? UIColor(currentTheme.chip) : .clear
            contentView.layer.cornerRadius = row ? 8 : 0
            contentView.clipsToBounds = false
        }
    }

    func setOpenAction(_ action: @escaping () -> Void) {
        if openAction == nil {
            let tap = UITapGestureRecognizer(target: self, action: #selector(open))
            tap.delegate = self
            contentView.addGestureRecognizer(tap)
        }
        openAction = action
    }

    @objc private func open() {
        resetPressFeedback()
        openAction?()
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldReceive touch: UITouch) -> Bool {
        var node = touch.view
        while let current = node, current !== contentView {
            if current is UIControl { return false }
            node = current.superview
        }
        return true
    }

    override func layoutSubviews() {
        pressFeedback.layoutAtRest { super.layoutSubviews() }
        let width = bounds.width, height = bounds.height
        let row = style == .songColumns || style == .vipSongs || style == .list
        if row {
            let actionWidth: CGFloat = play.isHidden ? 0 : 44
            artwork.frame = CGRect(x: 0, y: 8, width: 46, height: 46)
            title.frame = CGRect(x: 56, y: 10, width: max(0, width - 56 - actionWidth), height: 23)
            subtitle.frame = CGRect(x: 56, y: 35, width: max(0, width - 56 - actionWidth), height: 18)
            play.frame = play.isHidden ? .zero : CGRect(x: width - 36, y: 16, width: 32, height: 32)
            preview.frame = .zero
            reason.frame = CGRect(x: 56, y: 51, width: max(0, width - 56 - actionWidth), height: 14)
        } else if style == .rankingStack {
            let side: CGFloat = 88
            let bodyY: CGFloat = 52
            let frequencyWidth = subtitle.isHidden ? 0 :
                min(112, subtitle.sizeThatFits(CGSize(width: 112, height: 20)).width)
            title.frame = CGRect(x: 16, y: 10, width: max(0, width - 40 - frequencyWidth), height: 30)
            subtitle.frame = subtitle.isHidden ? .zero :
                CGRect(x: width - 16 - frequencyWidth, y: 15, width: frequencyWidth, height: 18)
            artwork.frame = CGRect(x: 16, y: bodyY, width: side, height: side)
            play.frame = CGRect(x: 16 + side / 2 - 22, y: bodyY + side / 2 - 22, width: 44, height: 44)
            if rankingShowsFallback {
                rankingPreview.frame = .zero
                preview.frame = CGRect(x: 122, y: bodyY, width: max(0, width - 138), height: 30)
            } else {
                preview.frame = .zero
                rankingPreview.frame = CGRect(x: 122, y: bodyY - 1,
                                              width: max(0, width - 138), height: side + 2)
                let rowHeight = rankingPreview.bounds.height / CGFloat(rankingRows.count)
                for (index, row) in rankingRows.enumerated() {
                    row.frame = CGRect(x: 0, y: CGFloat(index) * rowHeight,
                                       width: rankingPreview.bounds.width, height: rowHeight)
                }
            }
            reason.frame = .zero
            previews.frame = .zero
        } else if style == .artistUpdates {
            let side = height
            let textX = side + 14
            let textWidth = max(0, width - textX - 14)
            artwork.frame = CGRect(x: 0, y: 0, width: side, height: side)
            title.frame = CGRect(x: textX, y: 10, width: textWidth, height: 20)
            subtitle.frame = CGRect(x: textX, y: 32, width: max(0, width - textX - 44), height: 18)
            preview.frame = CGRect(x: textX, y: height - 28,
                                   width: max(0, width - textX - 44), height: 18)
            play.frame = play.isHidden ? .zero :
                CGRect(x: width - 44, y: height - 44, width: 44, height: 44)
            releaseType.frame = releaseType.isHidden ? .zero :
                CGRect(x: 6, y: 6, width: min(side - 12, releaseType.intrinsicContentSize.width + 8), height: 18)
            reason.frame = .zero
            previews.frame = .zero
        } else if style == .rankedSongs || style == .vipRankedSongs {
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
        } else if style == .banner {
            artwork.frame = CGRect(x: 0, y: 0, width: width, height: height)
            title.frame = CGRect(x: 14, y: height - 36,
                                 width: max(0, width - 14 - bannerTrailingInset), height: 22)
            title.textColor = .white
            subtitle.frame = .zero
            preview.frame = .zero
            play.frame = .zero
            reason.frame = .zero
            previews.frame = .zero
        } else {
            let featureFooter = style == .featureRail && !textGradient.isHidden
            let captionOnly = style == .playlistRail || style == .grid || style == .rankingSmall
            let artHeight = featureFooter || captionOnly ? min(width, height) :
                style == .featureRail ? height : min(width, max(0, height - 54))
            artwork.frame = CGRect(x: 0, y: 0, width: width, height: artHeight)
            title.frame = CGRect(x: 0, y: artHeight + 4, width: width, height: 34)
            subtitle.frame = captionOnly ? .zero :
                CGRect(x: 0, y: artHeight + 39, width: width, height: 14)
            if style == .featureRail {
                title.frame = CGRect(x: 10, y: height - 44, width: max(0, width - 50), height: 32)
                title.textColor = .white
                subtitle.frame = CGRect(x: 10, y: height - 17, width: max(0, width - 50), height: 14)
                subtitle.textColor = .white
            }
            preview.frame = .zero
            play.frame = CGRect(x: width - 38, y: (captionOnly ? artHeight : height) - 38,
                                width: 32, height: 32)
            reason.frame = .zero
        }
        CATransaction.begin(); CATransaction.setDisableActions(true)
        textGradient.frame = textGradient.isHidden ? .zero : style == .artistUpdates ?
            CGRect(x: artwork.frame.maxX, y: 0, width: max(0, width - artwork.frame.maxX), height: height) :
            artwork.frame
        CATransaction.commit()
        if previews.isHidden { artwork.isHidden = false }
    }
}

@MainActor
private final class MusicBrowseRankingPreviewRow: UIView {
    private let rank = UILabel()
    private let song = UILabel()
    private let change = UILabel()

    override init(frame: CGRect) {
        super.init(frame: frame)
        rank.font = .systemFont(ofSize: 13)
        rank.textAlignment = .center
        rank.adjustsFontSizeToFitWidth = true
        song.font = .systemFont(ofSize: 13)
        song.lineBreakMode = .byTruncatingTail
        change.font = .systemFont(ofSize: 11)
        change.textAlignment = .center
        addSubview(rank)
        addSubview(song)
        addSubview(change)
        isAccessibilityElement = true
        rank.isAccessibilityElement = false
        song.isAccessibilityElement = false
        change.isAccessibilityElement = false
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(_ entry: MusicPublicBrowseRanking.Entry?, fallbackRank: Int, theme: PageTheme) {
        guard let entry else {
            isHidden = true
            rank.text = nil
            song.attributedText = nil
            change.text = nil
            accessibilityIdentifier = nil
            accessibilityLabel = nil
            return
        }
        isHidden = false
        rank.text = String(entry.rank ?? fallbackRank)
        rank.textColor = UIColor(theme.ink)
        let copy = NSMutableAttributedString(string: entry.title, attributes: [
            .font: UIFont.systemFont(ofSize: 13),
            .foregroundColor: UIColor(theme.ink),
        ])
        if !entry.artist.isEmpty {
            copy.append(NSAttributedString(string: " − " + entry.artist, attributes: [
                .font: UIFont.systemFont(ofSize: 13),
                .foregroundColor: UIColor(theme.muted),
            ]))
        }
        song.attributedText = copy
        let movement = entry.rankingMovement
        let symbol = movement?.symbol ?? ""
        change.text = symbol
        change.textColor = UIColor(movement?.isRising == true ? theme.accent : theme.muted)
        let identity = entry.id.isEmpty ? "rank-\(fallbackRank)" : entry.id
        accessibilityIdentifier = "music.browse.ranking.\(identity)"
        accessibilityLabel = [rank.text ?? "", entry.title, entry.artist, movement?.label ?? ""]
            .filter { !$0.isEmpty }.joined(separator: "，")
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        rank.frame = CGRect(x: 0, y: 0, width: 22, height: bounds.height)
        change.frame = CGRect(x: max(0, bounds.width - 20), y: 0, width: 20, height: bounds.height)
        song.frame = CGRect(x: 30, y: 0, width: max(0, bounds.width - 54), height: bounds.height)
    }
}

fileprivate extension MusicPublicBrowseRanking.Entry {
    var rankingMovement: (symbol: String, label: String, isRising: Bool)? {
        guard let lastRank, let rank else { return nil }
        if lastRank == 0 { return ("新", "新上榜", true) }
        if lastRank == rank { return ("−", "排名持平", false) }
        return lastRank > rank ? ("▴", "排名上升", true) : ("▾", "排名下降", false)
    }
}

private enum MusicBrowseFeaturePalette {
    struct Value: Equatable {
        let red: Double
        let green: Double
        let blue: Double
    }

    enum Role {
        case preview
        case standard

        init(_ item: MusicPublicBrowseItem) {
            switch item.content {
            case .collection, .daily:
                self = .preview
            case .action(.daily):
                self = .preview
            default:
                self = .standard
            }
        }
    }

    static let fallback = Value(red: 105, green: 105, blue: 105)
    private static let entryTint = Value(red: 75, green: 56, blue: 50)

    static func sample(_ image: UIImage) -> Value? {
        guard let source = image.cgImage, let pixels = MusicCoverPaletteSampler.rgba(source) else { return nil }
        var red: UInt64 = 0, green: UInt64 = 0, blue: UInt64 = 0, weight: UInt64 = 0
        for offset in stride(from: 0, through: pixels.count - 4, by: 4) {
            let alpha = UInt64(pixels[offset + 3])
            red += UInt64(pixels[offset]) * alpha
            green += UInt64(pixels[offset + 1]) * alpha
            blue += UInt64(pixels[offset + 2]) * alpha
            weight += alpha
        }
        guard weight > 0 else { return nil }
        return Value(red: Double(red) / Double(weight),
                     green: Double(green) / Double(weight),
                     blue: Double(blue) / Double(weight))
    }

    static func color(_ source: Value, role: Role) -> UIColor {
        let value: Value
        switch role {
        case .preview:
            value = limitLuminance(source, maximum: 170)
        case .standard:
            value = standard(source)
        }
        return color(value)
    }

    static func artistGradientColors(_ source: Value) -> [CGColor] {
        let base = standard(source)
        let light = blend(base, with: Value(red: 255, green: 255, blue: 255), targetWeight: 0.25)
        return [base, light].map { color($0).cgColor }
    }

    private static func standard(_ source: Value) -> Value {
        limitLuminance(blend(source, with: entryTint, targetWeight: 0.65), maximum: 96)
    }

    private static func color(_ value: Value) -> UIColor {
        func channel(_ value: Double) -> CGFloat {
            CGFloat(min(255, max(0, Int(value.rounded(.toNearestOrAwayFromZero))))) / 255
        }
        return UIColor(red: channel(value.red), green: channel(value.green),
                       blue: channel(value.blue), alpha: 1)
    }

    private static func blend(_ source: Value, with target: Value, targetWeight: Double) -> Value {
        Value(red: source.red * (1 - targetWeight) + target.red * targetWeight,
              green: source.green * (1 - targetWeight) + target.green * targetWeight,
              blue: source.blue * (1 - targetWeight) + target.blue * targetWeight)
    }

    private static func limitLuminance(_ color: Value, maximum: Double) -> Value {
        let luminance = 0.2126 * color.red + 0.7152 * color.green + 0.0722 * color.blue
        guard luminance > maximum else { return color }
        let scale = maximum / luminance
        return Value(red: color.red * scale, green: color.green * scale, blue: color.blue * scale)
    }
}

@MainActor
private final class MusicBrowsePreviewButton: UIButton {
    private let cover = MusicArtworkView(size: 40)
    private let title = UILabel(), artist = UILabel()
    private let row = UIStackView()
    private lazy var pressFeedback = PressFeedback(view: row)
    private var pressedColor = UIColor.clear
    private var action: (() -> Void)?
    private(set) var reference: MusicReference
    var onHighlightChanged: (() -> Void)?

    init(track: MusicRemoteTrack, theme: PageTheme, action: @escaping () -> Void) {
        reference = track.ref
        super.init(frame: .zero)
        title.font = .systemFont(ofSize: 14); artist.font = .systemFont(ofSize: 11)
        let copy = UIStackView(arrangedSubviews: [title, artist]); copy.axis = .vertical; copy.spacing = 3
        row.addArrangedSubview(cover); row.addArrangedSubview(copy)
        row.spacing = 10; row.alignment = .center
        row.isUserInteractionEnabled = false
        layer.cornerRadius = 8
        addSubview(row); row.pinEdges(to: self, insets: UIEdgeInsets(top: 3, left: 0, bottom: 3, right: 8))
        addAction(UIAction { [weak self] _ in self?.action?() }, for: .touchUpInside)
        configure(track: track, theme: theme, action: action)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override var isHighlighted: Bool {
        didSet {
            pressFeedback.setPressed(isHighlighted)
            backgroundColor = isHighlighted ? pressedColor : .clear
            onHighlightChanged?()
        }
    }
    override func didMoveToWindow() {
        super.didMoveToWindow()
        if window == nil { resetPressFeedback() }
    }
    func resetPressFeedback() {
        isHighlighted = false
        pressFeedback.reset()
    }
    func configure(track: MusicRemoteTrack, theme: PageTheme, action: @escaping () -> Void) {
        reference = track.ref; self.action = action
        pressedColor = UIColor(theme.chip)
        backgroundColor = isHighlighted ? pressedColor : .clear
        cover.load(track.artwork)
        title.text = track.title; title.textColor = UIColor(theme.ink)
        artist.text = track.artist; artist.textColor = UIColor(theme.muted)
        accessibilityLabel = "播放" + track.title
    }
}

@MainActor
final class MusicBrowseHeader: UICollectionReusableView {
    static let reuseIdentifier = "MusicBrowseHeader"
    private let title = UILabel()
    private let play = UIButton(type: .system), choices = UIButton(type: .system)
    private let channelButton = UIButton(type: .system)
    private let channelArrow = UIImageView(image: UIImage(systemName: "chevron.right",
        withConfiguration: UIImage.SymbolConfiguration(pointSize: 11, weight: .semibold)))
    private let tabs = UIStackView()
    private let avatars = (0..<3).map { _ in MusicArtworkView(size: 0) }
    private let errorLabel = UILabel(), retry = UIButton(type: .system)
    private var playAction: (() -> Void)?
    private var channelAction: (() -> Void)?
    private var headingHeight: CGFloat = 0
    private var leadingAvatars = false

    static func height(for section: MusicPublicBrowseSection, showsSectionPlayback: Bool = true) -> CGFloat {
        return headingHeight(for: section, showsSectionPlayback: showsSectionPlayback) + (section.tabs.isEmpty ? 0 : 36)
    }

    private static func headingHeight(for section: MusicPublicBrowseSection, showsSectionPlayback: Bool) -> CGFloat {
        let hasPlayback = showsSectionPlayback && section.items.contains {
            if case .track = $0.content { return true }; return false
        }
        return !section.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
            hasPlayback || !section.filters.isEmpty || section.channel != nil || !section.avatars.isEmpty ? 44 : 0
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        title.font = .systemFont(ofSize: 16)
        title.numberOfLines = 1
        play.setImage(PageIcon.image(.play), for: .normal)
        play.accessibilityLabel = "播放全部"
        play.addAction(UIAction { [weak self] _ in self?.playAction?() }, for: .touchUpInside)
        choices.setImage(UIImage(systemName: "chevron.down"), for: .normal)
        choices.showsMenuAsPrimaryAction = true
        choices.accessibilityLabel = "选择栏目"
        channelButton.addAction(UIAction { [weak self] _ in self?.channelAction?() }, for: .touchUpInside)
        channelArrow.contentMode = .scaleAspectFit
        tabs.axis = .horizontal; tabs.spacing = 9; tabs.distribution = .fillEqually
        errorLabel.numberOfLines = 0; errorLabel.font = .systemFont(ofSize: 13)
        retry.setTitle("重试", for: .normal)
        for item in [channelButton, title, channelArrow, play, choices, tabs, errorLabel, retry] + avatars { addSubview(item) }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(section: MusicPublicBrowseSection,
                   theme: PageTheme, showsSectionPlayback: Bool = true, onPlay: @escaping () -> Void,
                   onChannel: @escaping () -> Void, onChoice: @escaping (String) -> Void,
                   onRetry: @escaping () -> Void, error: String?) {
        headingHeight = Self.headingHeight(for: section, showsSectionPlayback: showsSectionPlayback)
        leadingAvatars = section.layout == .artistUpdates
        title.text = section.title
        title.textColor = UIColor(theme.ink)
        playAction = onPlay
        play.isHidden = !showsSectionPlayback || !section.items.contains {
            if case .track = $0.content { return true }; return false
        }
        channelAction = onChannel
        channelButton.isHidden = section.channel == nil
        channelButton.accessibilityLabel = section.title
        channelButton.accessibilityIdentifier = "music.browse.channel.\(section.id)"
        channelArrow.isHidden = channelButton.isHidden
        channelArrow.tintColor = UIColor(theme.ink)
        title.isAccessibilityElement = channelButton.isHidden
        for (index, avatar) in avatars.enumerated() {
            avatar.isHidden = index >= section.avatars.count
            if !avatar.isHidden { avatar.load(section.avatars[index]); avatar.layer.cornerRadius = 12.5 }
        }
        errorLabel.text = error; errorLabel.textColor = UIColor(theme.muted)
        retry.isHidden = error == nil; retry.tintColor = UIColor(theme.ink)
        retry.removeAction(identifiedBy: UIAction.Identifier("retry"), for: .touchUpInside)
        retry.addAction(UIAction(identifier: UIAction.Identifier("retry")) { _ in onRetry() }, for: .touchUpInside)
        play.tintColor = UIColor(theme.ink); choices.tintColor = UIColor(theme.ink)
        let actions: [UIMenuElement] = section.filters.map { filter in
            UIAction(title: filter.title, state: filter.id == section.selectedFilter ? .on : .off) {
                _ in onChoice(filter.id)
            }
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
        let choiceWidth: CGFloat = choices.isHidden ? 0 : 44
        let playWidth: CGFloat = play.isHidden ? 0 : 44
        let avatarCount = avatars.filter { !$0.isHidden }.count
        let avatarWidth: CGFloat = avatarCount == 0 ? 0 : 25 + CGFloat(avatarCount - 1) * 18 + 8
        let titleArea = max(0, bounds.width - choiceWidth - playWidth - avatarWidth)
        let height = max(0, headingHeight - 6)
        let maximum = max(0, titleArea - (channelArrow.isHidden ? 0 : 18))
        let width = channelArrow.isHidden ? maximum :
            min(maximum, title.sizeThatFits(CGSize(width: maximum, height: height)).width)
        title.frame = CGRect(x: leadingAvatars ? avatarWidth : 0, y: 2, width: width, height: height)
        channelArrow.frame = channelArrow.isHidden ? .zero :
            CGRect(x: title.frame.maxX + 6, y: title.frame.midY - 8, width: 12, height: 16)
        channelButton.frame = CGRect(x: 0, y: 0, width: max(0, bounds.width - choiceWidth - playWidth), height: headingHeight)
        play.frame = CGRect(x: bounds.width - choiceWidth - playWidth, y: 0, width: playWidth, height: headingHeight)
        choices.frame = CGRect(x: bounds.width - choiceWidth, y: 0, width: choiceWidth, height: headingHeight)
        tabs.frame = CGRect(x: 0, y: headingHeight, width: bounds.width,
                            height: max(0, bounds.height - headingHeight))
        for (index, avatar) in avatars.enumerated() {
            avatar.frame = CGRect(x: (leadingAvatars ? 0 : titleArea + 8) + CGFloat(index) * 18,
                                  y: leadingAvatars ? title.frame.midY - 12.5 : 8, width: 25, height: 25)
        }
        if errorLabel.text != nil {
            let y = bounds.height - 64
            errorLabel.frame = CGRect(x: 0, y: y, width: max(0, bounds.width - 84), height: 60)
            retry.frame = CGRect(x: bounds.width - 80, y: y + 8, width: 76, height: 44)
            tabs.frame.size.height = max(0, y - headingHeight)
        } else { errorLabel.frame = .zero }
    }
}
#endif
