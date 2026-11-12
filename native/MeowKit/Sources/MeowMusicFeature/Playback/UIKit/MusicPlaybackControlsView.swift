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
    var onFMSettings: (() -> Void)?
    var onSources: (() -> Void)?
    var onCollapse: (() -> Void)?
    private let content: MusicPlayerContentModel
    private let title = LoopingTextView()
    private(set) lazy var titleSwipe = MusicMiniPlayerTitleSwipeView(title: title)
    private let artist = UILabel(), lyricPreview = UILabel()
    private let elapsed = UILabel(), durationLabel = UILabel()
    private let play = UIButton(type: .system), queue = UIButton(type: .system)
    private let compactPlay = UIButton(type: .system), compactQueue = UIButton(type: .system)
    private let previous = UIButton(type: .system), nextButton = UIButton(type: .system)
    private let favorite = UIButton(type: .system), mode = UIButton(type: .system)
    private let more = UIButton(type: .system), quality = UIButton(type: .system)
    private let collapse = UIButton(type: .system)
    private let progress = MusicProgressSlider()
    private let compactTrack = CAShapeLayer(), compactProgress = CAShapeLayer()
    private let playOutline = CAShapeLayer()
    private var subscriptions: [AnyCancellable] = []
    private var scrubFence: MusicReferencePlayerFence?
    private var rendering = false
    private var layout: Layout
    private var fullViewportHeight: CGFloat?
    private var fullSafeHeight: CGFloat?
    private var compactTitleFont = UIFont.systemFont(ofSize: 14, weight: .semibold)
    private var titleFont = UIFont.systemFont(ofSize: 14, weight: .semibold)
    private var displayedTheme: PageTheme?
    private struct ModeMenuState: Equatable {
        let fence: MusicReferencePlayerFence
        let command: UUID
        let similarID: UUID?
        let recommendationID: UUID?
        let fmID: UUID?
        let presentation: MusicPlaybackModePresentation
        let source: String
        let activeSource: Bool
        let heartbeat: Bool
        let canHeartbeat: Bool
        let canRecommend: Bool
        let canReturn: Bool
        let loading: Bool
        let canRetry: Bool
        let needsReadback: Bool
        let errorID: UUID?
        let available: Bool
    }
    private var displayedMode: ModeMenuState?
    var moreAnchor: UIView { more }
    var miniLayout: MusicMiniPlayerLayout {
        .init(compactTitleLineHeight: Double(compactTitleFont.lineHeight),
              titleLineHeight: Double(titleFont.lineHeight), artistLineHeight: Double(artist.font.lineHeight),
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
        for item in [artist, lyricPreview, elapsed, durationLabel, progress,
                     play, queue, compactPlay, compactQueue, previous, nextButton, favorite, mode,
                     more, quality, collapse] {
            addSubview(item)
        }
        if layout == .full { addSubview(title) }
        else { addSubview(titleSwipe) }
        for ring in [compactTrack, compactProgress, playOutline] {
            ring.fillColor = UIColor.clear.cgColor
            ring.lineCap = .round
        }
        for ring in [compactTrack, compactProgress] { compactPlay.layer.insertSublayer(ring, at: 0) }
        play.layer.insertSublayer(playOutline, at: 0)
        compactTrack.name = "play.progress.track"
        compactProgress.name = "play.progress.played"
        compactProgress.strokeEnd = 0
        playOutline.name = "play.outline"
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
            (compactPlay, .play, "播放或暂停"), (compactQueue, .queue, "播放队列"),
            (previous, .previous, "上一首"), (nextButton, .next, "下一首"),
            (favorite, .heart, "收藏"), (more, .more, "更多")
        ] {
            button.setImage(PageIcon.image(kind), for: .normal)
            button.accessibilityLabel = label
        }
        mode.accessibilityLabel = "播放模式"
        mode.preferredMenuElementOrder = .fixed
        let prefix = layout == .full ? "music.player" : "music.miniPlayer"
        play.accessibilityIdentifier = layout == .full ? "\(prefix).play" : "\(prefix).card.play"
        queue.accessibilityIdentifier = layout == .full ? "\(prefix).queue" : "\(prefix).card.queue"
        compactPlay.accessibilityIdentifier = "music.miniPlayer.play"
        compactQueue.accessibilityIdentifier = "music.miniPlayer.queue"
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
        for button in [play, compactPlay] {
            button.addAction(UIAction { [weak content] _ in content?.togglePlayback() }, for: .touchUpInside)
        }
        previous.addAction(UIAction { [weak content] _ in content?.navigate(.previous) }, for: .touchUpInside)
        nextButton.addAction(UIAction { [weak content] _ in content?.navigate(.next) }, for: .touchUpInside)
        favorite.addAction(UIAction { [weak content] _ in content?.toggleFavorite() }, for: .touchUpInside)
        for button in [queue, compactQueue] {
            button.addAction(UIAction { [weak self] _ in self?.onPresent?(.queue) }, for: .touchUpInside)
        }
        more.addAction(UIAction { [weak self] _ in self?.onMore?() }, for: .touchUpInside)
        quality.showsMenuAsPrimaryAction = true
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
            content.queueChanges.sink { [weak self] in self?.updateMode() },
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
        if self.layout == .full, layout != .full {
            titleSwipe.attachTitle()
            addSubview(titleSwipe)
        } else if self.layout != .full, layout == .full {
            titleSwipe.cancel()
            titleSwipe.removeFromSuperview()
            addSubview(title)
        }
        self.layout = layout
        updateFonts(compatibleWith: traits)
        if layout == .compact { scrubFence = nil }
        for item in [artist, progress, elapsed, durationLabel, play, queue, previous, nextButton, favorite] {
            setVisible(item, layout != .compact)
        }
        for item in [compactPlay, compactQueue] { setVisible(item, layout == .compact) }
        setVisible(lyricPreview, layout == .card)
        setVisible(collapse, layout == .card)
        for item in [mode, more, quality] { setVisible(item, layout == .full) }
        for (button, kind, size) in [
            (previous, PageIcon.Kind.previous, layout == .full ? CGFloat(25) : 26),
            (nextButton, PageIcon.Kind.next, layout == .full ? CGFloat(25) : 26),
            (queue, PageIcon.Kind.queue, layout == .full ? CGFloat(24) : 26)
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
        if !rendering {
            cancelSeek()
            mode.contextMenuInteraction?.dismissMenu()
        }
        configureTitle()
        updateClock()
    }

    func beginTitleSwipe(previous: MusicQueueEntry?, next: MusicQueueEntry?) {
        configureTitle(suspending: true)
        titleSwipe.begin(current: swipeText(content.model.displayTrack),
                         previous: previous.map { swipeText($0.track) },
                         next: next.map { swipeText($0.track) } ??
                            (content.model.canNext ? swipeText(nil, fallback: "下一首") : nil))
    }

    func updateIncomingTitle(_ entry: MusicQueueEntry, direction: MusicDirection) {
        titleSwipe.updateIncoming(swipeText(entry.track), direction: direction)
    }

    func endTitleSwipe() {
        titleSwipe.cancel()
        configureTitle()
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
        titleSwipe.frame = CGRect(x: titleX, y: compact ? (height - titleFont.lineHeight) / 2 : 16,
                                  width: compact ? copyWidth : max(0, copyWidth - 40),
                                  height: titleFont.lineHeight)
        artist.frame = CGRect(x: titleX, y: titleSwipe.frame.maxY + 6,
                              width: titleSwipe.frame.width, height: artist.font.lineHeight)
        lyricPreview.frame = CGRect(x: titleX, y: artist.frame.maxY + 8,
                                    width: copyWidth, height: lyricPreview.font.lineHeight + 4)
        collapse.frame = CGRect(x: width - 56, y: 6, width: 44, height: 44)
        let seekTop = CGFloat(miniLayout.seekTop)
        progress.frame = CGRect(x: 16, y: seekTop, width: max(0, width - 32), height: 28)
        elapsed.frame = CGRect(x: 16, y: seekTop + 28, width: 80, height: 18)
        durationLabel.frame = CGRect(x: width - 96, y: seekTop + 28, width: 80, height: 18)
        let metrics = miniLayout
        let frame = metrics.compactControlFrame(width: Double(width))
            .offsetBy(dx: 0, dy: height - CGFloat(metrics.compactHeight))
        compactPlay.frame = CGRect(x: frame.minX, y: frame.minY, width: 44, height: 44)
        compactQueue.frame = CGRect(x: frame.minX + 44, y: frame.minY, width: 44, height: 44)
        // Both sets belong to the dock's fixed bottom edge, not to each other's slots.
        let controlsTop = height - CGFloat(metrics.expandedHeight - metrics.controlsTop)
        let cellWidth = max(0, width - 32) / 5
        for (index, button) in [favorite, previous, play, nextButton, queue].enumerated() {
            button.frame = CGRect(x: 16 + CGFloat(index) * cellWidth, y: controlsTop,
                                  width: cellWidth, height: 56)
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
        play.setImage(PageIcon.image(playKind, size: layout == .full ? 34 : 28),
                      for: .normal)
        compactPlay.setImage(PageIcon.image(playing ? .pause : .play, size: 18), for: .normal)
        play.isEnabled = content.isAvailable
        compactPlay.isEnabled = content.isAvailable
        previous.isEnabled = content.model.canPrevious
        nextButton.isEnabled = content.model.canNext
        queue.isEnabled = content.isAvailable
        compactQueue.isEnabled = content.isAvailable
        progress.isEnabled = content.isAvailable && content.model.activeNativeAttemptID != nil
        quality.isEnabled = content.isAvailable && content.model.current != nil
        updateMode()
        let model = content.model
        let hasPlayback = model.activeResourceID != nil || model.isResolving
        let qualityName = model.isResolving ? "匹配中" : model.activeAudioDelivery.map {
            MusicSourceQualityPolicy.group($0.quality) == "spatial" ? "空间" : MusicQualityLabel.name($0.quality)
        } ?? "音质未知"
        quality.setTitle(qualityName, for: .normal)
        quality.alpha = hasPlayback ? 1 : 0
        quality.accessibilityElementsHidden = !hasPlayback
        quality.isUserInteractionEnabled = hasPlayback
        let occurrence = model.current?.id, access = model.accountAccessGeneration
        func action(_ value: String) -> UIAction {
            UIAction(title: MusicQualityLabel.name(value), state: model.activeAudioDelivery?.quality == value ? .on : .off) { [weak model] _ in
                Task {
                    guard let model, model.current?.id == occurrence, model.accountAccessGeneration == access else { return }
                    await model.switchQuality(value)
                }
            }
        }
        let enabled = MusicSourceQualityPolicy.ordered(model.state, automatic: false)
        let spatial = enabled.filter { MusicSourceQualityPolicy.group($0) == "spatial" }
        quality.menu = UIMenu(children: [
            UIAction(title: "查看更多", image: UIImage(systemName: "list.bullet")) { [weak self] _ in self?.onSources?() },
            UIMenu(options: .displayInline, children: enabled.filter {
                MusicSourceQualityPolicy.group($0) != "spatial"
            }.map(action)),
            UIMenu(title: "空间", children: spatial.map(action)),
        ])
        quality.accessibilityValue = content.model.activeAudioIsCached.map { $0 ? "本机缓存" : "网络音源" } ?? "未选择音源"
        updateFavorite()
        updateClock()
    }

    private var modeMenuState: ModeMenuState {
        let model = content.model, similar = model.similarMode, recommendations = model.queueRecommendations
        let radio = model.fmPlayback
        return ModeMenuState(fence: content.fence, command: model.recommendationCommandID,
            similarID: similar?.id, recommendationID: recommendations?.id, fmID: radio?.id,
            presentation: .init(mode: model.state.mode, similar: similar, recommendations: recommendations, fm: radio),
            source: model.playbackSourceTitle,
            activeSource: model.isFMActive || radio != nil || similar?.active == true || recommendations?.active == true,
            heartbeat: similar?.active == true && similar?.provider == .heartbeat,
            canHeartbeat: model.canEnterSimilarMode, canRecommend: model.canEnterSongRecommendations,
            canReturn: similar?.canRestoreQueue == true,
            loading: radio?.loading == true || similar?.status == .loading || recommendations?.status == .loading,
            canRetry: similar?.canRetry == true || recommendations?.canRetry == true,
            needsReadback: similar?.error == .commitUnconfirmed || recommendations?.error == .commitUnconfirmed || radio?.error == .commitUnconfirmed,
            errorID: similar?.errorID ?? recommendations?.errorID ?? radio?.errorID,
            available: content.isAvailable && model.current != nil && !model.isChangingSimilarMode && !model.isChangingQueueRecommendations)
    }

    private func updateMode() {
        guard layout == .full else { return }
        let state = modeMenuState
        guard displayedMode != state else { return }
        displayedMode = state
        mode.contextMenuInteraction?.dismissMenu()
        let presentation = state.presentation
        let ordinaryIcon: MusicPlaybackModeIcon.Kind
        switch presentation.mode { case .loop: ordinaryIcon = .loop; case .single: ordinaryIcon = .single; case .shuffle: ordinaryIcon = .shuffle }
        func specialImage(_ special: MusicPlaybackModePresentation.Special) -> UIImage {
            switch special {
            case .heartbeat: return MusicPlaybackModeIcon.image(.heartbeat)
            case .roaming: return MusicPlaybackModeIcon.image(.fm)
            case .recommendations: return MusicPlaybackModeIcon.image(.recommendations)
            }
        }
        let image: UIImage
        if presentation.continuing, let special = presentation.special { image = specialImage(special) }
        else { image = MusicPlaybackModeIcon.image(ordinaryIcon) }
        mode.setImage(image, for: .normal)
        mode.accessibilityValue = presentation.title
        mode.isEnabled = state.available
        mode.showsMenuAsPrimaryAction = true
        let model = content.model, similar = model.similarMode, recommendations = model.queueRecommendations
        let radio = model.fmPlayback
        var actions: [UIMenuElement] = []
        if state.canReturn {
            actions.append(UIAction(title: state.heartbeat ? "退出心动模式" : "退出推荐模式",
                                    image: UIImage(systemName: "arrow.uturn.backward")) { [weak self] _ in
                guard let self, self.admitsModeAction(state) else { return }
                self.content.returnFromRecommendations(snapshot: similar?.id, expected: state.fence)
            })
        }
        if let special = presentation.special {
            actions.append(UIAction(title: special.title, image: specialImage(special),
                                    state: presentation.continuing ? .on : .off) { [weak self] _ in
                guard let self, self.admitsModeAction(state) else { return }
                if let radio { self.content.setFMContinue(true, session: radio.id, expected: state.fence) }
                else if let similar, similar.active {
                    self.content.setSimilarContinue(true, snapshot: similar.id, expected: state.fence)
                } else if let recommendations {
                    self.content.setQueueRecommendationContinue(true, snapshot: recommendations.id, expected: state.fence)
                }
            })
        } else if !state.activeSource && !state.canRetry && !state.needsReadback && !state.loading {
            if state.canRecommend {
                actions.append(UIAction(title: "推荐模式", image: specialImage(.recommendations)) { [weak self] _ in
                    guard let self, self.admitsModeAction(state) else { return }
                    self.content.startSongRecommendations(expected: state.fence)
                })
            }
            if state.canHeartbeat {
                actions.append(UIAction(title: "心动模式", image: specialImage(.heartbeat)) { [weak self] _ in
                    guard let self, self.admitsModeAction(state) else { return }
                    self.content.startCurrentHeartbeat(expected: state.fence)
                })
            }
        }
        let modes: [(MusicPlaybackMode, MusicPlaybackModeIcon.Kind)] = [(.loop, .loop), (.single, .single), (.shuffle, .shuffle)]
        actions += modes.map { choice, icon in
            UIAction(title: choice.title, image: MusicPlaybackModeIcon.image(icon),
                     state: !presentation.continuing && presentation.mode == choice ? .on : .off) { [weak self] _ in
                guard let self, self.admitsModeAction(state) else { return }
                self.content.setMode(choice, expected: state.fence)
            }
        }
        mode.menu = UIMenu(children: actions)
    }

    private func admitsModeAction(_ state: ModeMenuState) -> Bool {
        rendering && layout == .full && state.available && modeMenuState == state
    }

    private func updateFavorite() {
        let liked = content.model.current.map { content.favorites.isFavorite($0.track) } ?? false
        favorite.setImage(PageIcon.image(.heart, filled: liked, size: layout == .full ? 23 : 26), for: .normal)
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

    private func configureTitle(suspending: Bool = false) {
        guard layout == .full || !titleSwipe.isSwiping else { return }
        let track = content.model.displayTrack
        let text = layout == .compact ? MusicMiniPlayerText.compactTitle(track) : track?.title ?? "暂无播放"
        let theme = content.controlTheme
        let colorRanges = layout == .compact ? compactTitleColors(track, text: text) : []
        title.configure(text: text,
                        font: titleFont, color: UIColor(theme.ink),
                        colorRanges: colorRanges,
                        allowsMotion: !UIAccessibility.isReduceMotionEnabled,
                        running: rendering && !suspending,
                        gap: layout == .full ? 40 : 28, pointsPerSecond: 24, minimumDuration: 0)
    }

    private func compactTitleColors(_ track: MusicRemoteTrack?, text: String) -> [(NSRange, UIColor)] {
        if let track {
            let artist = track.artist.trimmingCharacters(in: .whitespacesAndNewlines)
            let suffix = " - " + artist
            if !artist.isEmpty, artist != "未知", artist != "未知歌手", text.hasSuffix(suffix) {
                let length = (suffix as NSString).length
                return [(NSRange(location: (text as NSString).length - length, length: length),
                         UIColor(content.controlTheme.muted))]
            }
        }
        return []
    }

    private func swipeText(_ track: MusicRemoteTrack?, fallback: String = "暂无播放") -> NSAttributedString {
        let text = track.map { MusicMiniPlayerText.compactTitle($0) } ?? fallback
        let result = NSMutableAttributedString(string: text, attributes: [
            .font: compactTitleFont, .foregroundColor: UIColor(content.controlTheme.ink)
        ])
        for (range, color) in compactTitleColors(track, text: text) {
            result.addAttribute(.foregroundColor, value: color, range: range)
        }
        return result
    }

    func updateFonts(compatibleWith traits: UITraitCollection) {
        let metrics = UIFontMetrics(forTextStyle: .subheadline)
        compactTitleFont = metrics.scaledFont(
            for: .systemFont(ofSize: 14, weight: .semibold), compatibleWith: traits)
        titleFont = layout == .compact ? compactTitleFont : metrics.scaledFont(
            for: .systemFont(ofSize: 16, weight: layout == .full ? .medium : .semibold),
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
            for button in [play, queue, compactPlay, compactQueue, previous, nextButton, favorite, mode, more, collapse] {
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
        compactTrack.strokeColor = ink.withAlphaComponent(0.18).cgColor
        compactProgress.strokeColor = ink.cgColor
        compactTrack.lineWidth = 2
        compactProgress.lineWidth = 2
        playOutline.strokeColor = ink.withAlphaComponent(0.85).cgColor
        playOutline.lineWidth = 1.5
        playOutline.opacity = layout == .full ? 0 : 1
        CATransaction.commit()
    }

    private func layoutPlayRing() {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for (button, diameter, rings) in [
            (compactPlay, CGFloat(32), [compactTrack, compactProgress]),
            (play, CGFloat(52), [playOutline])
        ] {
            let radius = min(diameter, min(button.bounds.width, button.bounds.height)) / 2 - 1
            let path = UIBezierPath(arcCenter: CGPoint(x: button.bounds.midX, y: button.bounds.midY),
                                    radius: max(0, radius), startAngle: -.pi / 2, endAngle: .pi * 1.5,
                                    clockwise: true).cgPath
            for ring in rings { ring.frame = button.bounds; ring.path = path }
        }
        CATransaction.commit()
    }

    private func updateClock() {
        let clock = content.clock
        if layout == .compact {
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            compactProgress.strokeEnd = CGFloat(MusicMiniPlayerText.progress(position: clock.position, duration: clock.duration))
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
