#if os(iOS)
import UIKit
import Combine
import AVKit
import AVFoundation
import MeowUI
import MeowPlayback

/// The real capsule, card and full-player control tree. Product composition
/// moves this one opaque surface; UIKit/Core Animation own every interpolation.
@MainActor
public final class MusicPlayerSurface: WindowGradientView, UIScrollViewDelegate,
    UITableViewDataSource, UITableViewDelegate
{
    public enum Form { case compact, card, full }
    public private(set) var form = Form.compact
    public var onPresent: ((MusicPlaybackDestination) -> Void)?
    public var onMore: (() -> Void)?
    public var onMode: (() -> Void)?
    public var onClose: (() -> Void)?
    public var onSources: (() -> Void)?
    public var onSizeChanged: (() -> Void)?

    private let model: MusicViewModel
    private let playback: PlaybackController
    private let appearance: MusicAppearancePreferencesStore
    private let presentation: MusicMiniPlayerPresentation
    private let favorites: MusicMiniPlayerFavorites
    private let cover = MusicArtworkView(size: 0)
    private let title = LoopingTextView()
    private let artist = UILabel()
    private let lyricPreview = UILabel()
    private let elapsed = UILabel(), remaining = UILabel()
    private let play = UIButton(type: .system), queue = UIButton(type: .system)
    private let previous = UIButton(type: .system), nextButton = UIButton(type: .system)
    private let favorite = UIButton(type: .system), mode = UIButton(type: .system)
    private let more = UIButton(type: .system), close = UIButton(type: .system)
    private let route = AVRoutePickerView()
    private let sourceLabel = UILabel()
    private let progress = MusicProgressSlider()
    private let quality = UIButton(type: .system)
    private let recordScroll = UIScrollView()
    private var discs = (0..<3).map { _ in MusicRecordDiscView() }
    private let tonearm = MusicTonearmView()
    private let lyricTable = UITableView(frame: .zero, style: .plain)
    private let lyricSwitch = UIButton(type: .system)
    private let previewSeek = UIButton(type: .system), followLyrics = UIButton(type: .system)
    private let lyricBackdrop = UIImageView()
    private let lyricBlur = UIVisualEffectView(effect: UIBlurEffect(style: .regular))
    private let backdropMask = CAGradientLayer()
    private var subscriptions: [AnyCancellable] = []
    private var observers: [NSObjectProtocol] = []
    private var mini = MusicMiniPlayerLifecycle()
    private var records = MusicRecordTransition()
    private var lyricSelection = MusicReferenceLyricSelection()
    private var scrubFence: MusicReferencePlayerFence?
    private var lastLyricLine: Int?
    private var active = false
    private var lyricExpanded = false
    private var lastRecordWidth: CGFloat = 0
    private var artworkIdentity: MusicNowPlayingArtworkRequest?
    private let artworkAdmission = MusicNowPlayingArtworkState()
    private var displayedFence: MusicReferencePlayerFence?
    private var morphAnimator: UIViewPropertyAnimator?
    private var playbackTask: Task<Void, Never>?
    private var chorusTask: Task<Void, Never>?
    private var chorusFence: MusicReferencePlayerFence?
    private var chorusDuration: Double?
    private var chorus: MusicChorusPoint?

    public var moreAnchor: UIView { more }
    public var modeAnchor: UIView { mode }

    public init(model: MusicViewModel, playback: PlaybackController,
                appearance: MusicAppearancePreferencesStore,
                presentation: MusicMiniPlayerPresentation) {
        self.model = model
        self.playback = playback
        self.appearance = appearance
        self.presentation = presentation
        favorites = MusicMiniPlayerFavorites(model: model)
        super.init(themeProvider: appearance)
        configureViews()
        for name in [UIApplication.didBecomeActiveNotification,
                     UIApplication.willResignActiveNotification] {
            observers.append(NotificationCenter.default.addObserver(
                forName: name, object: nil, queue: .main
            ) { [weak self] notification in
                MainActor.assumeIsolated {
                    self?.setActive(notification.name == UIApplication.didBecomeActiveNotification &&
                                    self?.window != nil)
                }
            })
        }
        render()
    }

    public var dockHeight: CGFloat {
        let font = UIFontMetrics(forTextStyle: .subheadline).scaledFont(
            for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
        let layout = MusicMiniPlayerLayout(lineHeight: Double(font.lineHeight))
        return CGFloat(form == .card ? layout.expandedHeight : layout.compactHeight)
    }

    public func setForm(_ form: Form) {
        if form == .full { interruptDockMorph() }
        self.form = form
        if form == .full { mini.invalidate(); presentation.invalidate() }
        let full = form == .full
        for item in [recordScroll, tonearm, mode, close, more, lyricSwitch, route, sourceLabel] {
            item.isHidden = !full
        }
        quality.isHidden = !full || model.current == nil
        artist.isHidden = form == .compact
        lyricPreview.isHidden = form != .card
        progress.isHidden = form == .compact
        elapsed.isHidden = form == .compact
        remaining.isHidden = form == .compact
        previous.isHidden = form == .compact
        nextButton.isHidden = form == .compact
        favorite.isHidden = form == .compact
        lyricTable.isHidden = !full || !lyricExpanded
        recordScroll.isHidden = !full || lyricExpanded
        tonearm.isHidden = !full || lyricExpanded
        lyricBackdrop.isHidden = !appearance.preferences.showsLyricBackground(expanded: full && lyricExpanded)
        previewSeek.isHidden = !full || !lyricExpanded || !lyricSelection.isManual
        followLyrics.isHidden = previewSeek.isHidden
        moveCover(to: full ? discs[1].coverHost : self)
        setNeedsLayout()
        configureText()
        updateRotation()
        invalidateIntrinsicContentSize()
    }

    /// Finish a dock morph at its current native position before UIKit moves
    /// this surface. The next animator starts from those real view properties.
    public func interruptDockMorph() {
        guard let animator = morphAnimator else { return }
        morphAnimator = nil
        animator.stopAnimation(false)
        animator.finishAnimation(at: .current)
    }

    public func collapse() {
        mini.invalidate()
        presentation.invalidate()
        if form != .full { updateMiniForm() }
    }

    public func toggleExpansion() {
        guard form != .full, let current = model.current else {
            onPresent?(.player)
            return
        }
        if form == .card { mini.invalidate() }
        else { mini.activate(current.id); mini.hold() }
        updateMiniForm()
    }

    public override func didMoveToWindow() {
        super.didMoveToWindow()
        setActive(window != nil && UIApplication.shared.applicationState == .active)
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        let width = bounds.width, height = bounds.height
        guard width > 0, height > 0 else { return }
        let full = form == .full
        if !full {
            layer.cornerRadius = form == .compact ? height / 2 : 24
            let art: CGFloat = form == .compact ? 42 : 72
            cover.frame = CGRect(x: form == .compact ? 8 : 16, y: 8, width: art, height: art)
            cover.layer.cornerRadius = form == .compact ? 21 : 10
            let titleX = cover.frame.maxX + 12
            let copyWidth = max(0, width - titleX - (form == .compact ? 96 : 16))
            title.frame = CGRect(x: titleX, y: form == .compact ? 17 : 10,
                                 width: copyWidth, height: 24)
            artist.frame = CGRect(x: titleX, y: 37, width: copyWidth, height: 18)
            lyricPreview.frame = CGRect(x: titleX, y: 57, width: copyWidth, height: 25)
            progress.frame = CGRect(x: 16, y: height - 100, width: width - 32, height: 30)
            elapsed.frame = CGRect(x: 16, y: height - 73, width: 80, height: 18)
            remaining.frame = CGRect(x: width - 96, y: height - 73, width: 80, height: 18)
            let controlY = form == .compact ? height - 50 : height - 50
            if form == .compact {
                play.frame = CGRect(x: width - 92, y: controlY, width: 44, height: 44)
                queue.frame = CGRect(x: width - 48, y: controlY, width: 44, height: 44)
            } else {
                for (index, button) in [previous, play, nextButton, favorite, queue].enumerated() {
                    button.frame = CGRect(x: 8 + CGFloat(index) * (width - 16) / 5,
                                          y: controlY, width: (width - 16) / 5, height: 44)
                }
            }
            return
        }
        let top = safeAreaInsets.top
        let bottom = height - safeAreaInsets.bottom
        close.frame = CGRect(x: 12, y: top + 4, width: 44, height: 44)
        lyricSwitch.frame = CGRect(x: width - 60, y: top + 4, width: 44, height: 44)
        route.frame = CGRect(x: width / 2 - 22, y: top + 4, width: 44, height: 44)
        sourceLabel.frame = CGRect(x: 64, y: top + 48, width: width - 128, height: 20)
        let controlsY = bottom - 116
        title.frame = CGRect(x: 28, y: controlsY - 105, width: width - 56, height: 28)
        artist.frame = CGRect(x: 28, y: controlsY - 73, width: width - 56, height: 20)
        progress.frame = CGRect(x: 24, y: controlsY - 44, width: width - 48, height: 30)
        elapsed.frame = CGRect(x: 28, y: controlsY - 17, width: 80, height: 18)
        remaining.frame = CGRect(x: width - 108, y: controlsY - 17, width: 80, height: 18)
        quality.frame = CGRect(x: width / 2 - 70, y: controlsY - 28, width: 140, height: 36)
        let recordTop = top + 76
        let available = max(0, title.frame.minY - recordTop - 12)
        recordScroll.frame = CGRect(x: 0, y: recordTop, width: width, height: available)
        recordScroll.contentSize = CGSize(width: width * 3, height: available)
        let discSize = max(0, min(width * 0.84, available * 0.92))
        for (index, disc) in discs.enumerated() {
            disc.frame = CGRect(x: CGFloat(index) * width + (width - discSize) / 2,
                                y: (available - discSize) / 2, width: discSize, height: discSize)
        }
        let coverSize = discSize * 0.51
        cover.frame = CGRect(x: (discSize - coverSize) / 2, y: (discSize - coverSize) / 2,
                             width: coverSize, height: coverSize)
        cover.layer.cornerRadius = coverSize / 2
        tonearm.frame = CGRect(x: width * 0.57, y: recordTop + 3,
                              width: discSize * 0.3, height: discSize * 0.65)
        if lastRecordWidth != width {
            lastRecordWidth = width
            records.cancel()
            recordScroll.setContentOffset(CGPoint(x: width, y: 0), animated: false)
        }
        lyricTable.frame = recordScroll.frame.insetBy(dx: 20, dy: 0)
        lyricTable.contentInset = UIEdgeInsets(top: max(0, available / 2 - 32), left: 0,
                                              bottom: max(0, available / 2 - 32), right: 0)
        lyricBackdrop.frame = lyricTable.frame
        lyricBlur.frame = lyricBackdrop.bounds
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        backdropMask.frame = lyricBackdrop.bounds
        CATransaction.commit()
        previewSeek.frame = CGRect(x: width - 124, y: lyricTable.frame.midY - 22,
                                   width: 104, height: 44)
        followLyrics.frame = CGRect(x: width - 124, y: lyricTable.frame.maxY - 48,
                                    width: 104, height: 44)
        for (index, button) in [previous, play, nextButton].enumerated() {
            button.frame = CGRect(x: width / 2 - 126 + CGFloat(index) * 84,
                                  y: controlsY + 8, width: 84, height: 62)
        }
        for (index, button) in [mode, favorite, more, queue].enumerated() {
            button.frame = CGRect(x: 24 + CGFloat(index) * (width - 48) / 4,
                                  y: bottom - 48, width: (width - 48) / 4, height: 44)
        }
        updateRotation()
    }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        model.lyricLines.count
    }

    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath)
        -> UITableViewCell {
        let line = model.lyricLines[indexPath.row]
        let cell = tableView.dequeueReusableCell(withIdentifier: MusicLyricCell.reuseIdentifier,
                                                for: indexPath) as! MusicLyricCell
        cell.configure(line: line, selected: line.id == lyricSelection.selected,
                       active: indexPath.row == model.currentLyricIndex,
                       position: model.position, rate: playback.preferredRate,
                       playing: active && model.phase == .playing,
                       preferences: appearance.preferences, theme: theme)
        return cell
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        lyricSelection.begin(fence, selected: model.lyricLines[indexPath.row].id)
        updateLyricPreview()
    }

    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        if scrollView === lyricTable {
            let line = centeredLyric()
            lyricSelection.begin(fence, selected: line, dragging: true,
                                 playbackLine: model.currentLyricIndex)
        } else if scrollView === recordScroll {
            _ = records.begin(fence)
        }
    }

    public func scrollViewWillEndDragging(_ scrollView: UIScrollView, withVelocity velocity: CGPoint,
                                         targetContentOffset: UnsafeMutablePointer<CGPoint>) {
        guard scrollView === lyricTable else { return }
        let point = CGPoint(x: 1, y: targetContentOffset.pointee.y + lyricTable.bounds.height / 2)
        guard let index = lyricTable.indexPathForRow(at: point) else { return }
        let rect = lyricTable.rectForRow(at: index)
        targetContentOffset.pointee.y = rect.midY - lyricTable.bounds.height / 2
    }

    public func scrollViewDidEndDecelerating(_ scrollView: UIScrollView) { finishScroll(scrollView) }
    public func scrollViewDidEndDragging(_ scrollView: UIScrollView, willDecelerate decelerate: Bool) {
        if !decelerate { finishScroll(scrollView) }
    }

    private var fence: MusicReferencePlayerFence {
        .init(occurrence: model.current?.id, attempt: model.activeNativeAttemptID,
              access: model.accountAccessGeneration, queueRevision: model.state.revision,
              settingsRevision: model.state.settingsRevision)
    }

    private func configureViews() {
        layer.cornerRadius = 29
        for item in [cover, title, artist, lyricPreview, progress, elapsed, remaining,
                     recordScroll, tonearm, lyricBackdrop, lyricTable, previewSeek, followLyrics, lyricSwitch,
                     play, queue, previous, nextButton, favorite, mode, more, close, route, sourceLabel, quality] {
            addSubview(item)
        }
        route.prioritizesVideoDevices = false
        route.accessibilityLabel = "音频输出"
        quality.titleLabel?.font = .systemFont(ofSize: 12)
        quality.accessibilityIdentifier = "music.player.source"
        quality.accessibilityLabel = "切换音源与音质"
        quality.addAction(UIAction { [weak self] _ in self?.onSources?() }, for: .touchUpInside)
        sourceLabel.font = .systemFont(ofSize: 12)
        sourceLabel.textAlignment = .center
        lyricBackdrop.contentMode = .scaleAspectFill
        lyricBackdrop.clipsToBounds = true
        lyricBackdrop.alpha = 0.24
        lyricBackdrop.isUserInteractionEnabled = false
        lyricBackdrop.accessibilityElementsHidden = true
        lyricBackdrop.addSubview(lyricBlur)
        backdropMask.colors = [UIColor.clear.cgColor, UIColor.black.cgColor,
                               UIColor.black.cgColor, UIColor.clear.cgColor]
        backdropMask.locations = [0, 0.16, 0.82, 1]
        lyricBackdrop.layer.mask = backdropMask
        artist.font = .systemFont(ofSize: 12)
        lyricPreview.font = .systemFont(ofSize: 13)
        lyricPreview.numberOfLines = 1
        elapsed.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular)
        remaining.font = elapsed.font
        remaining.textAlignment = .right
        for (button, kind, label) in [
            (play, PageIcon.Kind.play, "播放或暂停"), (queue, .queue, "播放队列"),
            (previous, .miniPrevious, "上一首"), (nextButton, .miniNext, "下一首"),
            (favorite, .heart, "收藏"), (more, .more, "更多"), (close, .playerDown, "关闭播放器"),
            (lyricSwitch, .translation, "展开或收起歌词"),
        ] {
            button.setImage(PageIcon.image(kind), for: .normal)
            button.accessibilityLabel = label
        }
        play.accessibilityIdentifier = "music.miniPlayer.play"
        queue.accessibilityIdentifier = "music.miniPlayer.queue"
        play.addAction(UIAction { [weak self] _ in self?.togglePlayback() }, for: .touchUpInside)
        queue.addAction(UIAction { [weak self] _ in self?.onPresent?(.queue) }, for: .touchUpInside)
        previous.addAction(UIAction { [weak self] _ in self?.navigate(.previous) }, for: .touchUpInside)
        nextButton.addAction(UIAction { [weak self] _ in self?.navigate(.next) }, for: .touchUpInside)
        favorite.addAction(UIAction { [weak self] _ in
            guard let self, let track = self.model.current?.track else { return }
            self.mini.hold()
            self.favorites.toggle(track)
        }, for: .touchUpInside)
        more.addAction(UIAction { [weak self] _ in self?.onMore?() }, for: .touchUpInside)
        mode.addAction(UIAction { [weak self] _ in self?.onMode?() }, for: .touchUpInside)
        close.addAction(UIAction { [weak self] _ in self?.onClose?() }, for: .touchUpInside)
        lyricSwitch.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.lyricExpanded.toggle()
            self.setForm(self.form)
            self.updateClock()
        }, for: .touchUpInside)
        progress.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.mini.hold()
            self.scrubFence = self.fence
        }, for: .touchDown)
        progress.addAction(UIAction { [weak self] _ in
            guard let self, self.scrubFence?.admits(self.fence, permitted: self.active) == true else { return }
            self.elapsed.text = MusicMiniPlayerText.time(Double(self.progress.value))
        }, for: .valueChanged)
        progress.addAction(UIAction { [weak self] _ in self?.finishSeek() },
                           for: [.touchUpInside, .touchUpOutside])
        progress.addAction(UIAction { [weak self] _ in
            self?.scrubFence = nil
            self?.updateClock()
        }, for: .touchCancel)
        previewSeek.setTitle("播放此句", for: .normal)
        previewSeek.addAction(UIAction { [weak self] _ in self?.confirmLyric() }, for: .touchUpInside)
        followLyrics.setTitle("跟随播放", for: .normal)
        followLyrics.addAction(UIAction { [weak self] _ in
            self?.lyricSelection.cancel()
            self?.lastLyricLine = nil
            self?.updateLyricPreview()
            self?.updateClock()
        }, for: .touchUpInside)
        let titleTap = UITapGestureRecognizer(target: self, action: #selector(titleTapped))
        title.addGestureRecognizer(titleTap)
        title.isUserInteractionEnabled = true
        recordScroll.isPagingEnabled = true
        recordScroll.showsHorizontalScrollIndicator = false
        recordScroll.delegate = self
        for disc in discs { recordScroll.addSubview(disc) }
        lyricTable.backgroundColor = .clear
        lyricTable.separatorStyle = .none
        lyricTable.dataSource = self
        lyricTable.delegate = self
        lyricTable.register(MusicLyricCell.self, forCellReuseIdentifier: MusicLyricCell.reuseIdentifier)
        lyricTable.accessibilityIdentifier = "music.player.lyrics"
        cover.onImage = { [weak self] image, bytes in self?.acceptCover(image, bytes: bytes) }
        cover.onFailure = { [weak self] url in
            guard let self, self.artworkIdentity?.url == url else { return }
            self.model.artworkFailed(url)
        }
        setForm(.compact)
    }

    private func setActive(_ active: Bool) {
        guard self.active != active else { return }
        self.active = active
        if active {
            subscriptions = [
                model.objectWillChange.receive(on: RunLoop.main).sink { [weak self] in self?.render() },
                model.clock.objectWillChange.receive(on: RunLoop.main).sink { [weak self] in self?.updateClock() },
                playback.objectWillChange.receive(on: RunLoop.main).sink { [weak self] in self?.updateClock() },
                presentation.objectWillChange.receive(on: RunLoop.main).sink { [weak self] in self?.activatePrimaryCard() },
                appearance.objectWillChange.receive(on: RunLoop.main).sink { [weak self] in self?.render() },
                favorites.objectWillChange.receive(on: RunLoop.main).sink { [weak self] in self?.render() },
            ]
            artworkAdmission.becameActive()
            favorites.setActive(model.isAvailable && !model.isClosed)
            render()
        } else {
            subscriptions.removeAll()
            mini.suspend()
            artworkAdmission.suspendRendering()
            favorites.suspend()
            records.cancel()
            scrubFence = nil
            playbackTask?.cancel()
            chorusTask?.cancel()
            chorusFence = nil
            chorusDuration = nil
            lyricSelection.cancel()
            interruptDockMorph()
            configureText()
            updateRotation()
        }
    }

    private func render() {
        favorites.setActive(active && model.isAvailable && !model.isClosed)
        if displayedFence != fence {
            displayedFence = fence
            scrubFence = nil
            lyricSelection.cancel()
            lastLyricLine = nil
            lyricTable.reloadData()
            chorusTask?.cancel()
            chorus = nil
        }
        let request = MusicNowPlayingArtworkRequest(track: model.displayTrack)
        if request != artworkIdentity {
            artworkIdentity = request
            _ = artworkAdmission.activate(request, appearance: appearance)
            cover.load(request.url, cachedData: model.metadataArtworkData)
            mini.select(model.current?.id)
            lyricSelection.cancel()
            lastLyricLine = nil
        } else {
            artworkAdmission.preferencesChanged()
            if let image = cover.image { acceptCover(image, bytes: nil) }
        }
        if !records.accepts(fence) {
            records.cancel()
            recordScroll.setContentOffset(CGPoint(x: recordScroll.bounds.width, y: 0), animated: false)
        }
        synchronizeRecords()
        play.setImage(PageIcon.image(model.phase == .playing ? .pause : .play,
                                     size: form == .full ? 38 : 23), for: .normal)
        favorite.setImage(PageIcon.image(.heart,
                                        filled: model.current.map { favorites.isFavorite($0.track) } ?? false),
                          for: .normal)
        mode.setImage(UIImage(systemName: model.state.mode.symbol), for: .normal)
        lyricBackdrop.isHidden = !appearance.preferences.showsLyricBackground(
            expanded: form == .full && lyricExpanded)
        quality.isHidden = form != .full || model.current == nil
        let lyricStatus = model.lyricLines.isEmpty ? (model.lyricState == "disabled" ? "歌词已关闭" :
            model.lyricState == "loading" ? "歌词加载中" : "暂无歌词") : nil
        let label = UILabel()
        label.text = lyricStatus; label.textColor = UIColor(theme.muted)
        label.textAlignment = .center
        lyricTable.backgroundView = lyricStatus == nil ? nil : label
        configureText()
        updateClock()
        if !model.isAvailable || model.isClosed {
            mini.invalidate()
            presentation.invalidate()
            artworkAdmission.deactivate()
            cover.load(nil)
        }
        if form != .full { updateMiniForm() }
    }

    private func configureText() {
        let text = form == .compact ? MusicMiniPlayerText.compactTitle(model.displayTrack)
            : model.displayTrack?.title ?? "暂无播放"
        title.configure(text: text,
                        font: UIFontMetrics(forTextStyle: .subheadline).scaledFont(
                            for: .systemFont(ofSize: form == .full ? 20 : 14, weight: .semibold)),
                        color: UIColor(theme.ink), allowsMotion: !UIAccessibility.isReduceMotionEnabled,
                        running: active, gap: 28, pointsPerSecond: 24, minimumDuration: 0)
        artist.text = model.displayTrack?.artist ?? ""
        artist.textColor = UIColor(theme.muted)
        sourceLabel.text = MusicReferencePlayerLabels.source(
            fm: model.isFMActive ? MusicFMModeChoice.title(for: model.activeHomeFM) : nil,
            context: model.current?.sourceTitle,
            collection: model.collections.first { $0.id == model.state.sourceCollectionID }?.title,
            track: model.displayTrack?.title ?? "")
        sourceLabel.textColor = UIColor(theme.muted)
        route.tintColor = UIColor(theme.ink)
        route.activeTintColor = UIColor(theme.accent)
        quality.setTitle(model.activeAudioDelivery.map { MusicQualityLabel.name($0.quality) } ?? "音质未知",
                         for: .normal)
        quality.tintColor = UIColor(theme.muted)
        quality.accessibilityValue = model.activeAudioIsCached.map { $0 ? "本机缓存" : "网络音源" } ?? "未选择音源"
        lyricPreview.textColor = UIColor(theme.muted)
        elapsed.textColor = UIColor(theme.muted)
        remaining.textColor = UIColor(theme.muted)
        for button in [play, queue, previous, nextButton, favorite, mode, more, close,
                       lyricSwitch, previewSeek, followLyrics] { button.tintColor = UIColor(theme.ink) }
        progress.minimumTrackTintColor = UIColor(theme.accent)
        progress.tintColor = UIColor(theme.accent)
        progress.maximumTrackTintColor = UIColor(theme.muted).withAlphaComponent(0.15)
    }

    private func updateClock() {
        let snapshot = playback.snapshot
        let matching = model.activeNativeAttemptID == playback.currentAttempt?.id &&
            snapshot.owner == "music" && snapshot.resourceID == model.activeResourceID
        mini.sample(.init(occurrenceID: model.current?.id,
                          attemptID: matching ? playback.currentAttempt?.id : nil,
                          at: ProcessInfo.processInfo.systemUptime, position: snapshot.position,
                          rate: Double(playback.player.rate),
                          playing: active && matching && snapshot.phase == .playing &&
                              playback.player.timeControlStatus == .playing,
                          seeking: snapshot.phase == .buffering || snapshot.phase == .preparing))
        let duration = model.duration ?? 0
        if scrubFence == nil {
            progress.maximumValue = Float(max(1, duration))
            progress.value = Float(min(duration, max(0, model.position)))
        }
        elapsed.text = MusicMiniPlayerText.time(model.position)
        remaining.text = MusicMiniPlayerText.time(model.duration)
        let ranges = matching ? playback.player.currentItem?.loadedTimeRanges.compactMap { value -> Range<Double>? in
            let range = value.timeRangeValue
            let start = range.start.seconds, end = (range.start + range.duration).seconds
            return start.isFinite && end.isFinite && end > start ? start..<end : nil
        } ?? [] : []
        progress.buffers = MusicReferenceProgressGeometry.buffers(
            ranges, duration: model.duration, complete: matching && model.activeAudioIsCached == true)
        loadChorus()
        progress.chorus = chorus.map { MusicReferenceProgressGeometry.fraction($0.start, duration: model.duration) }
        lyricPreview.text = model.currentLyricIndex.flatMap {
            model.lyricLines.indices.contains($0) ? model.lyricLines[$0].text : nil
        } ?? ""
        if form != .full { updateMiniForm() }
        if lyricExpanded, let index = model.currentLyricIndex,
           !lyricSelection.isManual, !lyricTable.isDragging, !lyricTable.isDecelerating,
           index != lastLyricLine, model.lyricLines.indices.contains(index) {
            lastLyricLine = index
            lyricTable.reloadData()
            lyricTable.layoutIfNeeded()
            lyricTable.scrollToRow(at: IndexPath(row: index, section: 0), at: .middle,
                                   animated: !UIAccessibility.isReduceMotionEnabled)
        } else if lyricExpanded {
            for index in lyricTable.indexPathsForVisibleRows ?? [] {
                if let cell = lyricTable.cellForRow(at: index) as? MusicLyricCell,
                   model.lyricLines.indices.contains(index.row) {
                    cell.updateTime(model.position, playing: active && model.phase == .playing,
                                    rate: playback.preferredRate, active: index.row == model.currentLyricIndex)
                }
            }
        }
        updateRotation()
    }

    private func activatePrimaryCard() {
        guard form != .full, let value = presentation.activation,
              value.playback.occurrenceID == model.current?.id,
              value.playback.nativeAttemptID == model.activeNativeAttemptID,
              value.playback.accessGeneration == model.accountAccessGeneration else { return }
        mini.activate(value.playback.occurrenceID)
        updateMiniForm()
    }

    private func loadChorus() {
        guard active, form == .full, let duration = model.duration, duration.isFinite, duration > 0,
              model.activeNativeAttemptID != nil else { return }
        let expected = fence
        guard chorusFence != expected || chorusDuration != duration else { return }
        guard chorusTask == nil else { return }
        chorusFence = expected; chorusDuration = duration; chorus = nil
        chorusTask = Task { [weak self] in
            guard let self else { return }
            defer {
                self.chorusTask = nil
                if Task.isCancelled, self.chorusFence == expected, self.chorusDuration == duration {
                    self.chorusFence = nil
                    self.chorusDuration = nil
                }
                if self.active, self.form == .full { self.loadChorus() }
            }
            guard !Task.isCancelled, self.active, self.form == .full,
                  self.fence == expected, self.model.duration == duration else { return }
            let value = try? await self.model.chorus(actualDuration: duration)
            guard !Task.isCancelled, self.active, self.form == .full,
                  self.fence == expected, self.chorusFence == expected,
                  self.chorusDuration == duration, self.model.duration == duration else { return }
            self.chorus = value
            self.progress.chorus = value.map { MusicReferenceProgressGeometry.fraction($0.start, duration: duration) }
        }
    }

    private func updateMiniForm() {
        let target: Form = mini.expanded ? .card : .compact
        guard form != .full, form != target else { return }
        let curve = MusicMiniPlayerMorph(expanded: mini.expanded)
        interruptDockMorph()
        superview?.superview?.layoutIfNeeded()
        let animator = UIViewPropertyAnimator(duration: UIAccessibility.isReduceMotionEnabled ? 0 : curve.duration,
                                             controlPoint1: CGPoint(x: curve.x1, y: curve.y1),
                                             controlPoint2: CGPoint(x: curve.x2, y: curve.y2))
        morphAnimator = animator
        animator.addAnimations { [weak self] in
            guard let self else { return }
            self.setForm(target)
            self.onSizeChanged?()
            self.layoutIfNeeded()
        }
        animator.addCompletion { [weak self, weak animator] _ in
            if self?.morphAnimator === animator { self?.morphAnimator = nil }
        }
        animator.startAnimation()
    }

    private func finishSeek() {
        defer { scrubFence = nil; updateClock() }
        guard scrubFence?.admits(fence, permitted: model.isAvailable && !model.isClosed) == true else { return }
        _ = model.seek(to: Double(progress.value))
    }

    private func finishScroll(_ scrollView: UIScrollView) {
        if scrollView === lyricTable {
            if let id = centeredLyric() { lyricSelection.end(id) }
            updateLyricPreview()
            return
        }
        guard scrollView === recordScroll, recordScroll.bounds.width > 0,
              records.accepts(fence) else { return }
        let index = Int((recordScroll.contentOffset.x / recordScroll.bounds.width).rounded())
        if index == 1 { records.cancel(); return }
        let direction: MusicDirection = index < 1 ? .previous : .next
        guard let token = records.request(direction, fence: fence) else { return }
        let accepted = model.requestNavigation(direction, onSelection: { [weak self] entry in
            guard let self else { return }
            _ = self.records.selected(entry, token: token, current: self.fence)
        }, onCompletion: { [weak self] in
            guard let self, self.records.token == token else { return }
            let accepted = self.records.selectedID == self.model.current?.id && self.records.phase == .settling
            if accepted {
                let selected = direction == .next ? 2 : 0
                self.discs[1].setRotating(false)
                self.discs.swapAt(1, selected)
                self.moveCover(to: self.discs[1].coverHost)
            }
            self.records.cancel()
            self.synchronizeRecords()
            self.setNeedsLayout()
            self.layoutIfNeeded()
            self.recordScroll.setContentOffset(CGPoint(x: self.recordScroll.bounds.width, y: 0),
                                               animated: !accepted && !UIAccessibility.isReduceMotionEnabled)
        })
        if !accepted {
            records.cancel()
            recordScroll.setContentOffset(CGPoint(x: recordScroll.bounds.width, y: 0), animated: true)
        }
    }

    private func synchronizeRecords() {
        let previousID = model.isFMActive ? model.state.fmPreviousPreviewID : model.state.previousPreviewID
        let nextID = model.isFMActive ? model.state.fmNextPreviewID : model.state.nextPreviewID
        let previous = model.queue.first { $0.id == previousID }
        let next = model.queue.first { $0.id == nextID }
        records.synchronize(current: model.current, previous: previous, next: next)
        for (index, slot) in records.slots.enumerated() {
            let entry = slot.entry
            discs[index].configure(theme: theme, artwork: index == 1 ? nil : entry?.track.artwork,
                                   current: index == 1)
        }
    }

    private func updateRotation() {
        discs[1].setRotating(active && form == .full && !lyricExpanded &&
                            model.phase == .playing && !UIAccessibility.isReduceMotionEnabled)
        tonearm.setPlaying(model.phase == .playing)
    }

    private func centeredLyric() -> Int? {
        let point = CGPoint(x: 1, y: lyricTable.contentOffset.y + lyricTable.bounds.height / 2)
        return lyricTable.indexPathForRow(at: point).flatMap {
            model.lyricLines.indices.contains($0.row) ? model.lyricLines[$0.row].id : nil
        }
    }

    private func updateLyricPreview() {
        previewSeek.isHidden = form != .full || !lyricExpanded || !lyricSelection.isManual
        followLyrics.isHidden = previewSeek.isHidden
        lyricTable.reloadData()
    }

    private func confirmLyric() {
        guard let target = lyricSelection.target(lines: model.lyricLines, current: fence,
                                                permitted: model.isAvailable && !model.isClosed,
                                                duration: model.duration) else { return }
        _ = lyricSelection.confirm(accepted: model.seek(to: target), current: fence, permitted: true)
        updateLyricPreview()
    }

    private func togglePlayback() {
        guard playbackTask == nil else { return }
        mini.hold()
        let own = fence
        playbackTask = Task { [weak self] in
            guard let self else { return }
            defer { self.playbackTask = nil }
            guard !Task.isCancelled, own.admits(self.fence, permitted: self.active) else { return }
            await self.model.togglePlayback()
        }
    }

    private func navigate(_ direction: MusicDirection) {
        mini.hold()
        if direction == .next { _ = model.requestNext() } else { _ = model.requestPrevious() }
    }

    private func moveCover(to target: UIView) {
        guard cover.superview !== target else { return }
        let original = cover.superview.map { $0.convert(cover.frame, to: target) } ?? cover.frame
        cover.removeFromSuperview()
        target.addSubview(cover)
        cover.frame = original
    }

    private func acceptCover(_ image: UIImage, bytes: Data?) {
        lyricBackdrop.image = image
        let revision = artworkAdmission.samplingRevision
        _ = artworkAdmission.deliverImage(revision: revision) { }
        _ = artworkAdmission.captureArtwork(revision: revision) { image.jpegData(compressionQuality: 0.85) }
        if let bytes {
            _ = artworkAdmission.captureDisplay(revision: revision, encode: { bytes }) { [weak model] data in
                model?.capturedArtwork(data, url: self.artworkIdentity?.url)
            }
        }
        _ = artworkAdmission.sample(revision: revision) {
            image.cgImage.flatMap { MusicCoverPaletteSampler.rgba($0) }
        }
    }

    @objc private func titleTapped() {
        if form == .compact { toggleExpansion() }
        else { onPresent?(.player) }
    }

    deinit {
        playbackTask?.cancel()
        chorusTask?.cancel()
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
    }
}
#endif
