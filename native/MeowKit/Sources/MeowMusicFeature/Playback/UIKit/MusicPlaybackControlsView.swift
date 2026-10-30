#if os(iOS)
import UIKit
import Combine
import MeowUI

/// Shared control implementation, with one instance in each presentation.
@MainActor
final class MusicPlaybackControlsView: UIView {
    enum Layout { case compact, card, full }
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
    private var titleFont = UIFont.systemFont(ofSize: 14, weight: .semibold)
    private var displayedTheme: PageTheme?
    var moreAnchor: UIView { more }
    var modeAnchor: UIView { mode }
    var miniLayout: MusicMiniPlayerLayout {
        .init(titleLineHeight: Double(titleFont.lineHeight), artistLineHeight: Double(artist.font.lineHeight),
              lyricLineHeight: Double(lyricPreview.font.lineHeight + 4))
    }
    var fullHeight: CGFloat { 221 + max(0, titleFont.lineHeight - 24) + max(0, artist.font.lineHeight - 20) }

    init(content: MusicPlayerContentModel, layout: Layout) {
        self.content = content
        self.layout = layout
        super.init(frame: .zero)
        for item in [title, artist, lyricPreview, elapsed, durationLabel, progress,
                     play, queue, previous, nextButton, favorite, mode, more, quality, collapse] { addSubview(item) }
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
        for (button, kind) in [
            (previous, layout == .card ? PageIcon.Kind.previous : .miniPrevious),
            (nextButton, layout == .card ? .next : .miniNext), (queue, .queue)
        ] {
            button.setImage(PageIcon.image(kind, size: layout == .card ? 26 : 23), for: .normal)
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

    override func hitTest(_ point: CGPoint, with event: UIEvent?) -> UIView? {
        let hit = super.hitTest(point, with: event)
        return layout != .full && hit === self ? nil : hit
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let width = bounds.width, height = bounds.height
        if layout == .full {
            title.frame = CGRect(x: 28, y: 0, width: max(0, width - 56), height: titleFont.lineHeight + 4)
            artist.frame = CGRect(x: 28, y: title.frame.maxY + 4,
                                  width: max(0, width - 56), height: max(20, artist.font.lineHeight))
            let controlsY = height - 116
            progress.frame = CGRect(x: 24, y: controlsY - 44, width: max(0, width - 48), height: 30)
            elapsed.frame = CGRect(x: 28, y: controlsY - 17, width: 80, height: 18)
            durationLabel.frame = CGRect(x: width - 108, y: controlsY - 17, width: 80, height: 18)
            quality.frame = CGRect(x: width / 2 - 70, y: controlsY - 28, width: 140, height: 36)
            for (index, button) in [previous, play, nextButton].enumerated() {
                button.frame = CGRect(x: width / 2 - 126 + CGFloat(index) * 84, y: controlsY + 8, width: 84, height: 62)
            }
            for (index, button) in [mode, favorite, more, queue].enumerated() {
                button.frame = CGRect(x: 24 + CGFloat(index) * (width - 48) / 4, y: height - 48,
                                      width: (width - 48) / 4, height: 44)
            }
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

    private func setVisible(_ view: UIView, _ visible: Bool) {
        view.alpha = visible ? 1 : 0
        view.isUserInteractionEnabled = visible
        view.accessibilityElementsHidden = !visible
    }

    private func updatePlayback() {
        if scrubFence?.admits(content.fence, permitted: content.isAvailable) == false { cancelSeek() }
        play.setImage(PageIcon.image(content.model.phase == .playing ? .pause : .play,
                                     size: layout == .full ? 38 : layout == .card ? 28 : 18), for: .normal)
        play.isEnabled = content.isAvailable
        previous.isEnabled = content.model.canPrevious
        nextButton.isEnabled = content.model.canNext
        queue.isEnabled = content.isAvailable
        progress.isEnabled = content.isAvailable && content.model.activeNativeAttemptID != nil
        quality.isEnabled = content.isAvailable && content.model.current != nil
        mode.setImage(UIImage(systemName: content.model.state.mode.symbol), for: .normal)
        mode.accessibilityValue = content.model.state.mode.title
        quality.setTitle(content.model.activeAudioDelivery.map { MusicQualityLabel.name($0.quality) } ?? "音质未知",
                         for: .normal)
        quality.accessibilityValue = content.model.activeAudioIsCached.map { $0 ? "本机缓存" : "网络音源" } ?? "未选择音源"
        updateFavorite()
        updateClock()
    }

    private func updateFavorite() {
        let liked = content.model.current.map { content.favorites.isFavorite($0.track) } ?? false
        favorite.setImage(PageIcon.image(.heart, filled: liked, size: layout == .card ? 26 : 23), for: .normal)
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
        title.configure(text: layout == .compact ? MusicMiniPlayerText.compactTitle(content.model.displayTrack)
                            : content.model.displayTrack?.title ?? "暂无播放",
                        font: titleFont, color: UIColor(content.controlTheme.ink),
                        allowsMotion: !UIAccessibility.isReduceMotionEnabled, running: rendering,
                        gap: 28, pointsPerSecond: 24, minimumDuration: 0)
    }

    func updateFonts(compatibleWith traits: UITraitCollection) {
        titleFont = UIFontMetrics(forTextStyle: .subheadline).scaledFont(
            for: .systemFont(ofSize: layout == .full ? 20 : layout == .card ? 16 : 14, weight: .semibold),
            compatibleWith: traits)
        artist.font = UIFontMetrics(forTextStyle: .caption1).scaledFont(
            for: .systemFont(ofSize: 12), compatibleWith: traits)
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
        for button in [play, queue, previous, nextButton, favorite, mode, more, collapse] { button.tintColor = UIColor(theme.ink) }
        quality.tintColor = UIColor(theme.muted)
        applyControlStyle()
    }

    private func applyControlStyle() {
        let ink = UIColor(content.controlTheme.ink)
        let card = layout == .card
        progress.minimumTrackTintColor = card ? ink : UIColor(content.controlTheme.accent)
        progress.tintColor = progress.minimumTrackTintColor
        progress.maximumTrackTintColor = UIColor(content.controlTheme.muted).withAlphaComponent(card ? 0.22 : 0.15)
        progress.thumbColor = card ? ink : .white
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
