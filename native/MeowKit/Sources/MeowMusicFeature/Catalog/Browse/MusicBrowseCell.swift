#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicBrowseCell: UICollectionViewCell, UIGestureRecognizerDelegate {
    static let reuseIdentifier = "MusicBrowseCell"
    enum PlaybackLoading {
        case card
        case preview(MusicReference)
    }
    private let artwork = MusicArtworkView(size: 0)
    private let textGradient = CAGradientLayer()
    private let title = UILabel(), subtitle = UILabel(), preview = UILabel()
    private let releaseType = UILabel()
    private let previews = UIStackView()
    private let previewButtons = (0..<3).map { _ in MusicBrowsePreviewButton(frame: .zero) }
    private let rankingPreview = UIView()
    private let rankingRows = (0..<3).map { _ in MusicBrowseRankingPreviewRow() }
    private let play = UIButton(type: .system)
    private let spinner = UIActivityIndicatorView(style: .medium)
    private let regularPlayImage = PageIcon.image(.play, size: 17)
    private let rankingPlayImage = PageIcon.image(.play, size: 23)
    private var action: (() -> Void)?
    private var style = MusicPublicBrowseSection.Layout.list
    private var representedArtworkURL: URL?
    private var representedTrack: MusicRemoteTrack?
    private weak var artworkImages: MusicArtworkImages?
    private var featurePalette = MusicBrowseSampledPalette.fallback
    private var featurePaletteRole = MusicBrowseSampledPalette.Role.standard
    private var paletteTask: Task<Void, Never>?
    private var paletteGeneration: UInt64 = 0
    private var rankingShowsFallback = false
    private var currentTheme = PageTheme()
    private var openAction: (() -> Void)?
    private lazy var pressFeedback = PressFeedback(view: contentView)
    private var embeddedTouchPressed = false
    private var playbackLoading: PlaybackLoading?
    private var displayVisible = true
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
        for item in [artwork, title, subtitle, preview, releaseType, previews, rankingPreview, play] {
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
        previews.axis = .vertical; previews.spacing = 3; previews.distribution = .fillEqually
        for button in previewButtons {
            button.onHighlightChanged = { [weak self] in self?.applyPressFeedback() }
            previews.addArrangedSubview(button)
        }
        play.setImage(regularPlayImage, for: .normal)
        play.backgroundColor = UIColor.white.withAlphaComponent(0.88)
        play.layer.cornerRadius = 16
        play.layer.shadowColor = UIColor.black.cgColor
        play.layer.shadowOffset = .zero
        play.layer.shadowRadius = 3
        play.addAction(UIAction { [weak self] _ in self?.action?() }, for: .touchUpInside)
        spinner.isUserInteractionEnabled = false
        spinner.isAccessibilityElement = false
        play.addSubview(spinner)
        spinner.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            spinner.centerXAnchor.constraint(equalTo: play.centerXAnchor),
            spinner.centerYAnchor.constraint(equalTo: play.centerYAnchor),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var isHighlighted: Bool {
        didSet { applyPressFeedback() }
    }

    override var isHidden: Bool {
        didSet { updatePlaybackLoading() }
    }

    override func prepareForReuse() {
        super.prepareForReuse()
        paletteTask?.cancel()
        paletteTask = nil
        paletteGeneration &+= 1
        artwork.cancelPendingArtworkLoad()
        for button in previewButtons {
            button.cancelPendingArtworkLoad()
        }
        resetPressFeedback()
        setPlaybackLoading(nil, visible: false)
    }

    deinit {
        paletteTask?.cancel()
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        updatePlaybackLoading()
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
        for button in previewButtons {
            button.resetPressFeedback()
        }
    }

    func setPlaybackLoading(_ loading: PlaybackLoading?, visible: Bool) {
        playbackLoading = loading
        setDisplayVisible(visible)
    }

    func setDisplayVisible(_ visible: Bool) {
        let changed = displayVisible != visible
        displayVisible = visible
        updatePlaybackLoading()
        guard changed else { return }
        if !visible {
            paletteTask?.cancel()
            paletteTask = nil
            paletteGeneration &+= 1
            artwork.cancelPendingArtworkLoad()
            for button in previewButtons {
                button.cancelPendingArtworkLoad()
            }
        } else {
            artwork.resumePendingLoadIfNeeded()
            if let image = artwork.image {
                applyFeatureColor(from: image)
            }
            for button in previewButtons where !button.isHidden {
                button.resumePendingArtworkLoad()
            }
        }
    }

    private func updatePlaybackLoading() {
        let visible = displayVisible && window != nil && !isHidden
        let loading: Bool
        if case .card? = playbackLoading { loading = visible && !play.isHidden }
        else { loading = false }
        play.setImage(loading ? nil : style == .rankingStack ? rankingPlayImage : regularPlayImage, for: .normal)
        play.isEnabled = !loading
        play.accessibilityValue = loading ? "加载中" : nil
        spinner.color = play.tintColor
        if loading { if !spinner.isAnimating { spinner.startAnimating() } }
        else { spinner.stopAnimating() }
        for button in previewButtons {
            if case .preview(let reference)? = playbackLoading {
                button.setPlaybackLoading(visible && !previews.isHidden && !button.isHidden && reference == button.reference)
            } else { button.setPlaybackLoading(false) }
        }
    }

    private func applyPressFeedback() {
        // Grouped song previews own their feedback so their rows never scale twice.
        if previewButtons.contains(where: { !$0.isHidden && $0.isHighlighted }) {
            pressFeedback.reset()
        } else {
            pressFeedback.setPressed(isHighlighted || embeddedTouchPressed)
        }
        if style == .songColumns || style == .vipSongs || style == .list { applyCardSurface() }
    }

    func updateArtwork(_ url: URL?, images: MusicArtworkImages? = nil) {
        if let images { artworkImages = images }
        representedTrack = nil
        loadArtwork(url)
    }

    func configure(item: MusicPublicBrowseItem, layout: MusicPublicBrowseSection.Layout,
                   theme: PageTheme, images: MusicArtworkImages, artworkURL: URL? = nil,
                   onPreview: ((MusicRemoteTrack) -> Void)? = nil, showsPlayButton: Bool = true,
                   onPlay: @escaping () -> Void) {
        artworkImages = images
        if itemID != item.id || style != layout { resetPressFeedback(); playbackLoading = nil }
        style = layout; action = onPlay; itemID = item.id; currentTheme = theme
        artwork.contentMode = layout == .banner ? .scaleAspectFit : .scaleAspectFill
        artwork.layer.cornerRadius = layout == .banner || layout == .artistUpdates ? 0 : 8
        artwork.backgroundColor = layout == .artistUpdates ? .clear : UIColor.white.withAlphaComponent(0.18)
        featurePaletteRole = MusicBrowseSampledPalette.Role(item)
        if item.kind == nil, case .track(let track) = item.content { representedTrack = track }
        else { representedTrack = nil }
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
        subtitle.isHidden = layout == .playlistRail || [.grid, .artistGrid, .channelGrid].contains(layout) || layout == .rankingSmall || layout == .banner
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
        if layout != .rankingStack, let frequency = item.ranking?.updateFrequency, !frequency.isEmpty {
            subtitle.text = [item.subtitle, frequency].filter { !$0.isEmpty }.joined(separator: " · ")
        }
        subtitle.isHidden = subtitle.isHidden || subtitle.text?.isEmpty != false
        previews.isHidden = !(layout == .rankedSongs || layout == .vipRankedSongs)
        let tracks = previews.isHidden ? [] : Array(item.previewSongs.prefix(previewButtons.count))
        for (index, button) in previewButtons.enumerated() {
            guard tracks.indices.contains(index) else {
                button.resetForUnusedSlot()
                continue
            }
            let track = tracks[index]
            button.configure(track: track, theme: theme, images: images) { onPreview?(track) }
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
            representedTrack = nil
            loadArtwork(artworkURL ?? item.artwork)
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
        spinner.accessibilityIdentifier = "music.browse.loading.\(item.id)"
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
        updatePlaybackLoading()
        setNeedsLayout()
    }

    private func loadArtwork(_ url: URL?) {
        if representedArtworkURL != url {
            representedArtworkURL = url
            paletteTask?.cancel()
            paletteTask = nil
            paletteGeneration &+= 1
            if let key = url?.absoluteString, let cached = artworkImages?.effects.cachedBrowsePalette(for: key) {
                featurePalette = cached
            } else {
                featurePalette = MusicBrowseSampledPalette.fallback
            }
            applyTextGradient()
            applyCardSurface()
        }
        if let track = representedTrack {
            artwork.load(.init(ref: track.ref, songId: track.songId, title: track.title,
                artist: track.artist, album: track.album, duration: track.duration, sources: track.sources,
                artwork: url, artistNames: track.artistNames, sourcePosition: track.sourcePosition), images: artworkImages)
        } else { artwork.load(url, images: artworkImages) }
        if let image = artwork.image { applyFeatureColor(from: image) }
    }

    private func applyFeatureColor(from image: UIImage) {
        guard style == .featureRail || style == .artistUpdates else { return }
        guard let artworkImages else { return }
        let key = representedArtworkURL?.absoluteString ?? String(ObjectIdentifier(image).hashValue)
        if let cached = artworkImages.effects.cachedBrowsePalette(for: key) {
            if featurePalette != cached {
                featurePalette = cached
                applyTextGradient()
                applyCardSurface()
            }
            return
        }
        paletteTask?.cancel()
        paletteGeneration &+= 1
        let generation = paletteGeneration
        let effects = artworkImages.effects
        paletteTask = Task { [weak self] in
            guard let sampled = await effects.sampleBrowsePalette(for: image, key: key) else { return }
            guard let self, !Task.isCancelled, self.paletteGeneration == generation else { return }
            self.paletteTask = nil
            self.featurePalette = sampled
            self.applyTextGradient()
            self.applyCardSurface()
        }
    }

    private func applyTextGradient() {
        let visible = !textGradient.isHidden
        let corners: CACornerMask = style == .featureRail && visible
            ? [.layerMinXMinYCorner, .layerMaxXMinYCorner]
            : [.layerMinXMinYCorner, .layerMaxXMinYCorner, .layerMinXMaxYCorner, .layerMaxXMaxYCorner]
        let colors: [CGColor], locations: [NSNumber]
        if style == .artistUpdates {
            colors = featurePalette.artistGradientColors()
            locations = [0, 1]
        } else if style == .featureRail && visible {
            let color = featurePalette.color(role: featurePaletteRole)
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
                featurePalette.color(role: featurePaletteRole)
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
            previews.frame = .zero
        } else if style == .rankedSongs || style == .vipRankedSongs {
            artwork.frame = CGRect(x: 0, y: 4, width: 94, height: 94)
            title.frame = CGRect(x: 106, y: 8, width: max(0, width - 108), height: 36)
            subtitle.frame = CGRect(x: 106, y: 46, width: max(0, width - 108), height: 18)
            preview.frame = CGRect(x: 0, y: 104, width: width - 38, height: max(0, height - 104))
            play.frame = CGRect(x: width - 34, y: height - 34, width: 32, height: 32)
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
            previews.frame = .zero
        } else {
            let featureFooter = style == .featureRail && !textGradient.isHidden
            let captionOnly = style == .playlistRail || style == .grid || style == .artistGrid ||
                style == .channelGrid || style == .rankingSmall
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
        }
        CATransaction.begin(); CATransaction.setDisableActions(true)
        textGradient.frame = textGradient.isHidden ? .zero : style == .artistUpdates ?
            CGRect(x: artwork.frame.maxX, y: 0, width: max(0, width - artwork.frame.maxX), height: height) :
            artwork.frame
        CATransaction.commit()
        if previews.isHidden { artwork.isHidden = false }
    }
}
#endif
