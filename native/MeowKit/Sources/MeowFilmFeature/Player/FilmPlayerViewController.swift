#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public protocol FilmPlaybackPage: AnyObject {
    var filmPlaybackModel: FilmPlaybackModel? { get }
}

/// Passive controls over the App's one native video surface.
@MainActor
public final class FilmPlayerViewController: PageViewController, FilmPlaybackPage, UIGestureRecognizerDelegate {
    private let presentationOwner = UUID()
    public var filmPlaybackModel: FilmPlaybackModel? { model }
    public var onDismiss: (() -> Void)?
    let model: FilmPlaybackModel
    let video = UIView()
    private let top = UIStackView(), bottom = UIStackView()
    private let transport = UIStackView(), sourceControls = UIStackView(), controlGroups = UIStackView()
    private let titleLabel = UILabel(), sourceLabel = UILabel(), stateLabel = UILabel()
    private let activity = UIActivityIndicatorView(style: .large)
    private let play = FilmPlayerButton(type: .system), previous = FilmPlayerButton(type: .system), nextButton = FilmPlayerButton(type: .system)
    private let seek = UISlider(), volume = UISlider()
    private let position = UILabel(), duration = UILabel()
    private let pip = FilmPlayerButton(type: .system), settings = FilmPlayerButton(type: .system), shortVideo = FilmPlayerButton(type: .system)
    private let sound = FilmPlayerButton(type: .system)
    private let skipChapter = FilmPlayerButton(type: .system)
    private let rate = FilmPlayerMenuButton(type: .system), scale = FilmPlayerMenuButton(type: .system)
    private let pan = FilmPlayerMenuButton(type: .system), quality = FilmPlayerMenuButton(type: .system)
    private let change = FilmPlayerButton(type: .system), block = FilmPlayerButton(type: .system)
    private let retryMedia = FilmPlayerButton(type: .system)
    let settingsPanel = UIScrollView()
    private var videoView: UIView?
    private var videoSession: UUID?
    var scrubbing = false
    private var scaleMode: FilmVideoScale = .fit
    private var controlsRequested = true
    private var controlsHiddenByTap = false
    var openMenus = Set<ObjectIdentifier>()
    var singleTap: UITapGestureRecognizer!, doubleTap: UITapGestureRecognizer!
    var videoPan: UIPanGestureRecognizer!, hold: UILongPressGestureRecognizer!
    var panInteraction: FilmPlayerPanInteraction?
    var holdToken: UUID?
    private var gestureIdentity: FilmPlayerControlIdentity?
    private var gesturePreferences: FilmPlayerPreferences?
    var sliderIdentity: FilmPlayerControlIdentity?
    var brightnessScreen: UIScreen?
    var originalBrightness: CGFloat?
    var cancellingVideoGestures = false
    let feedback = UISelectionFeedbackGenerator()
    private var returnOrientation: UIInterfaceOrientation?
    var dismissing = false
    private var didNotifyDismiss = false
    private var lastControlsVisible: Bool?
    private static let playIcon = UIImage(systemName: "play.fill")
    private static let pauseIcon = UIImage(systemName: "pause.fill")
    private static let mutedIcon = UIImage(systemName: "speaker.slash.fill")
    private static let soundIcon = UIImage(systemName: "speaker.wave.2.fill")

    public init(model: FilmPlaybackModel, themeProvider: any PageThemeProviding) {
        self.model = model
        super.init(themeProvider: themeProvider)
        modalPresentationStyle = .fullScreen
        pageTitle = "播放"
    }

    /// Capture the page orientation before UIKit rotates its scene for playback.
    public func present(from presenter: UIViewController, animated: Bool = true, completion: (() -> Void)? = nil) {
        returnOrientation = presenter.viewIfLoaded?.window?.windowScene?.interfaceOrientation ??
            presenter.preferredInterfaceOrientationForPresentation
        presenter.present(self, animated: animated, completion: completion)
    }

    public override var usesPageHeader: Bool { false }
    public override var supportedInterfaceOrientations: UIInterfaceOrientationMask { .landscape }
    public override var preferredInterfaceOrientationForPresentation: UIInterfaceOrientation { .landscapeRight }
    public override var preferredStatusBarStyle: UIStatusBarStyle { .lightContent }
    public override var prefersStatusBarHidden: Bool { true }
    public override var prefersHomeIndicatorAutoHidden: Bool { top.alpha == 0 }

