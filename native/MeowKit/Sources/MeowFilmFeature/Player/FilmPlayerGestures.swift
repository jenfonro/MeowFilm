#if os(iOS)
import UIKit

@MainActor
extension FilmPlayerViewController {
    func configureVideoGestures() {
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

    func cancelVideoGestures() {
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

    func restoreBrightness() {
        if let brightnessScreen, let originalBrightness { brightnessScreen.brightness = originalBrightness }
        brightnessScreen = nil; originalBrightness = nil
    }
}
#endif
