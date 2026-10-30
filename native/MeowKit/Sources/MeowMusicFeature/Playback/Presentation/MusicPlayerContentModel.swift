#if os(iOS)
import UIKit
import Combine
import AVFoundation
import MeowUI
import MeowPlayback

struct MusicPlayerClockState: Equatable {
    var position: Double = 0
    var duration: Double?
    var rate: Double = 1
    var playing = false
    var buffers: [Range<Double>] = []
    var chorus: Double?
}

/// Product-lifetime playback content. Dock and page own only their rendering;
/// changing presentation never starts a second metadata/artwork/chorus reader.
@MainActor
public final class MusicPlayerContentModel {
    let model: MusicViewModel
    let playback: PlaybackController
    let appearance: MusicAppearancePreferencesStore
    let presentation: MusicMiniPlayerPresentation
    let favorites: MusicMiniPlayerFavorites
    private(set) var clock = MusicPlayerClockState()
    private var mini = MusicMiniPlayerLifecycle()
    private let artworkAdmission = MusicNowPlayingArtworkState()
    private var artworkIdentity: MusicNowPlayingArtworkRequest?
    private var images: [URL: UIImage] = [:]
    private var wantedImages: Set<URL> = []
    private var failedImages: Set<URL> = []
    private var artworkBytes: Data?
    private var artworkReads: [URL: (id: UUID, task: Task<Void, Never>)] = [:]
    private var playbackTask: Task<Void, Never>?
    private var autoCollapseTask: Task<Void, Never>?
    private var modeTasks: [UUID: Task<Void, Never>] = [:]
    private struct ChorusRequest: Equatable {
        let playback: MusicMiniPlayerPlaybackIdentity
        let source: MusicPlaybackSelection?
        let resource: String
        let duration: Double
    }
    private var chorusRead: (request: ChorusRequest, task: Task<Void, Never>)?
    private enum ChorusResult {
        case ready(MusicChorusPoint?)
        case failed(MusicServiceError)
    }
    private var chorusSettlement: (request: ChorusRequest, result: ChorusResult)?
    private var chorus: MusicChorusPoint? {
        if case .ready(let point) = chorusSettlement?.result { return point }
        return nil
    }
    private var subscriptions: [AnyCancellable] = []
    private var observers: [NSObjectProtocol] = []
    private var foreground = false
    private var lastActivation: UUID?
    private var lastFullPresentation = false
    private var observedTrack: MusicRemoteTrack?
    private var observedOccurrence: String?
    private var accessGeneration: UUID
    private let playbackUpdates = PassthroughSubject<Void, Never>()
    private let queueUpdates = PassthroughSubject<Void, Never>()
    private let metadataUpdates = PassthroughSubject<Void, Never>()
    private let artworkUpdates = PassthroughSubject<Void, Never>()
    private let appearanceUpdates = PassthroughSubject<Void, Never>()
    private let clockUpdates = PassthroughSubject<Void, Never>()
    private let favoriteUpdates = PassthroughSubject<Void, Never>()
    private let dockUpdates = PassthroughSubject<Void, Never>()
    private let accessUpdates = PassthroughSubject<Void, Never>()

    var playbackChanges: AnyPublisher<Void, Never> { playbackUpdates.eraseToAnyPublisher() }
    var queueChanges: AnyPublisher<Void, Never> { queueUpdates.eraseToAnyPublisher() }
    var metadataChanges: AnyPublisher<Void, Never> { metadataUpdates.eraseToAnyPublisher() }
    var artworkChanges: AnyPublisher<Void, Never> { artworkUpdates.eraseToAnyPublisher() }
    var appearanceChanges: AnyPublisher<Void, Never> { appearanceUpdates.eraseToAnyPublisher() }
    var clockChanges: AnyPublisher<Void, Never> { clockUpdates.eraseToAnyPublisher() }
    var favoriteChanges: AnyPublisher<Void, Never> { favoriteUpdates.eraseToAnyPublisher() }
    var dockChanges: AnyPublisher<Void, Never> { dockUpdates.eraseToAnyPublisher() }
    var accessChanges: AnyPublisher<Void, Never> { accessUpdates.eraseToAnyPublisher() }
    var isAvailable: Bool { model.isAvailable && !model.isClosed }
    var dockExpanded: Bool { mini.expanded }
    var artwork: UIImage? { artworkIdentity?.url.flatMap { images[$0] } }
    var controlTheme: PageTheme { .init(palette: appearance.preferences.colorPalette, dark: false) }
    var fence: MusicReferencePlayerFence {
        .init(occurrence: model.current?.id, attempt: model.activeNativeAttemptID,
              access: model.accountAccessGeneration, queueRevision: model.state.revision,
              settingsRevision: model.state.settingsRevision)
    }