    public override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        setNeedsUpdateOfSupportedInterfaceOrientations()
    }

    public override func pageDidBecomeVisible() {
        model.setPresentationVisible(true, owner: presentationOwner)
        model.open()
        controlsRequested = true
        controlsHiddenByTap = false
        render()
    }

    public override func pageDidBecomeHidden() {
        model.setPresentationVisible(false, owner: presentationOwner)
        cancelVideoGestures()
        restoreBrightness()
        cancelAutoHide()
        settingsPanel.isHidden = true
        settings.isSelected = false
        openMenus.removeAll()
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        seek.minimumTrackTintColor = UIColor(theme.accent)
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.player"
        view.backgroundColor = .black
        video.backgroundColor = .black
        video.accessibilityIdentifier = "film.player.video"
        view.addSubview(video); video.pinEdges(to: view)
        activity.color = .white
        stateLabel.font = .systemFont(ofSize: 14)
        stateLabel.textColor = .white; stateLabel.textAlignment = .center; stateLabel.numberOfLines = 2
        stateLabel.accessibilityIdentifier = "film.player.state"
        let status = UIStackView(arrangedSubviews: [activity, stateLabel])
        status.axis = .vertical; status.spacing = 12
        status.isUserInteractionEnabled = false
        status.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(status)
        NSLayoutConstraint.activate([
            status.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            status.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            status.widthAnchor.constraint(lessThanOrEqualTo: view.safeAreaLayoutGuide.widthAnchor, multiplier: 0.7),
        ])
        configureTop()
        configureTransport()
        configureBottom()
        configureSettings()
        configureVideoGestures()
        let playerPresentation = Publishers.MergeMany([
            model.presentationStore.$snapshot.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.presentationStore.$presentation.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.presentationStore.$opening.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.presentationStore.$observationError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.presentationStore.$actionError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.presentationStore.$mediaStopError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.presentationStore.$sessionReleaseError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.presentationStore.$nativeStatus.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ]).eraseToAnyPublisher()
        observe(playerPresentation, whenVisible: true) { [weak self] in self?.render() }
        observe(model.mediaStore.didChange.eraseToAnyPublisher(), immediateOnMain: true, whenVisible: true) { [weak self] _ in
            self?.render()
        }
        render()
    }

    private func configureTop() {
        let close = FilmPlayerButton(type: .system)
        configure(close, title: "返回", id: "close", symbol: "chevron.left") { [weak self] in self?.dismissPlayer() }
        titleLabel.font = .systemFont(ofSize: 17)
        titleLabel.textColor = .white; titleLabel.numberOfLines = 1
        titleLabel.accessibilityIdentifier = "film.player.title"
        sourceLabel.font = .systemFont(ofSize: 12); sourceLabel.textColor = .lightGray
        sourceLabel.numberOfLines = 1; sourceLabel.accessibilityIdentifier = "film.player.source"
        let titles = UIStackView(arrangedSubviews: [titleLabel, sourceLabel])
        titles.axis = .vertical; titles.spacing = 3
        for child in [close, titles] { top.addArrangedSubview(child) }
        configureCapsule(top)
        top.spacing = 8
        top.accessibilityIdentifier = "film.player.top-controls"
        top.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(top)
        NSLayoutConstraint.activate([
            top.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 12),
            top.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -12),
            top.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8),
            top.heightAnchor.constraint(equalToConstant: 50),
        ])
    }

    private func configureTransport() {
        configure(previous, title: "上一集", id: "previous", symbol: "backward.end.fill") { [weak model] in model?.send("previous") }
        configure(play, title: "播放", id: "play-pause", symbol: "play.fill") { [weak model] in model?.pauseOrResume() }
        configure(nextButton, title: "下一集", id: "next", symbol: "forward.end.fill") { [weak model] in model?.send("next") }
        configure(sound, title: "音量", id: "volume-menu", symbol: "speaker.wave.2.fill") { [weak self] in
            guard let self else { return }
            settingsPanel.isHidden = false; settings.isSelected = true
            view.layoutIfNeeded()
            settingsPanel.scrollRectToVisible(volume.convert(volume.bounds, to: settingsPanel), animated: false)
        }
        let separator = UILabel()
        separator.text = "/"; separator.textColor = .lightGray
        separator.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        for label in [position, duration] {
            label.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
            label.textColor = .white
            label.setContentHuggingPriority(.required, for: .horizontal)
            label.setContentCompressionResistancePriority(.required, for: .horizontal)
        }
        let time = row([position, separator, duration])
        time.spacing = 4
        time.isLayoutMarginsRelativeArrangement = true
        time.directionalLayoutMargins = .init(top: 0, leading: 6, bottom: 0, trailing: 6)
        for child in [play, previous, nextButton, sound, time] { transport.addArrangedSubview(child) }
        configureCapsule(transport)
        transport.accessibilityIdentifier = "film.player.transport-controls"
    }

    private func configureBottom() {
        seek.accessibilityIdentifier = "film.player.seek"; seek.accessibilityLabel = "播放进度"
        seek.minimumTrackTintColor = UIColor(theme.accent)
        seek.maximumTrackTintColor = UIColor.white.withAlphaComponent(0.3)
        let thumbSize = CGSize(width: 12, height: 12)
        let thumb = UIGraphicsImageRenderer(size: thumbSize).image { context in
            UIColor.white.setFill()
            context.cgContext.fillEllipse(in: CGRect(origin: .zero, size: thumbSize))
        }
        seek.setThumbImage(thumb, for: .normal)
        seek.heightAnchor.constraint(equalToConstant: 44).isActive = true
        seek.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            sliderIdentity = model.controlIdentity
            scrubbing = sliderIdentity != nil; showControls()
        }, for: .touchDown)
        seek.addAction(UIAction { [weak self] _ in
            guard let self, scrubbing else { return }
            position.text = time(Double(seek.value))
            showControls()
        }, for: .valueChanged)
        seek.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            scrubbing = false
            if sliderIdentity == model.controlIdentity, sliderIdentity != nil { model.seek(to: Double(seek.value)) }
            sliderIdentity = nil; showControls()
        }, for: [.touchUpInside, .touchUpOutside])
        seek.addAction(UIAction { [weak self] _ in
            self?.sliderIdentity = nil; self?.scrubbing = false; self?.showControls()
        }, for: .touchCancel)
        configure(pan, title: "网盘", id: "pan", symbol: nil) {}
        pan.widthAnchor.constraint(lessThanOrEqualToConstant: 110).isActive = true
        pan.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        pan.menu = UIMenu(title: "网盘", children: FilmPanProvider.allCases.map { provider in
            UIAction(title: provider.title, identifier: .init(provider.rawValue)) { [weak model] _ in model?.selectPan(provider) }
        })
        configure(quality, title: "画质", id: "quality", symbol: nil) {}
        quality.widthAnchor.constraint(lessThanOrEqualToConstant: 74).isActive = true
        quality.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        quality.menu = UIMenu(title: "画质", children: FilmResourceQuality.allCases.map { quality in
            UIAction(title: quality.title, identifier: .init(quality.rawValue)) { [weak model] _ in model?.selectQuality(quality) }
        })
        configure(change, title: "换源", id: "switch", symbol: nil) { [weak model] in model?.send("switch") }
        configure(block, title: "片源错误", id: "block", symbol: nil) { [weak self] in self?.confirmBlock() }
        configure(skipChapter, title: "跳过片头", id: "skip-chapter", symbol: nil) { [weak model] in model?.skipChapter() }
        skipChapter.isHidden = true
        configure(pip, title: "画中画", id: "pip", symbol: "pip") { [weak model] in
            guard let model else { return }
            if model.mediaState.pictureInPicture { model.stopPictureInPicture() }
            else { model.startPictureInPicture() }
        }
        configure(settings, title: "设置", id: "settings", symbol: "gearshape.fill") { [weak self] in
            guard let self else { return }
            settingsPanel.isHidden.toggle()
            settings.isSelected = !settingsPanel.isHidden
        }
        for child in [pan, quality, change, block, pip, settings] { sourceControls.addArrangedSubview(child) }
        configureCapsule(sourceControls)
        sourceControls.accessibilityIdentifier = "film.player.source-controls"
        controlGroups.axis = .horizontal; controlGroups.alignment = .center
        controlGroups.distribution = .equalSpacing; controlGroups.spacing = 12
        controlGroups.addArrangedSubview(transport); controlGroups.addArrangedSubview(sourceControls)
        bottom.axis = .vertical; bottom.spacing = 0
        bottom.addArrangedSubview(seek); bottom.addArrangedSubview(controlGroups)
        bottom.accessibilityIdentifier = "film.player.bottom-controls"
        bottom.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(bottom)
        skipChapter.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(skipChapter)
        NSLayoutConstraint.activate([
            bottom.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 12),
            bottom.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -12),
            bottom.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -8),
            skipChapter.trailingAnchor.constraint(equalTo: bottom.trailingAnchor),
            skipChapter.bottomAnchor.constraint(equalTo: bottom.topAnchor, constant: -8),
            skipChapter.topAnchor.constraint(greaterThanOrEqualTo: top.bottomAnchor, constant: 8),
        ])
    }

    public override func viewWillLayoutSubviews() {
        super.viewWillLayoutSubviews()
        let width = transport.systemLayoutSizeFitting(UIView.layoutFittingCompressedSize).width +
            sourceControls.systemLayoutSizeFitting(UIView.layoutFittingCompressedSize).width + 12
        let available = view.bounds.width - view.safeAreaInsets.left - view.safeAreaInsets.right - 24
        let axis: NSLayoutConstraint.Axis = width <= available ? .horizontal : .vertical
        guard controlGroups.axis != axis else { return }
        controlGroups.axis = axis
        controlGroups.alignment = axis == .horizontal ? .center : .trailing
        controlGroups.distribution = axis == .horizontal ? .equalSpacing : .fill
    }

    private func configureSettings() {
        configure(rate, title: "1×", id: "rate", symbol: nil) {}
        updateRateMenu()
        configure(scale, title: "比例", id: "scale", symbol: nil) {}
        scale.menu = UIMenu(title: "比例", children: FilmVideoScale.allCases.map { mode in
            UIAction(title: mode.title, identifier: .init(mode.rawValue)) { [weak self] _ in
                guard let self else { return }
                scaleMode = mode; model.setVideoScale(mode); render()
            }
        })
        volume.accessibilityIdentifier = "film.player.volume"; volume.accessibilityLabel = "播放音量"
        volume.heightAnchor.constraint(equalToConstant: 44).isActive = true
        volume.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            model.setVolume(Double(volume.value))
        }, for: .valueChanged)
        configure(shortVideo, title: "短视频", id: "short-video", symbol: nil) { [weak model] in
            guard let model, let snapshot = model.snapshot else { return }
            model.setShortVideo(!snapshot.shortVideo)
        }
        configure(retryMedia, title: "重试地址", id: "retry-media", symbol: nil) { [weak model] in model?.retryMedia() }
        let content = UIStackView(arrangedSubviews: [
            settingRow("倍速", rate), settingRow("比例", scale), settingRow("音量", volume),
            row([shortVideo, retryMedia], equal: true),
        ])
        content.axis = .vertical; content.spacing = 4
        content.isLayoutMarginsRelativeArrangement = true
        content.directionalLayoutMargins = .init(top: 8, leading: 12, bottom: 8, trailing: 12)
        content.translatesAutoresizingMaskIntoConstraints = false
        settingsPanel.backgroundColor = UIColor(white: 0.1, alpha: 1)
        settingsPanel.layer.cornerRadius = 12
        settingsPanel.isHidden = true
        settingsPanel.accessibilityIdentifier = "film.player.settings-panel"
        settingsPanel.translatesAutoresizingMaskIntoConstraints = false
        settingsPanel.addSubview(content); view.addSubview(settingsPanel)
        let height = settingsPanel.heightAnchor.constraint(equalTo: content.heightAnchor)
        height.priority = .defaultHigh
        NSLayoutConstraint.activate([
            settingsPanel.topAnchor.constraint(greaterThanOrEqualTo: top.bottomAnchor, constant: 8),
            settingsPanel.trailingAnchor.constraint(equalTo: bottom.trailingAnchor),
            settingsPanel.widthAnchor.constraint(equalToConstant: 280),
            settingsPanel.widthAnchor.constraint(lessThanOrEqualTo: view.safeAreaLayoutGuide.widthAnchor, constant: -24),
            settingsPanel.bottomAnchor.constraint(equalTo: bottom.topAnchor, constant: -4),
            height,
            content.leadingAnchor.constraint(equalTo: settingsPanel.contentLayoutGuide.leadingAnchor),
            content.trailingAnchor.constraint(equalTo: settingsPanel.contentLayoutGuide.trailingAnchor),
            content.topAnchor.constraint(equalTo: settingsPanel.contentLayoutGuide.topAnchor),
            content.bottomAnchor.constraint(equalTo: settingsPanel.contentLayoutGuide.bottomAnchor),
            content.widthAnchor.constraint(equalTo: settingsPanel.frameLayoutGuide.widthAnchor),
        ])
    }

    private func confirmBlock() {
        guard let snapshot = model.snapshot,
              let attemptID = snapshot.current?.id ?? snapshot.attempt?.id else { return }
        presentPageConfirmation(
            title: "片源错误",
            message: snapshot.work.title,
            confirmTitle: "屏蔽并换源",
            destructive: true,
            onConfirm: { [weak self, weak model] in
                model?.send("block", parameters: ["attemptId": .string(attemptID)])
                self?.showControls()
            },
            onCancel: { [weak self] in
                self?.showControls()
            }
        )
    }

    func render() {
        guard isViewLoaded else { return }
        if gestureIdentity != model.controlIdentity ||
            gesturePreferences?.hasSameGestureSettings(as: model.playerPreferences) != true {
            gestureIdentity = model.controlIdentity
            gesturePreferences = model.playerPreferences
            cancelVideoGestures()
        }
        if videoSession != model.sessionID, let surface = model.makeVideoView() {
            videoView?.removeFromSuperview(); videoView = surface; videoSession = model.sessionID
            video.insertSubview(surface, at: 0); surface.pinEdges(to: video)
            model.setVideoScale(scaleMode)
        }
        let state = model.mediaState, snapshot = model.snapshot
        let kind = snapshot?.work.kind
        let nextTitle = model.presentation?.work.title ?? snapshot?.work.title
        if titleLabel.text != nextTitle { titleLabel.text = nextTitle }
        let activeAttempt = model.executionStage == nil ? nil : (snapshot?.current ?? snapshot?.attempt)
        let nextSource: String? = if let snap = snapshot, model.executionStage != nil {
            [snap.activeSourceLabel, snap.activeEpisodeLabel].filter { !$0.isEmpty }.joined(separator: " · ")
        } else {
            nil
        }
        if sourceLabel.text != nextSource { sourceLabel.text = nextSource }
        let playing = model.canControlPlayback
        play.isEnabled = playing || model.canStartPlayback
        let paused = !playing || state.paused
        setImage(paused ? Self.playIcon : Self.pauseIcon, on: play)
        play.accessibilityLabel = paused ? "播放" : "暂停"
        let maxSeek = Float(state.duration)
        if let preview = panInteraction?.previewPosition {
            if seek.maximumValue != maxSeek { seek.maximumValue = maxSeek }
            let previewValue = Float(preview)
            if seek.value != previewValue { seek.value = previewValue }
        } else if !scrubbing {
            if seek.maximumValue != maxSeek { seek.maximumValue = maxSeek }
            let positionValue = Float(state.position)
            if seek.value != positionValue { seek.value = positionValue }
        }
        seek.isEnabled = playing && state.duration > 0
        let positionText = time(panInteraction?.previewPosition ?? (scrubbing ? Double(seek.value) : state.position))
        if position.text != positionText { position.text = positionText }
        let durationText = time(state.duration)
        if duration.text != durationText { duration.text = durationText }
        let volumeValue = Float(state.volume)
        if volume.value != volumeValue { volume.value = volumeValue }
        volume.isEnabled = playing
        sound.isEnabled = playing
        setImage(state.volume == 0 ? Self.mutedIcon : Self.soundIcon, on: sound)
        sound.accessibilityValue = "\(Int(state.volume * 100))%"
        let rateTitle = "\(state.rate)×"
        setTitle(rateTitle, on: rate); rate.isEnabled = playing
        rate.accessibilityValue = rateTitle
        updateRateMenu()
        selectMenu(rate, value: String(state.rate))
        setTitle(scaleMode.title, on: scale); selectMenu(scale, value: scaleMode.rawValue)
        pip.isEnabled = state.pictureInPicture || playing && state.pictureInPictureAvailable
        pip.isSelected = state.pictureInPicture
        pip.accessibilityLabel = state.pictureInPicture ? "退出画中画" : "画中画"
        previous.isEnabled = (snapshot?.canPrevious == true) && playing
        nextButton.isEnabled = (snapshot?.canNext == true) && playing
        shortVideo.isSelected = snapshot?.shortVideo == true
        setTitle(shortVideo.isSelected ? "短视频：开" : "短视频", on: shortVideo)
        shortVideo.isEnabled = model.executionStage != nil && kind != .movie && model.playbackStatus != .unavailable
        if let segment = model.skipSegment {
            let skipTitle = segment.kind == .intro ? "跳过片头" : "跳过片尾"
            setTitle(skipTitle, on: skipChapter)
            skipChapter.accessibilityLabel = skipTitle
            skipChapter.isHidden = false
        } else { skipChapter.isHidden = true }
        let selected = model.executionStage != nil && activeAttempt != nil && snapshot?.noSources != true
        pan.isEnabled = selected; quality.isEnabled = selected
        change.isEnabled = snapshot?.canSwitchSource == true
        block.isEnabled = snapshot?.canBlockSource == true
        retryMedia.isEnabled = model.canRetryMedia
        let panTitle = (snapshot?.activePan).flatMap(FilmPanProvider.init(rawValue:))?.title ??
            activeAttempt?.provider.flatMap(FilmPanProvider.init(rawValue:))?.title ?? "网盘"
        let qualityTitle = (snapshot?.activeQuality).flatMap(FilmResourceQuality.init(rawValue:))?.title ??
            (activeAttempt?.quality.isEmpty == false ? activeAttempt!.quality : "画质")
        setTitle(panTitle, on: pan)
        setTitle(qualityTitle, on: quality)
        pan.accessibilityValue = panTitle; quality.accessibilityValue = qualityTitle
        scale.accessibilityValue = scaleMode.title
        selectMenu(pan, value: snapshot?.activePan.isEmpty == false ? snapshot?.activePan : activeAttempt?.provider)
        selectMenu(quality, value: snapshot?.activeQuality)
        let busy = model.playbackStatus.busy
        if busy { activity.startAnimating() } else { activity.stopAnimating() }
        let nextStateText = model.playbackStatus == .playing ? nil : model.playbackStatus.title
        if stateLabel.text != nextStateText { stateLabel.text = nextStateText }
        let suppress = { (err: FilmPlaybackError?) -> FilmPlaybackError? in
            guard let err else { return nil }
            if err.code == "NO_VIDEO_SOURCES" { return nil }
            if err.code == "NOT_CONFIGURED", snapshot?.noSources == true { return nil }
            return err
        }
        let reported = suppress(snapshot?.reportError.map { FilmPlaybackError(code: $0) })
        let session = suppress(snapshot?.error.map { FilmPlaybackError(code: $0) })
        let actionErr = suppress(model.actionError)
        if let error = actionErr ?? model.observationError ?? reported ?? session {
            let token = AnyHashable(String(describing: error))
            if actionErr == nil, model.observationError != nil {
                syncErrorSource("playback", message: error.localizedDescription, token: token, retry: { [weak model] in
                    model?.reconnect()
                })
            } else {
                syncErrorSource("playback", message: error.localizedDescription, token: token, acknowledged: { [weak self, weak model] in
                    self?.clearErrorSource("playback")
                    model?.acknowledgeError()
                })
            }
        } else {
            clearErrorSource("playback")
        }
        if keepsControlsVisible && !controlsHiddenByTap { controlsRequested = true }
        updateControls()
    }

    private var keepsControlsVisible: Bool {
        model.mediaState.paused || activity.isAnimating || model.mediaState.attemptID == nil ||
            scrubbing || panInteraction != nil || holdToken != nil ||
            !settingsPanel.isHidden || !openMenus.isEmpty || presentedViewController != nil ||
            model.actionError != nil || model.observationError != nil || model.snapshot?.error != nil ||
            UIAccessibility.isVoiceOverRunning
    }

    func showControls() {
        controlsRequested = true
        controlsHiddenByTap = false
        cancelAutoHide()
        updateControls()
    }

    private func updateControls() {
        let visible = !controlsHiddenByTap && (controlsRequested || keepsControlsVisible)
        skipChapter.isHidden = model.skipSegment == nil || !settingsPanel.isHidden
        for overlay in [top, bottom, skipChapter] {
            overlay.alpha = visible ? 1 : 0
            overlay.isUserInteractionEnabled = visible
            overlay.accessibilityElementsHidden = !visible
        }
        if lastControlsVisible != visible {
            lastControlsVisible = visible
            setNeedsUpdateOfHomeIndicatorAutoHidden()
        }
        if keepsControlsVisible || !visible || !isPageVisible {
            cancelAutoHide()
        } else if !hasActivePageTask(key: "hideControls") {
            runPageTask(key: "hideControls") { [weak self] in
                do { try await Task.sleep(for: .milliseconds(2200)) } catch { return }
                guard let self, self.isPageVisible, !self.keepsControlsVisible else { return }
                self.controlsRequested = false
                self.updateControls()
            }
        }
    }

    private func cancelAutoHide() { cancelPageTask(key: "hideControls") }

    @objc func toggleControls() {
        if !settingsPanel.isHidden {
            settingsPanel.isHidden = true; settings.isSelected = false
            showControls()
        } else {
            controlsHiddenByTap = top.alpha != 0
            controlsRequested = !controlsHiddenByTap
            cancelAutoHide(); updateControls()
        }
    }

    private func updateRateMenu() {
        let values = model.availableRates
        let current = rate.menu?.children.compactMap { ($0 as? UIAction)?.identifier.rawValue } ?? []
        guard current != values.map({ String($0) }) else { return }
        rate.menu = UIMenu(title: "倍速", children: values.map { value in
            UIAction(title: "\(value)×", identifier: .init(String(value))) { [weak model] _ in model?.setRate(value) }
        })
    }


    public override func present(_ viewControllerToPresent: UIViewController, animated flag: Bool, completion: (() -> Void)? = nil) {
        cancelVideoGestures()
        cancelAutoHide()
        controlsRequested = true
        controlsHiddenByTap = false
        super.present(viewControllerToPresent, animated: flag, completion: completion)
        updateControls()
    }

    private func dismissPlayer() {
        guard !dismissing else { return }
        cancelVideoGestures()
        restoreBrightness()
        dismissing = true; cancelAutoHide()
        Task { [weak self] in
            guard let self else { return }
            await model.close(allowSystemOwner: true)
            guard model.canLeave || model.hasSystemOwner else {
                dismissing = false; render()
                return
            }
            let presenter = presentingViewController
            let orientation = returnOrientation
            let finish: () -> Void = { [weak self, weak presenter] in
                if let presenter, let orientation {
                    presenter.setNeedsUpdateOfSupportedInterfaceOrientations()
                    let mask: UIInterfaceOrientationMask?
                    switch orientation {
                    case .portrait: mask = .portrait
                    case .portraitUpsideDown: mask = .portraitUpsideDown
                    case .landscapeLeft: mask = .landscapeLeft
                    case .landscapeRight: mask = .landscapeRight
                    default: mask = nil
                    }
                    if let mask {
                        presenter.viewIfLoaded?.window?.windowScene?.requestGeometryUpdate(.iOS(
                            interfaceOrientations: mask
                        ))
                    }
                }
                self?.notifyDismiss()
            }
            dismissSelf(animated: true, completion: finish)
        }
    }

    public override func pageDidBecomeInactive() {
        cancelVideoGestures()
        restoreBrightness()
        cancelAutoHide()
        guard !dismissing else { return }
        let model = self.model
        let dismissCallback: (() -> Void)?
        if didNotifyDismiss {
            dismissCallback = nil
        } else {
            didNotifyDismiss = true
            dismissCallback = onDismiss
            onDismiss = nil
        }
        Task {
            await model.close(allowSystemOwner: true)
            dismissCallback?()
        }
    }

    private func notifyDismiss() {
        guard !didNotifyDismiss else { return }
        didNotifyDismiss = true
        onDismiss?()
    }

    private func time(_ value: Double) -> String {
        let seconds = value.isFinite && value >= 0 ? Int(min(value, Double(Int32.max))) : 0
        return seconds >= 3600 ? String(format: "%d:%02d:%02d", seconds / 3600, seconds / 60 % 60, seconds % 60) :
            String(format: "%d:%02d", seconds / 60, seconds % 60)
    }

    private func row(_ children: [UIView], equal: Bool = false) -> UIStackView {
        let stack = UIStackView(arrangedSubviews: children)
        stack.axis = .horizontal; stack.alignment = .center; stack.spacing = 8
        if equal { stack.distribution = .fillEqually }
        return stack
    }

    private func configureCapsule(_ stack: UIStackView) {
        stack.axis = .horizontal; stack.alignment = .center; stack.spacing = 4
        stack.isLayoutMarginsRelativeArrangement = true
        stack.directionalLayoutMargins = .init(top: 3, leading: 6, bottom: 3, trailing: 6)
        stack.backgroundColor = UIColor(white: 20.0 / 255, alpha: 0.45)
        stack.layer.cornerRadius = 25
        stack.setContentHuggingPriority(.required, for: .horizontal)
    }

    private func settingRow(_ title: String, _ control: UIView) -> UIStackView {
        let label = UILabel()
        label.text = title; label.textColor = .lightGray; label.font = .systemFont(ofSize: 14)
        label.widthAnchor.constraint(equalToConstant: 44).isActive = true
        let stack = row([label, control])
        stack.heightAnchor.constraint(equalToConstant: 44).isActive = true
        return stack
    }

    private func setTitle(_ title: String, on button: UIButton) {
        if button.configuration?.title != title {
            button.configuration?.title = title
        }
    }

    private func setImage(_ image: UIImage?, on button: UIButton) {
        if button.configuration?.image !== image {
            button.configuration?.image = image
        }
    }

    private func selectMenu(_ button: UIButton, value: String?) {
        for case let action as UIAction in button.menu?.children ?? [] {
            let next: UIMenuElement.State = action.identifier.rawValue == value ? .on : .off
            if action.state != next { action.state = next }
        }
    }

    private func configure(_ button: UIButton, title: String, id: String, symbol: String?, action: @escaping () -> Void) {
        button.tintColor = .white
        var configuration = UIButton.Configuration.plain()
        configuration.cornerStyle = .capsule
        configuration.baseForegroundColor = .white
        configuration.contentInsets = .init(top: 0, leading: symbol == nil ? 10 : 5, bottom: 0, trailing: symbol == nil ? 10 : 5)
        configuration.background.backgroundInsets = .init(top: 5, leading: symbol == nil ? 0 : 5, bottom: 5, trailing: symbol == nil ? 0 : 5)
        configuration.background.strokeWidth = 1
        configuration.titleLineBreakMode = .byTruncatingTail
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var attributes = $0
            attributes.font = .systemFont(ofSize: 12)
            return attributes
        }
        if let symbol {
            configuration.image = UIImage(systemName: symbol)
            configuration.preferredSymbolConfigurationForImage = UIImage.SymbolConfiguration(pointSize: 16, weight: .medium)
            button.widthAnchor.constraint(equalToConstant: 44).isActive = true
        } else { configuration.title = title }
        button.configuration = configuration
        button.configurationUpdateHandler = { button in
            var configuration = button.configuration!
            let emphasized = button.isHighlighted || button.isSelected
            configuration.baseForegroundColor = UIColor.white.withAlphaComponent(button.isEnabled ? 0.92 : 0.38)
            configuration.background.backgroundColor = UIColor.white.withAlphaComponent(emphasized ? 0.14 : 0.06)
            configuration.background.strokeColor = UIColor.white.withAlphaComponent(emphasized ? 0.28 : 0.08)
            button.configuration = configuration
        }
        button.accessibilityIdentifier = "film.player.\(id)"; button.accessibilityLabel = title
        button.heightAnchor.constraint(equalToConstant: 44).isActive = true
        button.widthAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        button.addAction(UIAction { [weak self] _ in
            action(); self?.showControls()
        }, for: .touchUpInside)
        if let menu = button as? FilmPlayerMenuButton {
            menu.showsMenuAsPrimaryAction = true
            menu.onMenuVisibility = { [weak self, weak menu] visible in
                guard let self, let menu else { return }
                if visible { openMenus.insert(ObjectIdentifier(menu)) }
                else { openMenus.remove(ObjectIdentifier(menu)) }
                showControls()
            }
        }
    }
}

/// Keep the 44-point hit bounds independent of an SF Symbol's optical alignment.
@MainActor
private class FilmPlayerButton: UIButton {
    override var alignmentRectInsets: UIEdgeInsets { .zero }
}

/// Native menus remain open independently of the video overlay's idle timer.
@MainActor
private final class FilmPlayerMenuButton: FilmPlayerButton {
    var onMenuVisibility: ((Bool) -> Void)?

    override func contextMenuInteraction(_ interaction: UIContextMenuInteraction,
                                        willDisplayMenuFor configuration: UIContextMenuConfiguration,
                                        animator: (any UIContextMenuInteractionAnimating)?) {
        super.contextMenuInteraction(interaction, willDisplayMenuFor: configuration, animator: animator)
        onMenuVisibility?(true)
    }

    override func contextMenuInteraction(_ interaction: UIContextMenuInteraction,
                                        willEndFor configuration: UIContextMenuConfiguration,
                                        animator: (any UIContextMenuInteractionAnimating)?) {
        super.contextMenuInteraction(interaction, willEndFor: configuration, animator: animator)
        animator?.addCompletion { [weak self] in self?.onMenuVisibility?(false) }
        if animator == nil { onMenuVisibility?(false) }
    }
}
#endif
