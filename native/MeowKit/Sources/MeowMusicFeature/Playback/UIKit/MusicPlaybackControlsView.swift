#if os(iOS)
import UIKit
import Combine
import MeowUI

/// Shared control implementation, with one instance in each presentation.
@MainActor
final class MusicPlaybackControlsView: UIView {
    enum Layout { case compact, card, full }
    private struct FullLayoutMetrics {
        let actionsHeight: CGFloat
        let actionProgressGap: CGFloat
        let timeControlsGap: CGFloat
        let transportHeight: CGFloat
        let bottomSpace: CGFloat

        var fixedHeight: CGFloat {
            actionsHeight + actionProgressGap + 44 + 6 + 16 + timeControlsGap +
                transportHeight + bottomSpace
        }
    }

    var onPresent: ((MusicPlaybackDestination) -> Void)?
    var onMore: (() -> Void)?
    var onMode: (() -> Void)?
    var onSources: (() -> Void)?
    var onCollapse: (() -> Void)?
    private let content: MusicPlayerContentModel
    private let title = LoopingTextView()
    private let artist = UILabel(), lyricPreview = UILabel()
    private let elapsed = UILabel(), durationLabel = UILabel()
    private let play = UIButton(type: .system), queue = UIButton(type: .system)
    private let previous = UIButton(type: .system), nextButton = UIButton(type: .system)
    private let favorite = UIButton(type: .system), mode = UIButton(type: .system)
    private let more = UIButton(type: .system), quality = UIButton(type: .system)
    private let collapse = UIButton(type: .system)
    private let progress = MusicProgressSlider()
    private let playTrack = CAShapeLayer(), playProgress = CAShapeLayer()
    private var subscriptions: [AnyCancellable] = []
    private var scrubFence: MusicReferencePlayerFence?
    private var rendering = false
    private var layout: Layout
    private var fullViewportHeight: CGFloat?
    private var fullSafeHeight: CGFloat?
    private var titleFont = UIFont.systemFont(ofSize: 14, weight: .semibold)
    private var displayedTheme: PageTheme?
    var moreAnchor: UIView { more }
    var modeAnchor: UIView { mode }
    var miniLayout: MusicMiniPlayerLayout {
        .init(titleLineHeight: Double(titleFont.lineHeight), artistLineHeight: Double(artist.font.lineHeight),
              lyricLineHeight: Double(lyricPreview.font.lineHeight + 4))
    }
    var fullHeight: CGFloat {
        fullLayoutMetrics.fixedHeight + max(0, titleFont.lineHeight - 23) +
            max(0, artist.font.lineHeight - 18)
    }