    public init(model: MusicViewModel, playback: PlaybackController,
                appearance: MusicAppearancePreferencesStore,
                presentation: MusicMiniPlayerPresentation) {
        self.model = model
        self.playback = playback
        self.appearance = appearance
        self.presentation = presentation
        favorites = model.favorites
        accessGeneration = model.accountAccessGeneration
        foreground = UIApplication.shared.applicationState == .active
        observe(model.accessChanges) { [weak self] in self?.reconcileAccess() }
        observe(model.playbackChanges) { [weak self] in self?.reconcilePlayback() }
        observe(model.queueChanges) { [weak self] in self?.queueUpdates.send() }
        observe(model.libraryChanges) { [weak self] in
            self?.queueUpdates.send()
        }
        observe(model.metadataChanges) { [weak self] in
            self?.reconcileArtwork()
            self?.metadataUpdates.send()
            self?.updateClock()
        }
        observe(Publishers.CombineLatest(model.clock.$position, model.clock.$duration)
            .dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in self?.updateClock() }
        observe(Publishers.CombineLatest3(playback.$snapshot, playback.$currentAttempt, playback.$preferredRate)
            .dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in self?.updateClock() }
        observe(Publishers.CombineLatest(presentation.$activation, presentation.$isFullPlayerPresented)
            .dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in self?.reconcilePresentation() }
        observe(Publishers.CombineLatest(appearance.$preferences, appearance.$effectivePalette)
            .removeDuplicates { $0.0 == $1.0 && $0.1 == $1.1 }
            .dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in
                self?.artworkAdmission.preferencesChanged()
                self?.acceptArtwork()
                self?.appearanceUpdates.send()
            }
        observe(Publishers.CombineLatest(appearance.$coverPreferenceRevision, appearance.$displayedArtworkRevision)
            .dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in
                self?.artworkAdmission.preferencesChanged()
                self?.artworkAdmission.artworkContextChanged()
                self?.acceptArtwork()
            }
        observe(favorites.objectWillChange.eraseToAnyPublisher()) { [weak self] in self?.favoriteUpdates.send() }
        for name in [UIApplication.didBecomeActiveNotification, UIApplication.willResignActiveNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) {
                [weak self] notification in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.foreground = notification.name == UIApplication.didBecomeActiveNotification
                    if notification.name == UIApplication.didBecomeActiveNotification {
                        self.artworkAdmission.becameActive()
                        self.acceptArtwork()
                        self.updateClock()
                    } else {
                        self.artworkAdmission.suspendRendering()
                    }
                }
            })
        }
        reconcileAccess()
        reconcilePresentation()
    }

    func artwork(for entry: MusicQueueEntry?) -> UIImage? {
        if entry?.id == model.current?.id { return artwork }
        return entry?.track.artwork.flatMap { images[$0] }
    }

    func collapseDock() {
        changeMini { $0.invalidate() }
        presentation.invalidate()
    }

    func holdDock() { changeMini { $0.hold() } }
    func interactOutsideDock() { changeMini { $0.interactOutside() } }

    func togglePlayback() {
        guard playbackTask == nil, isAvailable else { return }
        holdDock()
        let expected = fence, model = model
        playbackTask = Task { [weak self] in
            defer { self?.playbackTask = nil }
            guard !Task.isCancelled, self?.fence == expected, self?.isAvailable == true else { return }
            await model.togglePlayback()
        }
    }

    func navigate(_ direction: MusicDirection) {
        holdDock()
        if direction == .next { _ = model.requestNext() } else { _ = model.requestPrevious() }
    }

    func toggleFavorite() {
        guard isAvailable, let track = model.current?.track else { return }
        favorites.toggle(track)
    }

    @discardableResult
    func seek(to position: Double, expected: MusicReferencePlayerFence) -> Bool {
        guard expected.admits(fence, permitted: isAvailable) else { return false }
        return model.seek(to: position)
    }

    func setMode(_ mode: MusicPlaybackMode, expected: MusicReferencePlayerFence) {
        runModeCommand(expected: expected) { await $0.setMode(mode) }
    }

    func setSimilarMode(active: Bool, snapshot: UUID?, expected: MusicReferencePlayerFence) {
        runModeCommand(expected: expected) { model in
            guard model.similarMode?.id == snapshot else { return }
            if active { await model.exitSimilarMode() } else { await model.enterSimilarMode() }
        }
    }

    private func observe(_ changes: AnyPublisher<Void, Never>, update: @escaping @MainActor () -> Void) {
        subscriptions.append(changes.receive(on: RunLoop.main).sink { update() })
    }

    private func runModeCommand(expected: MusicReferencePlayerFence,
                                operation: @escaping @MainActor (MusicViewModel) async -> Void) {
        guard expected.admits(fence, permitted: isAvailable) else { return }
        let id = UUID(), model = model
        modeTasks[id] = Task { [weak self] in
            defer { self?.modeTasks[id] = nil }
            guard !Task.isCancelled, self?.fence == expected, self?.isAvailable == true else { return }
            await operation(model)
        }
    }

    private func changeMini(_ change: (inout MusicMiniPlayerLifecycle) -> Void) {
        let previous = mini
        change(&mini)
        if previous.autoCollapseAt != mini.autoCollapseAt {
            autoCollapseTask?.cancel()
            autoCollapseTask = nil
            if let deadline = mini.autoCollapseAt {
                autoCollapseTask = Task { [weak self] in
                    do {
                        try await Task.sleep(for: .seconds(max(0, deadline - ProcessInfo.processInfo.systemUptime)))
                    } catch { return }
                    guard !Task.isCancelled, let self, self.mini.autoCollapseAt == deadline else { return }
                    self.changeMini { $0.expire(at: deadline) }
                }
            }
        }
        if previous.expanded != mini.expanded { dockUpdates.send() }
    }

    private func reconcileAccess() {
        if !isAvailable || accessGeneration != model.accountAccessGeneration {
            playbackTask?.cancel()
            for task in modeTasks.values { task.cancel() }
            collapseDock()
            artworkAdmission.deactivate()
            artworkIdentity = nil
            images.removeAll()
            failedImages.removeAll()
            artworkBytes = nil
            for read in artworkReads.values { read.task.cancel() }
            artworkUpdates.send()
        }
        accessGeneration = model.accountAccessGeneration
        reconcilePlayback()
        accessUpdates.send()
    }

    private func reconcilePlayback() {
        changeMini { $0.select(model.current?.id) }
        reconcileArtwork()
        updateClock()
        if observedTrack != model.displayTrack || observedOccurrence != model.current?.id {
            observedTrack = model.displayTrack
            observedOccurrence = model.current?.id
            metadataUpdates.send()
        }
        playbackUpdates.send()
    }

    private func reconcilePresentation() {
        let full = presentation.isFullPlayerPresented
        if full {
            changeMini { $0.invalidate() }
        } else if let value = presentation.activation, lastActivation != value.intentID {
            lastActivation = value.intentID
            if value.playback.occurrenceID == model.current?.id,
               value.playback.nativeAttemptID == model.activeNativeAttemptID,
               value.playback.accessGeneration == model.accountAccessGeneration {
                changeMini { $0.activate(value.playback.occurrenceID, at: ProcessInfo.processInfo.systemUptime) }
            }
        }
        if lastFullPresentation != full {
            if full, case .failed = chorusSettlement?.result { chorusSettlement = nil }
            lastFullPresentation = full
            dockUpdates.send()
        }
        reconcileArtwork()
        updateClock()
    }

    private func reconcileArtwork() {
        let request = MusicNowPlayingArtworkRequest(track: isAvailable ? model.displayTrack : nil)
        var changed = request != artworkIdentity
        if changed {
            for read in artworkReads.values { read.task.cancel() }
            artworkIdentity = request
            artworkBytes = nil
            if isAvailable { artworkAdmission.activate(request, appearance: appearance) }
            else { artworkAdmission.deactivate() }
        }
        wantedImages = Set([request.url].compactMap { $0 })
        if isAvailable, presentation.isFullPlayerPresented {
            for direction in [MusicDirection.previous, .next] {
                if let url = model.recordPreview(direction)?.track.artwork { wantedImages.insert(url) }
            }
        }
        for (url, read) in artworkReads where !wantedImages.contains(url) { read.task.cancel() }
        images = images.filter { wantedImages.contains($0.key) }
        failedImages.formIntersection(wantedImages)
        for url in wantedImages where images[url] == nil && artworkReads[url] == nil && !failedImages.contains(url) {
            let data = url == request.url ? model.metadataArtworkData : nil
            if data == nil, let cached = MusicArtworkImages.cached(url) {
                images[url] = cached
                changed = true
            } else { readArtwork(url, cachedData: data) }
        }
        if !foreground { artworkAdmission.suspendRendering() }
        acceptArtwork()
        if changed { artworkUpdates.send() }
    }

    private func readArtwork(_ url: URL, cachedData: Data?) {
        let id = UUID()
        let access = model.accountAccessGeneration, expected = artworkIdentity
        let task = Task { [weak self] in
            defer {
                if self?.artworkReads[url]?.id == id {
                    self?.artworkReads[url] = nil
                    self?.reconcileArtwork()
                }
            }
            do {
                let (image, data) = try await MusicArtworkImages.read(url, cachedData: cachedData)
                try Task.checkCancellation()
                guard let self, self.wantedImages.contains(url), self.isAvailable,
                      self.model.accountAccessGeneration == access,
                      MusicNowPlayingArtworkRequest(track: self.model.displayTrack) == expected else { return }
                self.images[url] = image
                if self.artworkIdentity?.url == url {
                    self.artworkBytes = data
                    self.acceptArtwork()
                }
                self.artworkUpdates.send()
            } catch {
                guard !Task.isCancelled, let self, self.wantedImages.contains(url), self.isAvailable,
                      self.model.accountAccessGeneration == access,
                      MusicNowPlayingArtworkRequest(track: self.model.displayTrack) == expected else { return }
                self.failedImages.insert(url)
                if self.artworkIdentity?.url == url { self.model.artworkFailed(url) }
            }
        }
        artworkReads[url] = (id, task)
    }

    private func acceptArtwork() {
        guard let image = artwork else { return }
        let revision = artworkAdmission.samplingRevision
        _ = artworkAdmission.deliverImage(revision: revision) { }
        _ = artworkAdmission.captureArtwork(revision: revision) { image.jpegData(compressionQuality: 0.85) }
        if let artworkBytes {
            _ = artworkAdmission.captureDisplay(revision: revision, encode: { artworkBytes }) { [weak self] data in
                guard let self else { return }
                self.model.capturedArtwork(data, url: self.artworkIdentity?.url)
            }
        }
        _ = artworkAdmission.sample(revision: revision) {
            image.cgImage.flatMap { MusicCoverPaletteSampler.rgba($0) }
        }
    }

    private var chorusRequest: ChorusRequest? {
        guard isAvailable, let occurrence = model.current?.id, let attempt = model.activeNativeAttemptID,
              MusicReferencePlayerFence.ownsAttempt(active: model.activeNativeAttemptID,
                  observed: playback.currentAttempt?.id, owner: playback.snapshot.owner),
              let resource = model.activeResourceID,
              playback.snapshot.resourceID == resource,
              let duration = model.duration, duration.isFinite, duration > 0 else { return nil }
        return .init(playback: .init(occurrenceID: occurrence, nativeAttemptID: attempt,
                                    accessGeneration: model.accountAccessGeneration),
                     source: model.activePlaybackSelection, resource: resource, duration: duration)
    }

    private func reconcileChorus() {
        let expected = chorusRequest
        if chorusSettlement?.request != expected { chorusSettlement = nil }
        if let read = chorusRead {
            if read.request != expected { read.task.cancel() }
            return
        }
        guard presentation.isFullPlayerPresented, let expected, chorusSettlement?.request != expected else { return }
        let model = model
        let task = Task { [weak self] in
            // Cancellation does not release this slot. Only the actual
            // provider's return (including non-cooperative reads) reaches here.
            defer {
                if self?.chorusRead?.request == expected {
                    self?.chorusRead = nil
                    self?.updateClock()
                }
            }
            do {
                let point = try await model.chorus(actualDuration: expected.duration)
                guard !Task.isCancelled, let self, self.chorusRequest == expected else { return }
                let valid = point.flatMap {
                    $0.start.isFinite && $0.end.isFinite && $0.start >= 0 &&
                        $0.end > $0.start && $0.end <= expected.duration ? $0 : nil
                }
                self.chorusSettlement = (expected, .ready(valid))
            } catch {
                guard !Task.isCancelled, let self, self.chorusRequest == expected else { return }
                self.chorusSettlement = (expected, .failed(MusicServiceError.sanitized(error)))
            }
        }
        chorusRead = (expected, task)
    }

    func flushChorus() async {
        while let read = chorusRead { await read.task.value }
    }

    private func updateClock() {
        reconcileChorus()
        let snapshot = playback.snapshot
        let matching = MusicReferencePlayerFence.ownsAttempt(
            active: model.activeNativeAttemptID, observed: playback.currentAttempt?.id, owner: snapshot.owner
        ) && snapshot.resourceID == model.activeResourceID
        let ranges = matching ? playback.player.currentItem?.loadedTimeRanges.compactMap { value -> Range<Double>? in
            let range = value.timeRangeValue
            let start = range.start.seconds, end = (range.start + range.duration).seconds
            return start.isFinite && end.isFinite && end > start ? start..<end : nil
        } ?? [] : []
        let next = MusicPlayerClockState(
            position: model.position, duration: model.duration, rate: playback.preferredRate,
            playing: matching && model.phase == .playing && playback.player.timeControlStatus == .playing,
            buffers: MusicReferenceProgressGeometry.buffers(
                ranges, duration: model.duration, complete: matching && model.activeAudioIsCached == true),
            chorus: chorus.map { MusicReferenceProgressGeometry.fraction($0.start, duration: model.duration) })
        if clock != next { clock = next; clockUpdates.send() }
    }

    deinit {
        playbackTask?.cancel()
        autoCollapseTask?.cancel()
        for task in modeTasks.values { task.cancel() }
        chorusRead?.task.cancel()
        for read in artworkReads.values { read.task.cancel() }
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
    }
}
#endif
