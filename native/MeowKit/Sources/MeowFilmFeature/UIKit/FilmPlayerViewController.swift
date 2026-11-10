#if os(iOS)
import UIKit
import MeowUI

@MainActor
public protocol FilmPlaybackPage: AnyObject {
    var filmPlaybackModel: FilmPlaybackModel? { get }
}

/// UIKit controls over the App's single native video surface. Constructing this
/// page creates no media input, decoder or audio session.
@MainActor
public final class FilmPlayerViewController: PageViewController, FilmPlaybackPage {
    public var filmPlaybackModel: FilmPlaybackModel? { model }
    public var onDismiss: (() -> Void)?
    private let model: FilmPlaybackModel
    private let video = UIView(), controls = UIStackView()
    private let titleLabel = UILabel(), sourceLabel = UILabel(), stateLabel = UILabel()
    private let activity = UIActivityIndicatorView(style: .large)
    private let play = UIButton(type: .system), previous = UIButton(type: .system), nextButton = UIButton(type: .system)
    private let seek = UISlider(), volume = UISlider()
    private let position = UILabel(), duration = UILabel()
    private let pip = UIButton(type: .system), rate = UIButton(type: .system), scale = UIButton(type: .system)
    private let fullscreen = UIButton(type: .system), shortVideo = UIButton(type: .system)
    private var videoView: UIView?
    private var videoSession: UUID?
    private var scrubbing = false
    private var fillScreen = false
    private var scaleMode: FilmVideoScale = .fit
    private var shownError: FilmPlaybackError?
    private var swipeUp: UISwipeGestureRecognizer!, swipeDown: UISwipeGestureRecognizer!
    private var dismissing = false
    private var didNotifyDismiss = false

    public init(model: FilmPlaybackModel, themeProvider: any PageThemeProviding) {
        self.model = model
        super.init(themeProvider: themeProvider)
        modalPresentationStyle = .fullScreen
        pageTitle = "播放"
    }

