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
    var onTitleTap: (() -> Void)? { didSet { title.isUserInteractionEnabled = onTitleTap != nil } }
    private let content: MusicPlayerContentModel
    private let title = LoopingTextView()
    private let artist = UILabel(), lyricPreview = UILabel()
    private let elapsed = UILabel(), durationLabel = UILabel()
    private let play = UIButton(type: .system), queue = UIButton(type: .system)
    private let previous = UIButton(type: .system), nextButton = UIButton(type: .system)
    private let favorite = UIButton(type: .system), mode = UIButton(type: .system)
    private let more = UIButton(type: .system), quality = UIButton(type: .system)
    private let progress = MusicProgressSlider()
    private var subscriptions: [AnyCancellable] = []
    private var scrubFence: MusicReferencePlayerFence?
    private var rendering = false
    private var layout: Layout
    private var displayedTheme: PageTheme?
    var moreAnchor: UIView { more }
    var modeAnchor: UIView { mode }
    var miniLayout: MusicMiniPlayerLayout { .init(lineHeight: Double(titleFont.lineHeight)) }
    var fullHeight: CGFloat { 221 + max(0, titleFont.lineHeight - 24) + max(0, artist.font.lineHeight - 20) }
    private var titleFont: UIFont {
        UIFontMetrics(forTextStyle: .subheadline).scaledFont(
            for: .systemFont(ofSize: layout == .full ? 20 : 14, weight: .semibold),
            compatibleWith: traitCollection)
    }

    init(content: MusicPlayerContentModel, layout: Layout) {
        self.content = content
        self.layout = layout
        super.init(frame: .zero)
        for item in [title, artist, lyricPreview, elapsed, durationLabel, progress,
                     play, queue, previous, nextButton, favorite, mode, more, quality] { addSubview(item) }
        artist.font = UIFontMetrics(forTextStyle: .caption1).scaledFont(for: .systemFont(ofSize: 12))
        lyricPreview.font = UIFontMetrics(forTextStyle: .footnote).scaledFont(for: .systemFont(ofSize: 13))
        artist.adjustsFontForContentSizeCategory = true
        lyricPreview.adjustsFontForContentSizeCategory = true
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
        play.addAction(UIAction { [weak content] _ in content?.togglePlayback() }, for: .touchUpInside)
        previous.addAction(UIAction { [weak content] _ in content?.navigate(.previous) }, for: .touchUpInside)
        nextButton.addAction(UIAction { [weak content] _ in content?.navigate(.next) }, for: .touchUpInside)
        favorite.addAction(UIAction { [weak content] _ in content?.toggleFavorite() }, for: .touchUpInside)
        queue.addAction(UIAction { [weak self] _ in
            self?.content.holdDock()
            self?.onPresent?(.queue)
        }, for: .touchUpInside)
        mode.addAction(UIAction { [weak self] _ in self?.onMode?() }, for: .touchUpInside)
        more.addAction(UIAction { [weak self] _ in self?.onMore?() }, for: .touchUpInside)
        quality.addAction(UIAction { [weak self] _ in self?.onSources?() }, for: .touchUpInside)
        title.addGestureRecognizer(UITapGestureRecognizer(target: self, action: #selector(titleTapped)))
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
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (view: MusicPlaybackControlsView, _: UITraitCollection) in
            view.configureTitle()
            view.setNeedsLayout()
        }
        setLayout(layout)
        updatePlayback()
        updateMetadata()
        applyTheme()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func setLayout(_ layout: Layout) {
        self.layout = layout
        for item in [artist, progress, elapsed, durationLabel, previous, nextButton, favorite] {
            setVisible(item, layout != .compact)
        }
        setVisible(lyricPreview, layout == .card)
        for item in [mode, more, quality] { setVisible(item, layout == .full) }
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
        let titleX: CGFloat = compact ? 62 : 100
        let copyWidth = max(0, width - titleX - (compact ? 96 : 16))
        title.frame = CGRect(x: titleX, y: compact ? (height - titleFont.lineHeight) / 2 : 10,
                             width: copyWidth, height: titleFont.lineHeight)
        artist.frame = CGRect(x: titleX, y: title.frame.maxY + 5, width: copyWidth, height: artist.font.lineHeight)
        lyricPreview.frame = CGRect(x: titleX, y: artist.frame.maxY + 4,
                                    width: copyWidth, height: lyricPreview.font.lineHeight + 4)
        progress.frame = CGRect(x: 16, y: height - 100, width: max(0, width - 32), height: 30)
        elapsed.frame = CGRect(x: 16, y: height - 73, width: 80, height: 18)
        durationLabel.frame = CGRect(x: width - 96, y: height - 73, width: 80, height: 18)
        if compact {
            play.frame = CGRect(x: width - 92, y: height - 50, width: 44, height: 44)
            queue.frame = CGRect(x: width - 48, y: height - 50, width: 44, height: 44)
        } else {
            for (index, button) in [previous, play, nextButton, favorite, queue].enumerated() {
                button.frame = CGRect(x: 8 + CGFloat(index) * (width - 16) / 5, y: height - 50,
                                      width: (width - 16) / 5, height: 44)
            }
        }
    }

    private func setVisible(_ view: UIView, _ visible: Bool) {
        view.alpha = visible ? 1 : 0
        view.isUserInteractionEnabled = visible
        view.accessibilityElementsHidden = !visible
    }

    private func updatePlayback() {
        if scrubFence?.admits(content.fence, permitted: content.isAvailable) == false { cancelSeek() }
        play.setImage(PageIcon.image(content.model.phase == .playing ? .pause : .play,
                                     size: layout == .full ? 38 : 23), for: .normal)
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
        favorite.setImage(PageIcon.image(.heart, filled: liked), for: .normal)
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

    private func applyTheme() {
        let theme = content.controlTheme
        guard displayedTheme != theme else { return }
        displayedTheme = theme
        configureTitle()
        for label in [artist, lyricPreview, elapsed, durationLabel] { label.textColor = UIColor(theme.muted) }
        for button in [play, queue, previous, nextButton, favorite, mode, more] { button.tintColor = UIColor(theme.ink) }
        quality.tintColor = UIColor(theme.muted)
        progress.minimumTrackTintColor = UIColor(theme.accent)
        progress.tintColor = UIColor(theme.accent)
        progress.maximumTrackTintColor = UIColor(theme.muted).withAlphaComponent(0.15)
    }

    private func updateClock() {
        guard layout != .compact else { return }
        let clock = content.clock
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
    @objc private func titleTapped() { onTitleTap?() }
}
#endif
