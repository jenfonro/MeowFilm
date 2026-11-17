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
    private let model: FilmPlaybackModel
    private let video = UIView()
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
    private let settingsPanel = UIScrollView()
    private var videoView: UIView?
    private var videoSession: UUID?
    private var scrubbing = false
    private var scaleMode: FilmVideoScale = .fit
    private var controlsRequested = true
    private var controlsHiddenByTap = false
    private var openMenus = Set<ObjectIdentifier>()
    private var hideTask: Task<Void, Never>?
    private var shownError: FilmPlaybackError?
    private var singleTap: UITapGestureRecognizer!, doubleTap: UITapGestureRecognizer!
    private var videoPan: UIPanGestureRecognizer!, hold: UILongPressGestureRecognizer!
    private var panInteraction: FilmPlayerPanInteraction?
    private var holdToken: UUID?
    private var gestureIdentity: FilmPlayerControlIdentity?
    private var gesturePreferences: FilmPlayerPreferences?
    private var sliderIdentity: FilmPlayerControlIdentity?
    private var brightnessScreen: UIScreen?
    private var originalBrightness: CGFloat?
    private var inactiveObservation: AnyCancellable?
    private var cancellingVideoGestures = false
    private let feedback = UISelectionFeedbackGenerator()
    private var returnOrientation: UIInterfaceOrientation?
    private var dismissing = false
    private var didNotifyDismiss = false

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

    public override func pageDidFinishAppearing() {
        super.pageDidFinishAppearing()
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

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.player"
        view.backgroundColor = .black
        pageHeader.isHidden = true
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
        inactiveObservation = NotificationCenter.default.publisher(for: UIApplication.willResignActiveNotification)
            .sink { [weak self] _ in
                self?.cancelVideoGestures()
                self?.restoreBrightness()
            }
        observe(model) { [weak self] in self?.render() }
        render()
    }

    private func configureTop() {
        let close = control("返回", id: "close", symbol: "chevron.left") { [weak self] in self?.dismissPlayer() }
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
        seek.minimumTrackTintColor = UIColor(red: 1, green: 0, blue: 0.25, alpha: 1)
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
        let change = control("换源", id: "switch") { [weak model] in model?.send("switch") }
        let block = control("片源错误", id: "block") { [weak self] in self?.confirmBlock() }
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
        let retry = control("重试地址", id: "retry-media") { [weak model] in model?.retryMedia() }
        let content = UIStackView(arrangedSubviews: [
            settingRow("倍速", rate), settingRow("比例", scale), settingRow("音量", volume),
            row([shortVideo, retry], equal: true),
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
              let attemptID = snapshot.current?.attemptID ?? snapshot.attempt?.id else { return }
        let alert = UIAlertController(title: "片源错误", message: snapshot.target.title, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel) { [weak self, weak alert] _ in
            guard let self, let alert else { return }
            dismissPageModal(alert) { [weak self] in self?.showControls() }
        })
        alert.addAction(UIAlertAction(title: "屏蔽并换源", style: .destructive) { [weak self, weak model, weak alert] _ in
            model?.send("block", parameters: ["attemptId": .string(attemptID)])
            guard let self, let alert else { return }
            dismissPageModal(alert) { [weak self] in self?.showControls() }
        })
        present(alert, animated: true)
    }

    private func render() {
        guard isViewLoaded else { return }
        if gestureIdentity != model.controlIdentity || gesturePreferences != model.playerPreferences {
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
        let kind = snapshot.map { value in
            value.metadataSources.first { $0.id == value.metadataSourceID }?.kind ?? value.target.kind
        }
        titleLabel.text = model.presentation?.target.title
        let candidate = model.executionStage == nil ? nil : snapshot?.current?.candidate ?? snapshot?.attempt?.candidate
        sourceLabel.text = candidate.map { [$0.source.name, $0.lineLabel, $0.name].filter { !$0.isEmpty }.joined(separator: " · ") }
        let playing = state.attemptID != nil
        play.isEnabled = playing || snapshot != nil
        play.configuration?.image = UIImage(systemName: state.paused ? "play.fill" : "pause.fill")
        play.accessibilityLabel = state.paused ? "播放" : "暂停"
        if let preview = panInteraction?.previewPosition {
            seek.maximumValue = Float(state.duration)
            seek.value = Float(preview)
        } else if !scrubbing {
            seek.maximumValue = Float(state.duration)
            seek.value = Float(state.position)
        }
        seek.isEnabled = playing && state.duration > 0
        position.text = time(panInteraction?.previewPosition ?? state.position); duration.text = time(state.duration)
        volume.value = Float(state.volume); volume.isEnabled = playing
        sound.isEnabled = playing
        sound.configuration?.image = UIImage(systemName: state.volume == 0 ? "speaker.slash.fill" : "speaker.wave.2.fill")
        sound.accessibilityValue = "\(Int(state.volume * 100))%"
        rate.configuration?.title = "\(state.rate)×"; rate.isEnabled = playing
        rate.accessibilityValue = "\(state.rate)×"
        updateRateMenu()
        selectMenu(rate, value: String(state.rate))
        scale.configuration?.title = scaleMode.title; selectMenu(scale, value: scaleMode.rawValue)
        pip.isEnabled = state.pictureInPictureAvailable
        pip.isSelected = state.pictureInPicture
        pip.accessibilityLabel = state.pictureInPicture ? "退出画中画" : "画中画"
        previous.isEnabled = kind != .movie && playing
        nextButton.isEnabled = kind != .movie && playing
        shortVideo.isSelected = snapshot?.shortVideo == true
        shortVideo.configuration?.title = shortVideo.isSelected ? "短视频：开" : "短视频"
        shortVideo.isEnabled = snapshot != nil && kind != .movie
        if let segment = model.skipSegment {
            skipChapter.configuration?.title = segment.kind == .intro ? "跳过片头" : "跳过片尾"
            skipChapter.accessibilityLabel = skipChapter.configuration?.title
            skipChapter.isHidden = false
        } else { skipChapter.isHidden = true }
        pan.configuration?.title = candidate?.provider.flatMap(FilmPanProvider.init(rawValue:))?.title ?? candidate?.lineLabel ?? "网盘"
        quality.configuration?.title = candidate?.quality.flatMap(FilmResourceQuality.init(rawValue:))?.title ?? candidate?.quality ?? "画质"
        pan.accessibilityValue = pan.configuration?.title; quality.accessibilityValue = quality.configuration?.title
        scale.accessibilityValue = scaleMode.title
        selectMenu(pan, value: candidate?.provider); selectMenu(quality, value: candidate?.quality)
        let busy = state.buffering || ["loading", "searching", "resolving", "pending"].contains(model.executionStage ?? "")
        if busy { activity.startAnimating() } else { activity.stopAnimating() }
        stateLabel.text = busy ? "准备中" : model.executionStage == "ended" ? "播放结束" : nil
        let reported = snapshot?.reportError.map { FilmPlaybackError(code: $0) }
        let metadata = snapshot?.metadataError.map { FilmPlaybackError(code: $0) }
        let session = snapshot?.error.map { FilmPlaybackError(code: $0) }
        if model.actionError == nil, model.observationError == nil, reported == nil, metadata == nil, session == nil {
            shownError = nil
        }
        if let error = model.actionError ?? model.observationError ?? reported ?? metadata ?? session,
           error != shownError, canPresentPageAlert {
            let presented: Bool
            if model.actionError == nil, model.observationError != nil {
                presented = showRetryableError(error.localizedDescription, retry: { [weak model] in model?.reconnect() })
            } else if model.actionError == nil, model.observationError == nil, reported == nil, metadata != nil {
                presented = showRetryableError(error.localizedDescription, retry: { [weak model] in model?.refreshBrowsing() })
            } else {
                presented = showError(error.localizedDescription, acknowledged: { [weak model] in model?.acknowledgeError() })
            }
            if presented { shownError = error }
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

    private func showControls() {
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
        setNeedsUpdateOfHomeIndicatorAutoHidden()
        if keepsControlsVisible || !visible || !isPageVisible {
            cancelAutoHide()
        } else if hideTask == nil {
            hideTask = Task { [weak self] in
                do { try await Task.sleep(for: .milliseconds(2200)) } catch { return }
                guard let self else { return }
                hideTask = nil
                guard isPageVisible, !keepsControlsVisible else { return }
                controlsRequested = false
                updateControls()
            }
        }
    }

    private func cancelAutoHide() { hideTask?.cancel(); hideTask = nil }

    @objc private func toggleControls() {
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

    private func configureVideoGestures() {
        singleTap = UITapGestureRecognizer(target: self, action: #selector(tapVideo(_:)))
        doubleTap = UITapGestureRecognizer(target: self, action: #selector(tapVideo(_:)))
        doubleTap.numberOfTapsRequired = 2
        videoPan = UIPanGestureRecognizer(target: self, action: #selector(panVideo(_:)))
        videoPan.maximumNumberOfTouches = 1
        hold = UILongPressGestureRecognizer(target: self, action: #selector(holdVideo(_:)))
        hold.minimumPressDuration = 0.4
        singleTap.require(toFail: doubleTap)
        for tap in [singleTap!, doubleTap!] {
            tap.require(toFail: videoPan)
            tap.require(toFail: hold)
        }
        for gesture in [singleTap!, doubleTap!, videoPan!, hold!] as [UIGestureRecognizer] {
            gesture.delegate = self
            video.addGestureRecognizer(gesture)
        }
    }

    public func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldReceive touch: UITouch) -> Bool {
        guard canUseVideoGestures else { return false }
        let point = touch.location(in: video)
        // Leave screen edges to the system; reject overlay/control hit bounds,
        // rather than only rejecting the UILabel or image that received a touch.
        let horizontalInset = max(20, max(video.safeAreaInsets.left, video.safeAreaInsets.right))
        guard point.x > horizontalInset, point.x < video.bounds.width - horizontalInset,
              point.y > 20, point.y < video.bounds.height - 20 else { return false }
        if let hit = view.hitTest(touch.location(in: view), with: nil) {
            guard hit === video || hit.isDescendant(of: video) else { return false }
            var ancestor: UIView? = hit
            while let current = ancestor, current !== video {
                if current is UIControl { return false }
                ancestor = current.superview
            }
        }
        return true
    }

    public func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard canUseVideoGestures else { return false }
        if !settingsPanel.isHidden { return gestureRecognizer === singleTap }
        if gestureRecognizer === hold || gestureRecognizer === videoPan {
            guard model.controlIdentity != nil else { return false }
        }
        if gestureRecognizer === videoPan {
            let preferences = model.playerPreferences
            return preferences.swipeSeek || preferences.swipeBrightness || preferences.swipeVolume ||
                preferences.swipeEpisodes && model.snapshot?.shortVideo == true
        }
        return true
    }

    private var canUseVideoGestures: Bool {
        isPageVisible && UIApplication.shared.applicationState == .active && !dismissing &&
            openMenus.isEmpty && presentedViewController == nil
    }

    @objc private func tapVideo(_ gesture: UITapGestureRecognizer) {
        guard gesture.state == .ended, canUseVideoGestures else { return }
        if !settingsPanel.isHidden { toggleControls(); return }
        let region = FilmPlayerScreenRegion(x: Double(gesture.location(in: video).x), width: Double(video.bounds.width))
        let preferences = model.playerPreferences
        let isDouble = gesture === doubleTap
        let action: FilmPlayerTapAction
        if isDouble { action = region == .center ? preferences.doubleTapCenter : preferences.doubleTapSides }
        else { action = region == .center ? preferences.singleTapCenter : preferences.singleTapSides }
        switch action {
        case .none: break
        case .toggleControls: if !isDouble { toggleControls() }
        case .playPause: model.pauseOrResume(); showControls()
        case .seek:
            guard region != .center else { return }
            model.seekByStep(forward: region == .right); showControls()
        }
    }

    @objc private func holdVideo(_ gesture: UILongPressGestureRecognizer) {
        switch gesture.state {
        case .began:
            guard canUseVideoGestures, settingsPanel.isHidden else { return }
            holdToken = model.beginTemporaryRate()
            if holdToken != nil, model.playerPreferences.hapticHold { feedback.selectionChanged() }
            showControls()
        case .ended, .cancelled, .failed:
            if let holdToken { model.endTemporaryRate(holdToken) }
            holdToken = nil
            render()
        default: break
        }
    }

    @objc private func panVideo(_ gesture: UIPanGestureRecognizer) {
        let translation = gesture.translation(in: video)
        switch gesture.state {
        case .began:
            guard canUseVideoGestures, settingsPanel.isHidden, let identity = model.controlIdentity,
                  let screen = view.window?.screen else { return }
            let state = model.mediaState
            panInteraction = FilmPlayerPanInteraction(identity: identity, preferences: model.playerPreferences,
                region: .init(x: Double(gesture.location(in: video).x - translation.x), width: Double(video.bounds.width)),
                shortVideo: model.snapshot?.shortVideo == true,
                initialPosition: state.position, duration: state.duration,
                initialVolume: state.volume, initialBrightness: Double(screen.brightness))
            updatePan(translation: translation)
            showControls()
        case .changed:
            guard canUseVideoGestures, settingsPanel.isHidden, panInteraction?.identity == model.controlIdentity else {
                cancelVideoGestures(); return
            }
            updatePan(translation: translation)
        case .ended:
            updatePan(translation: translation)
            guard var interaction = panInteraction else { return }
            panInteraction = nil
            switch interaction.finish(cancelled: !canUseVideoGestures || !settingsPanel.isHidden, current: model.controlIdentity) {
            case .seek(let position): model.seek(to: position)
            case .episode(let next):
                if model.snapshot?.shortVideo == true, model.playerPreferences.swipeEpisodes {
                    model.send(next ? "next" : "previous")
                }
            case nil: break
            }
            render()
        case .cancelled, .failed:
            panInteraction = nil
            render()
        default: break
        }
    }

    private func updatePan(translation: CGPoint) {
        guard var interaction = panInteraction, interaction.identity == model.controlIdentity else { return }
        let previousMode = interaction.mode
        let change = interaction.update(x: Double(translation.x), y: Double(translation.y),
                                        width: Double(video.bounds.width), height: Double(video.bounds.height))
        // Publish the locked operation before native volume emits a state update.
        panInteraction = interaction
        if previousMode == nil {
            let preferences = interaction.preferences
            let haptic: Bool
            switch interaction.mode {
            case .seek: haptic = preferences.hapticSeek
            case .volume: haptic = preferences.hapticVolume
            case .brightness: haptic = preferences.hapticBrightness
            default: haptic = false
            }
            if haptic { feedback.selectionChanged() }
        }
        switch change {
        case .seek:
            render()
        case .volume(let volume):
            model.setVolume(volume)
        case .brightness(let brightness):
            guard let screen = view.window?.screen else { return }
            if brightnessScreen !== screen {
                restoreBrightness()
                brightnessScreen = screen
                originalBrightness = screen.brightness
            }
            screen.brightness = CGFloat(brightness)
        case nil: break
        }
    }

    private func cancelVideoGestures() {
        guard !cancellingVideoGestures else { return }
        cancellingVideoGestures = true
        defer { cancellingVideoGestures = false }
        holdToken = nil; panInteraction = nil; sliderIdentity = nil; scrubbing = false
        model.cancelTemporaryRate()
        for gesture in [singleTap, doubleTap, videoPan, hold] as [UIGestureRecognizer?] {
            guard let gesture else { continue }
            gesture.isEnabled = false
            gesture.isEnabled = true
        }
    }

    private func restoreBrightness() {
        if let brightnessScreen, let originalBrightness { brightnessScreen.brightness = originalBrightness }
        brightnessScreen = nil; originalBrightness = nil
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
                    presenter.viewIfLoaded?.window?.windowScene?.requestGeometryUpdate(.iOS(
                        interfaceOrientations: UIInterfaceOrientationMask(rawValue: 1 << orientation.rawValue)
                    ))
                }
                self?.notifyDismiss()
            }
            if let presenter = presenter as? PageViewController {
                presenter.dismissPageModal(self, completion: finish)
            } else {
                dismiss(animated: true, completion: finish)
            }
        }
    }

    public override func pageDidBecomeInactive() {
        cancelVideoGestures()
        restoreBrightness()
        cancelAutoHide()
        guard !dismissing else { return }
        Task { [self] in
            await model.close(allowSystemOwner: true)
            notifyDismiss()
        }
    }

    private func notifyDismiss() {
        guard !didNotifyDismiss else { return }
        didNotifyDismiss = true
        onDismiss?()
    }

    private func time(_ value: Double) -> String {
        let seconds = Int(value)
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

    private func selectMenu(_ button: UIButton, value: String?) {
        for case let action as UIAction in button.menu?.children ?? [] {
            action.state = action.identifier.rawValue == value ? .on : .off
        }
    }

    private func control(_ title: String, id: String, symbol: String? = nil, action: @escaping () -> Void) -> UIButton {
        let button = FilmPlayerButton(type: .system)
        configure(button, title: title, id: id, symbol: symbol, action: action)
        return button
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