    public override var preferredStatusBarStyle: UIStatusBarStyle { .lightContent }
    public override var prefersStatusBarHidden: Bool { fillScreen }
    public override var prefersHomeIndicatorAutoHidden: Bool { fillScreen }
    public override func pageDidBecomeVisible() {
        model.open()
        render()
    }
    public override func pageDidFinishAppearing() {
        super.pageDidFinishAppearing()
        render()
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.player"
        view.backgroundColor = .black
        pageHeader.isHidden = true
        video.backgroundColor = .black
        video.accessibilityIdentifier = "film.player.video"
        view.addSubview(video); video.pinEdges(to: view)
        activity.color = .white; activity.translatesAutoresizingMaskIntoConstraints = false
        video.addSubview(activity)
        NSLayoutConstraint.activate([
            activity.centerXAnchor.constraint(equalTo: video.centerXAnchor),
            activity.centerYAnchor.constraint(equalTo: video.centerYAnchor),
        ])
        controls.axis = .vertical; controls.spacing = 8
        controls.backgroundColor = UIColor.black.withAlphaComponent(0.68)
        controls.isLayoutMarginsRelativeArrangement = true
        controls.directionalLayoutMargins = .init(top: 12, leading: 16, bottom: 12, trailing: 16)
        controls.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(controls)
        NSLayoutConstraint.activate([
            controls.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            controls.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            controls.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
        ])
        let close = control("返回", id: "close", symbol: "chevron.down") { [weak self] in self?.dismissPlayer() }
        titleLabel.font = .systemFont(ofSize: 17, weight: .semibold)
        titleLabel.textColor = .white; titleLabel.numberOfLines = 2
        titleLabel.accessibilityIdentifier = "film.player.title"
        controls.addArrangedSubview(row([titleLabel, close]))
        sourceLabel.font = .systemFont(ofSize: 12); sourceLabel.textColor = .lightGray
        sourceLabel.numberOfLines = 2; sourceLabel.accessibilityIdentifier = "film.player.source"
        controls.addArrangedSubview(sourceLabel)
        stateLabel.font = .systemFont(ofSize: 14); stateLabel.textColor = .white
        stateLabel.textAlignment = .center; stateLabel.numberOfLines = 2
        stateLabel.accessibilityIdentifier = "film.player.state"
        controls.addArrangedSubview(stateLabel)
        for label in [position, duration] {
            label.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
            label.textColor = .white
            label.setContentHuggingPriority(.required, for: .horizontal)
        }
        seek.accessibilityIdentifier = "film.player.seek"
        seek.accessibilityLabel = "播放进度"
        seek.addAction(UIAction { [weak self] _ in self?.scrubbing = true }, for: .touchDown)
        seek.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            scrubbing = false; model.seek(to: Double(seek.value))
        }, for: [.touchUpInside, .touchUpOutside])
        seek.addAction(UIAction { [weak self] _ in self?.scrubbing = false }, for: .touchCancel)
        controls.addArrangedSubview(row([position, seek, duration]))
        configure(previous, title: "上一集", id: "previous", symbol: "backward.end.fill") { [weak model] in model?.send("previous") }
        configure(play, title: "播放", id: "play-pause", symbol: "play.fill") { [weak model] in model?.pauseOrResume() }
        configure(nextButton, title: "下一集", id: "next", symbol: "forward.end.fill") { [weak model] in model?.send("next") }
        configure(shortVideo, title: "短视频", id: "short-video", symbol: "rectangle.portrait") { [weak model] in
            guard let model, let snapshot = model.snapshot else { return }
            model.setShortVideo(!snapshot.shortVideo)
        }
        controls.addArrangedSubview(row([previous, play, nextButton, shortVideo], equal: true))
        configure(rate, title: "1×", id: "rate", symbol: nil) {}
        rate.showsMenuAsPrimaryAction = true
        rate.menu = UIMenu(title: "倍速", children: [0.5, 0.75, 1, 1.25, 1.5, 2, 3].map { value in
            UIAction(title: "\(value)×") { [weak model] _ in model?.setRate(value) }
        })
        configure(scale, title: "比例", id: "scale", symbol: nil) {}
        scale.showsMenuAsPrimaryAction = true
        scale.menu = UIMenu(title: "比例", children: FilmVideoScale.allCases.map { mode in
            UIAction(title: mode.title) { [weak self] _ in
                self?.scaleMode = mode; self?.model.setVideoScale(mode)
            }
        })
        configure(pip, title: "画中画", id: "pip", symbol: "pip") { [weak model] in
            guard let model else { return }
            if model.mediaState.pictureInPicture { model.stopPictureInPicture() }
            else { model.startPictureInPicture() }
        }
        configure(fullscreen, title: "全屏", id: "fullscreen", symbol: "arrow.up.left.and.arrow.down.right") { [weak self] in
            guard let self else { return }
            fillScreen.toggle(); controls.alpha = fillScreen ? 0 : 1
            controls.isUserInteractionEnabled = !fillScreen
            setNeedsUpdateOfHomeIndicatorAutoHidden()
            setNeedsStatusBarAppearanceUpdate()
        }
        controls.addArrangedSubview(row([rate, scale, pip, fullscreen], equal: true))
        volume.accessibilityIdentifier = "film.player.volume"; volume.accessibilityLabel = "播放音量"
        volume.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            model.setVolume(Double(volume.value))
        }, for: .valueChanged)
        let volumeLabel = UILabel()
        volumeLabel.text = "音量"; volumeLabel.textColor = .white; volumeLabel.font = .systemFont(ofSize: 13)
        controls.addArrangedSubview(row([volumeLabel, volume]))
        configureSourceActions()
        let tap = UITapGestureRecognizer(target: self, action: #selector(toggleControls))
        video.addGestureRecognizer(tap)
        swipeUp = UISwipeGestureRecognizer(target: self, action: #selector(moveEpisode(_:)))
        swipeUp.direction = .up
        swipeDown = UISwipeGestureRecognizer(target: self, action: #selector(moveEpisode(_:)))
        swipeDown.direction = .down
        video.addGestureRecognizer(swipeUp); video.addGestureRecognizer(swipeDown)
        observe(model) { [weak self] in self?.render() }
        render()
    }

    private func configureSourceActions() {
        let pan = control("网盘", id: "pan") {}
        pan.showsMenuAsPrimaryAction = true
        pan.menu = UIMenu(title: "网盘", children: FilmPanProvider.allCases.map { provider in
            UIAction(title: provider.title) { [weak model] _ in model?.selectPan(provider) }
        })
        let quality = control("画质", id: "quality") {}
        quality.showsMenuAsPrimaryAction = true
        quality.menu = UIMenu(title: "画质", children: FilmResourceQuality.allCases.map { quality in
            UIAction(title: quality.title) { [weak model] _ in model?.selectQuality(quality) }
        })
        let change = control("换源", id: "switch") { [weak model] in model?.send("switch") }
        let block = control("片源错误", id: "block") { [weak self] in
            guard let self, let snapshot = model.snapshot,
                  let attemptID = snapshot.current?.attemptID ?? snapshot.attempt?.id else { return }
            let alert = UIAlertController(title: "片源错误", message: snapshot.target.title, preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "取消", style: .cancel))
            alert.addAction(UIAlertAction(title: "屏蔽并换源", style: .destructive) { [weak model] _ in
                model?.send("block", parameters: ["attemptId": .string(attemptID)])
            })
            present(alert, animated: true)
        }
        controls.addArrangedSubview(row([pan, quality, change, block], equal: true))
        let external = control("外部播放器", id: "external") {}
        external.showsMenuAsPrimaryAction = true
        external.menu = UIMenu(title: "外部播放器", children: FilmExternalPlayer.allCases.map { player in
            UIAction(title: player.title) { [weak model] _ in model?.openExternalPlayer(player) }
        })
        let retry = control("重试地址", id: "retry-media") { [weak model] in model?.retryMedia() }
        controls.addArrangedSubview(row([retry, external], equal: true))
    }

    private func render() {
        guard isViewLoaded else { return }
        if videoSession != model.sessionID, let surface = model.makeVideoView() {
            videoView?.removeFromSuperview(); videoView = surface; videoSession = model.sessionID
            video.insertSubview(surface, at: 0); surface.pinEdges(to: video)
            model.setVideoScale(scaleMode)
        }
        let state = model.mediaState, snapshot = model.snapshot
        titleLabel.text = snapshot?.target.title
        let candidate = snapshot?.current?.candidate ?? snapshot?.attempt?.candidate
        sourceLabel.text = candidate.map { [$0.source.name, $0.lineLabel, $0.name].filter { !$0.isEmpty }.joined(separator: " · ") }
        let playing = state.attemptID != nil
        play.isEnabled = playing || model.requiresExplicitPlay || snapshot != nil
        play.setTitle(state.paused ? "播放" : "暂停", for: .normal)
        play.setImage(UIImage(systemName: state.paused ? "play.fill" : "pause.fill"), for: .normal)
        if !scrubbing {
            seek.maximumValue = Float(state.duration)
            seek.value = Float(state.position)
        }
        seek.isEnabled = playing && state.duration > 0
        position.text = time(state.position); duration.text = time(state.duration)
        volume.value = Float(state.volume); volume.isEnabled = playing
        rate.setTitle("\(state.rate)×", for: .normal); rate.isEnabled = playing
        pip.isEnabled = state.pictureInPictureAvailable
        pip.isSelected = state.pictureInPicture
        previous.isEnabled = snapshot?.target.kind != .movie && playing
        nextButton.isEnabled = snapshot?.target.kind != .movie && playing
        shortVideo.isSelected = snapshot?.shortVideo == true
        shortVideo.setTitle(shortVideo.isSelected ? "短视频：开" : "短视频", for: .normal)
        shortVideo.isEnabled = snapshot != nil && snapshot?.target.kind != .movie
        swipeUp.isEnabled = shortVideo.isSelected; swipeDown.isEnabled = shortVideo.isSelected
        let busy = state.buffering || ["loading", "searching", "resolving", "pending"].contains(snapshot?.stage ?? "")
        if busy { activity.startAnimating() } else { activity.stopAnimating() }
        stateLabel.text = snapshot?.error.map { FilmPlaybackError(code: $0).localizedDescription } ??
            (busy ? "准备中" : snapshot?.stage == "ended" ? "播放结束" : nil)
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
    }

    @objc private func toggleControls() {
        fillScreen.toggle(); controls.alpha = fillScreen ? 0 : 1
        controls.isUserInteractionEnabled = !fillScreen
        setNeedsUpdateOfHomeIndicatorAutoHidden()
        setNeedsStatusBarAppearanceUpdate()
    }
    @objc private func moveEpisode(_ gesture: UISwipeGestureRecognizer) {
        guard model.snapshot?.shortVideo == true else { return }
        model.send(gesture.direction == .up ? "next" : "previous")
    }
    private func dismissPlayer() {
        guard !dismissing else { return }
        dismissing = true
        Task { [weak self] in
            guard let self else { return }
            await model.close(allowSystemOwner: true)
            guard model.canLeave || model.hasSystemOwner else {
                dismissing = false
                render()
                return
            }
            dismiss(animated: true) { [weak self] in self?.notifyDismiss() }
        }
    }
    public override func pageDidBecomeInactive() {
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
    private func control(_ title: String, id: String, symbol: String? = nil, action: @escaping () -> Void) -> UIButton {
        let button = UIButton(type: .system)
        configure(button, title: title, id: id, symbol: symbol, action: action)
        return button
    }
    private func configure(_ button: UIButton, title: String, id: String, symbol: String?, action: @escaping () -> Void) {
        button.setTitle(title, for: .normal); button.tintColor = .white
        button.titleLabel?.font = .systemFont(ofSize: 12, weight: .medium)
        button.titleLabel?.lineBreakMode = .byTruncatingTail
        if let symbol { button.setImage(UIImage(systemName: symbol), for: .normal) }
        button.accessibilityIdentifier = "film.player.\(id)"; button.accessibilityLabel = title
        button.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        button.addAction(UIAction { _ in action() }, for: .touchUpInside)
    }
}
#endif