    init(content: MusicPlayerContentModel, layout: Layout) {
        self.content = content
        self.layout = layout
        super.init(frame: .zero)
        for item in [title, artist, lyricPreview, elapsed, durationLabel, progress,
                     play, queue, previous, nextButton, favorite, mode, more, quality, collapse] {
            addSubview(item)
        }
        for ring in [playTrack, playProgress] {
            ring.fillColor = UIColor.clear.cgColor
            ring.lineCap = .round
            play.layer.insertSublayer(ring, at: 0)
        }
        playTrack.name = "play.progress.track"
        playProgress.name = "play.progress.played"
        playProgress.strokeEnd = 0
        title.isUserInteractionEnabled = false
        elapsed.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular)
        durationLabel.font = elapsed.font
        durationLabel.textAlignment = .right
        quality.titleLabel?.font = .systemFont(ofSize: 12)
        quality.accessibilityIdentifier = "music.player.source"
        quality.accessibilityLabel = "切换音源与音质"
        progress.accessibilityLabel = "播放进度"
        for (button, kind, label) in [
            (play, PageIcon.Kind.play, "播放或暂停"), (queue, .queue, "播放队列"),
            (previous, .miniPrevious, "上一首"), (nextButton, .miniNext, "下一首"),
            (favorite, .heart, "收藏"), (more, .more, "更多")
        ] {
            button.setImage(PageIcon.image(kind), for: .normal)
            button.accessibilityLabel = label
        }
        mode.accessibilityLabel = "播放模式"
        let prefix = layout == .full ? "music.player" : "music.miniPlayer"
        play.accessibilityIdentifier = "\(prefix).play"
        queue.accessibilityIdentifier = "\(prefix).queue"
        previous.accessibilityIdentifier = "\(prefix).previous"
        nextButton.accessibilityIdentifier = "\(prefix).next"
        favorite.accessibilityIdentifier = "\(prefix).favorite"
        progress.accessibilityIdentifier = "\(prefix).progress"
        if layout == .full {
            mode.accessibilityIdentifier = "music.player.mode"
            more.accessibilityIdentifier = "music.player.more"
            quality.accessibilityIdentifier = "music.player.quality"
        }
        collapse.setImage(PageIcon.image(.down, size: 22), for: .normal)
        collapse.accessibilityLabel = "收起播放器"
        collapse.accessibilityIdentifier = "music.miniPlayer.collapse"
        collapse.addAction(UIAction { [weak self] _ in self?.onCollapse?() }, for: .touchUpInside)
        play.addAction(UIAction { [weak content] _ in content?.togglePlayback() }, for: .touchUpInside)
        previous.addAction(UIAction { [weak content] _ in content?.navigate(.previous) }, for: .touchUpInside)
        nextButton.addAction(UIAction { [weak content] _ in content?.navigate(.next) }, for: .touchUpInside)
        favorite.addAction(UIAction { [weak content] _ in content?.toggleFavorite() }, for: .touchUpInside)
        queue.addAction(UIAction { [weak self] _ in
            self?.onPresent?(.queue)
        }, for: .touchUpInside)
        mode.addAction(UIAction { [weak self] _ in self?.onMode?() }, for: .touchUpInside)
        more.addAction(UIAction { [weak self] _ in self?.onMore?() }, for: .touchUpInside)
        quality.addAction(UIAction { [weak self] _ in self?.onSources?() }, for: .touchUpInside)
        progress.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.content.holdDock()
            self.scrubFence = self.content.fence
        }, for: .touchDown)
        progress.addAction(UIAction { [weak self] _ in
            guard let self, self.scrubFence?.admits(self.content.fence, permitted: self.rendering) == true else { return }
            self.elapsed.text = MusicMiniPlayerText.time(Double(self.progress.value))
        }, for: .valueChanged)
        progress.addAction(UIAction { [weak self] _ in self?.finishSeek() }, for: [.touchUpInside, .touchUpOutside])
        progress.addAction(UIAction { [weak self] _ in self?.cancelSeek() }, for: .touchCancel)
        subscriptions = [
            content.playbackChanges.sink { [weak self] in self?.updatePlayback() },
            content.accessChanges.sink { [weak self] in self?.updatePlayback() },
            content.metadataChanges.sink { [weak self] in self?.updateMetadata() },
            content.favoriteChanges.sink { [weak self] in self?.updateFavorite() },
            content.appearanceChanges.sink { [weak self] in self?.applyTheme() },
            content.clockChanges.sink { [weak self] in
                if self?.rendering == true { self?.updateClock() }
            }
        ]
        setLayout(layout, compatibleWith: traitCollection)
        applyTheme()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func setLayout(_ layout: Layout, compatibleWith traits: UITraitCollection) {
        self.layout = layout
        updateFonts(compatibleWith: traits)
        if layout == .compact { scrubFence = nil }
        for item in [artist, progress, elapsed, durationLabel, previous, nextButton, favorite] {
            setVisible(item, layout != .compact)
        }
        setVisible(lyricPreview, layout == .card)
        setVisible(collapse, layout == .card)
        for item in [mode, more, quality] { setVisible(item, layout == .full) }
        for (button, kind, size) in [
            (previous, layout == .compact ? PageIcon.Kind.miniPrevious : .previous,
             layout == .full ? CGFloat(25) : layout == .card ? 26 : 23),
            (nextButton, layout == .compact ? .miniNext : .next,
             layout == .full ? CGFloat(25) : layout == .card ? 26 : 23),
            (queue, PageIcon.Kind.queue, layout == .full ? CGFloat(24) : layout == .card ? 26 : 23)
        ] {
            button.setImage(PageIcon.image(kind, size: size), for: .normal)
        }
        applyControlStyle()
        updatePlayback()
        updateMetadata()
        setNeedsLayout()
    }

    func setRendering(_ rendering: Bool) {
        guard self.rendering != rendering else { return }
        self.rendering = rendering
        if !rendering { cancelSeek() }
        configureTitle()
        updateClock()
    }

    func setFullViewport(viewportHeight: CGFloat, safeHeight: CGFloat) {
        guard layout == .full else { return }
        let viewportHeight = max(0, viewportHeight)
        let safeHeight = max(0, safeHeight)
        guard fullViewportHeight != viewportHeight || fullSafeHeight != safeHeight else { return }
        fullViewportHeight = viewportHeight
        fullSafeHeight = safeHeight
        setNeedsLayout()
    }

    override func hitTest(_ point: CGPoint, with event: UIEvent?) -> UIView? {
        let hit = super.hitTest(point, with: event)
        return layout != .full && hit === self ? nil : hit
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let width = bounds.width, height = bounds.height
        if layout == .full {
            let metrics = fullLayoutMetrics
            let inset = width * 0.068
            let actionHeight = metrics.actionsHeight + max(0, titleFont.lineHeight - 23) +
                max(0, artist.font.lineHeight - 18)
            more.frame = CGRect(x: width - inset - 44, y: 0, width: 44, height: metrics.actionsHeight)
            favorite.frame = CGRect(x: more.frame.minX - 44, y: 0,
                                    width: 44, height: metrics.actionsHeight)
            let copyWidth = max(0, favorite.frame.minX - inset - 6)
            title.frame = CGRect(x: inset + 4, y: 0, width: max(0, copyWidth - 4),
                                 height: max(23, titleFont.lineHeight))
            let copyGap: CGFloat = metrics.actionsHeight == 44 ? 3 : 4
            artist.frame = CGRect(x: title.frame.minX, y: title.frame.maxY + copyGap,
                                  width: title.frame.width, height: max(18, artist.font.lineHeight))
            progress.frame = CGRect(x: inset, y: actionHeight + metrics.actionProgressGap,
                                    width: max(0, width - inset * 2), height: 44)
            let timeY = progress.frame.maxY + 6
            elapsed.frame = CGRect(x: inset + 4, y: timeY, width: 80, height: 16)
            durationLabel.frame = CGRect(x: width - inset - 84, y: timeY, width: 80, height: 16)
            quality.frame = CGRect(x: width / 2 - 70, y: timeY - 6, width: 140, height: 28)
            let transportY = timeY + 16 + metrics.timeControlsGap
            let cellWidth = width / 5
            for (index, button) in [mode, previous, play, nextButton, queue].enumerated() {
                button.frame = CGRect(x: CGFloat(index) * cellWidth, y: transportY,
                                      width: cellWidth, height: metrics.transportHeight)
            }
            layoutPlayRing()
            return
        }
        let compact = layout == .compact
        let titleX: CGFloat = compact ? 62 : 112
        let copyWidth = max(0, width - titleX - (compact ? 96 : 16))
        title.frame = CGRect(x: titleX, y: compact ? (height - titleFont.lineHeight) / 2 : 16,
                             width: compact ? copyWidth : max(0, copyWidth - 40), height: titleFont.lineHeight)
        artist.frame = CGRect(x: titleX, y: title.frame.maxY + 6,
                              width: title.frame.width, height: artist.font.lineHeight)
        lyricPreview.frame = CGRect(x: titleX, y: artist.frame.maxY + 8,
                                    width: copyWidth, height: lyricPreview.font.lineHeight + 4)
        collapse.frame = CGRect(x: width - 56, y: 6, width: 44, height: 44)
        let seekTop = CGFloat(miniLayout.seekTop)
        progress.frame = CGRect(x: 16, y: seekTop, width: max(0, width - 32), height: 28)
        elapsed.frame = CGRect(x: 16, y: seekTop + 28, width: 80, height: 18)
        durationLabel.frame = CGRect(x: width - 96, y: seekTop + 28, width: 80, height: 18)
        if compact {
            let frame = miniLayout.compactControlFrame(width: Double(width))
            play.frame = CGRect(x: frame.minX, y: frame.minY, width: 44, height: 44)
            queue.frame = CGRect(x: frame.minX + 44, y: frame.minY, width: 44, height: 44)
        } else {
            let cellWidth = max(0, width - 32) / 5
            for (index, button) in [favorite, previous, play, nextButton, queue].enumerated() {
                button.frame = CGRect(x: 16 + CGFloat(index) * cellWidth, y: CGFloat(miniLayout.controlsTop),
                                      width: cellWidth, height: 56)
            }
        }
        layoutPlayRing()
    }

    private var fullLayoutMetrics: FullLayoutMetrics {
        guard let fullViewportHeight, let fullSafeHeight else {
            return FullLayoutMetrics(actionsHeight: 48, actionProgressGap: 8,
                                     timeControlsGap: 20, transportHeight: 56, bottomSpace: 18)
        }
        if fullViewportHeight <= 480 {
            return FullLayoutMetrics(actionsHeight: 44, actionProgressGap: 0,
                                     timeControlsGap: 5, transportHeight: 48, bottomSpace: 8)
        }
        if fullViewportHeight <= 650 {
            return FullLayoutMetrics(actionsHeight: 48, actionProgressGap: 8,
                                     timeControlsGap: 10, transportHeight: 52, bottomSpace: 14)
        }
        return FullLayoutMetrics(actionsHeight: 48, actionProgressGap: fullSafeHeight * 0.0225,
                                 timeControlsGap: fullSafeHeight * 0.027,
                                 transportHeight: 56, bottomSpace: fullSafeHeight * 0.027)
    }

    private func setVisible(_ view: UIView, _ visible: Bool) {
        view.alpha = visible ? 1 : 0
        view.isUserInteractionEnabled = visible
        view.accessibilityElementsHidden = !visible
    }

    private func updatePlayback() {
        if scrubFence?.admits(content.fence, permitted: content.isAvailable) == false { cancelSeek() }
        let playing = content.model.phase == .playing
        let playKind: PageIcon.Kind = layout == .full
            ? (playing ? .playerPause : .playerPlay) : (playing ? .pause : .play)
        play.setImage(PageIcon.image(playKind, size: layout == .full ? 34 : layout == .card ? 28 : 18),
                      for: .normal)
        play.isEnabled = content.isAvailable
        previous.isEnabled = content.model.canPrevious
        nextButton.isEnabled = content.model.canNext
        queue.isEnabled = content.isAvailable
        progress.isEnabled = content.isAvailable && content.model.activeNativeAttemptID != nil
        quality.isEnabled = content.isAvailable && content.model.current != nil
        if layout == .full {
            let configuration = UIImage.SymbolConfiguration(pointSize: 24, weight: .regular)
            mode.setImage(UIImage(systemName: content.model.state.mode.symbol,
                                  withConfiguration: configuration), for: .normal)
        } else {
            mode.setImage(UIImage(systemName: content.model.state.mode.symbol), for: .normal)
        }
        mode.accessibilityValue = content.model.state.mode.title
        let qualityName = content.model.activeAudioDelivery.map { MusicQualityLabel.name($0.quality) }
        if layout == .full {
            quality.setTitle(qualityName, for: .normal)
            quality.alpha = qualityName == nil ? 0 : 1
            quality.accessibilityElementsHidden = qualityName == nil
            quality.isUserInteractionEnabled = qualityName != nil
        } else {
            quality.setTitle(qualityName ?? "音质未知", for: .normal)
        }
        quality.accessibilityValue = content.model.activeAudioIsCached.map { $0 ? "本机缓存" : "网络音源" } ?? "未选择音源"
        updateFavorite()
        updateClock()
    }

    private func updateFavorite() {
        let liked = content.model.current.map { content.favorites.isFavorite($0.track) } ?? false
        favorite.setImage(PageIcon.image(.heart, filled: liked, size: layout == .card ? 26 : 23), for: .normal)
        if layout == .full {
            favorite.tintColor = liked
                ? UIColor(red: 0.90, green: 0.36, blue: 0.41, alpha: 1)
                : UIColor(content.controlTheme.muted)
        }
        favorite.accessibilityValue = liked ? "已收藏" : "未收藏"
        favorite.isEnabled = content.isAvailable && content.model.current != nil &&
            content.favorites.loaded && !content.favorites.loading && !content.favorites.busy
    }

    private func updateMetadata() {
        configureTitle()
        artist.text = content.model.displayTrack?.artist ?? ""
        updateClock()
    }

    private func configureTitle() {
        let track = content.model.displayTrack
        let text = layout == .compact ? MusicMiniPlayerText.compactTitle(track) : track?.title ?? "暂无播放"
        let theme = content.controlTheme
        var colorRanges: [(NSRange, UIColor)] = []
        if layout == .compact, let track {
            let artist = track.artist.trimmingCharacters(in: .whitespacesAndNewlines)
            let suffix = " - " + artist
            if !artist.isEmpty, artist != "未知", artist != "未知歌手", text.hasSuffix(suffix) {
                let length = (suffix as NSString).length
                colorRanges.append((NSRange(location: (text as NSString).length - length, length: length),
                                    UIColor(theme.muted)))
            }
        }
        title.configure(text: text,
                        font: titleFont, color: UIColor(theme.ink),
                        colorRanges: colorRanges,
                        allowsMotion: !UIAccessibility.isReduceMotionEnabled, running: rendering,
                        gap: layout == .full ? 40 : 28, pointsPerSecond: 24, minimumDuration: 0)
    }

    func updateFonts(compatibleWith traits: UITraitCollection) {
        titleFont = UIFontMetrics(forTextStyle: .subheadline).scaledFont(
            for: .systemFont(ofSize: layout == .full ? 16 : layout == .card ? 16 : 14,
                             weight: layout == .full ? .medium : .semibold),
            compatibleWith: traits)
        artist.font = UIFontMetrics(forTextStyle: .caption1).scaledFont(
            for: .systemFont(ofSize: layout == .full ? 13 : 12), compatibleWith: traits)
        lyricPreview.font = UIFontMetrics(forTextStyle: .footnote).scaledFont(
            for: .systemFont(ofSize: 13), compatibleWith: traits)
        configureTitle()
        setNeedsLayout()
    }

    private func applyTheme() {
        let theme = content.controlTheme
        guard displayedTheme != theme else { return }
        displayedTheme = theme
        configureTitle()
        for label in [artist, lyricPreview, elapsed, durationLabel] { label.textColor = UIColor(theme.muted) }
        if layout == .full {
            for button in [play, previous, nextButton] { button.tintColor = UIColor(theme.ink) }
            for button in [queue, favorite, mode, more] {
                button.tintColor = UIColor(theme.muted)
            }
        } else {
            for button in [play, queue, previous, nextButton, favorite, mode, more, collapse] {
                button.tintColor = UIColor(theme.ink)
            }
        }
        quality.tintColor = UIColor(theme.muted)
        applyControlStyle()
        if layout == .full { updateFavorite() }
    }

    private func applyControlStyle() {
        let ink = UIColor(content.controlTheme.ink)
        let muted = UIColor(content.controlTheme.muted)
        let card = layout == .card
        progress.minimumTrackTintColor = layout == .full
            ? muted : (card ? ink : UIColor(content.controlTheme.accent))
        progress.tintColor = progress.minimumTrackTintColor
        progress.maximumTrackTintColor = muted.withAlphaComponent(layout == .full ? 0.18 : card ? 0.22 : 0.15)
        progress.thumbColor = layout == .full ? muted : (card ? ink : .white)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        playTrack.strokeColor = ink.withAlphaComponent(card ? 0.85 : 0.18).cgColor
        playProgress.strokeColor = ink.cgColor
        playTrack.lineWidth = card ? 1.5 : 2
        playProgress.lineWidth = 2
        playTrack.opacity = layout == .full ? 0 : 1
        playProgress.opacity = layout == .compact ? 1 : 0
        CATransaction.commit()
    }

    private func layoutPlayRing() {
        let diameter: CGFloat = layout == .compact ? 32 : 52
        let radius = min(diameter, min(play.bounds.width, play.bounds.height)) / 2 - 1
        let path = UIBezierPath(arcCenter: CGPoint(x: play.bounds.midX, y: play.bounds.midY),
                                radius: max(0, radius), startAngle: -.pi / 2, endAngle: .pi * 1.5,
                                clockwise: true).cgPath
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for ring in [playTrack, playProgress] { ring.frame = play.bounds; ring.path = path }
        CATransaction.commit()
    }

    private func updateClock() {
        let clock = content.clock
        if layout == .compact {
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            playProgress.strokeEnd = CGFloat(MusicMiniPlayerText.progress(position: clock.position, duration: clock.duration))
            CATransaction.commit()
            return
        }
        if scrubFence == nil {
            progress.maximumValue = Float(max(1, clock.duration ?? 0))
            progress.value = Float(max(0, min(clock.duration ?? 0, clock.position)))
            let time = MusicMiniPlayerText.time(clock.position)
            if elapsed.text != time { elapsed.text = time }
        }
        let duration = MusicMiniPlayerText.time(clock.duration)
        if durationLabel.text != duration { durationLabel.text = duration }
        if progress.buffers != clock.buffers { progress.buffers = clock.buffers }
        if progress.chorus != clock.chorus { progress.chorus = clock.chorus }
        if layout == .card {
            let text = content.model.currentLyricIndex.flatMap {
                content.model.lyricLines.indices.contains($0) ? content.model.lyricLines[$0].text : nil
            } ?? ""
            if lyricPreview.text != text { lyricPreview.text = text }
        }
    }

    private func cancelSeek() { scrubFence = nil; updateClock() }
    private func finishSeek() {
        if let scrubFence { _ = content.seek(to: Double(progress.value), expected: scrubFence) }
        cancelSeek()
    }
}
#endif
