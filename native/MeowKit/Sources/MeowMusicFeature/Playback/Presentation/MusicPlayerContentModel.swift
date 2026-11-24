#if os(iOS)
import UIKit
import Combine
import MeowContracts
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
    let artworkImages: MusicArtworkImages
    private var artworkIdentity: MusicNowPlayingArtworkRequest?
    private var artworkBinding: MusicSystemMetadataBinding?
    private var artworkContextRevision: UUID?
    private var images: [URL: UIImage] = [:]
    private var artworkResources: [URL: URL] = [:]
    private var wantedImages: Set<URL> = []
    private var failedImages: Set<URL> = []
    private var cancelledImages: Set<URL> = []
    private var artworkDemandRevision: UUID
    private var retainedArtwork: UIImage?
    private var retainedSongID: String?
    private var artworkReads: [URL: (id: UUID, task: Task<Void, Never>)] = [:]
    private struct DiscretePlaybackState: Equatable {
        let owner: String?
        let resourceID: String?
        let phase: PlaybackPhase
        let duration: Double?
        let error: String?
        let isOutputPlaying: Bool
    }
    private struct ArtworkProcessingKey: Equatable {
        let url: URL
        let imageID: ObjectIdentifier
        let samplingRevision: UUID
        let artworkRevision: UUID
        let wantsArtwork: Bool
        let wantsPalette: Bool
    }
    private var artworkProcessing: (key: ArtworkProcessingKey, task: Task<Void, Never>)?
    private var playbackTasks: [UUID: Task<Void, Never>] = [:]
    private var cacheProgressSource: UUID?
    private var cacheProgressTask: Task<Void, Never>?
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
    private let positionUpdates = PassthroughSubject<Double, Never>()
    private let seekUpdates = PassthroughSubject<Void, Never>()
    private let favoriteUpdates = PassthroughSubject<Void, Never>()
    private let dockUpdates = PassthroughSubject<Void, Never>()
    private let accessUpdates = PassthroughSubject<Void, Never>()

    var playbackChanges: AnyPublisher<Void, Never> { playbackUpdates.eraseToAnyPublisher() }
    var queueChanges: AnyPublisher<Void, Never> { queueUpdates.eraseToAnyPublisher() }
    var metadataChanges: AnyPublisher<Void, Never> { metadataUpdates.eraseToAnyPublisher() }
    var artworkChanges: AnyPublisher<Void, Never> { artworkUpdates.eraseToAnyPublisher() }
    var appearanceChanges: AnyPublisher<Void, Never> { appearanceUpdates.eraseToAnyPublisher() }
    var clockChanges: AnyPublisher<Void, Never> { clockUpdates.eraseToAnyPublisher() }
    var positionSamples: AnyPublisher<Double, Never> { positionUpdates.eraseToAnyPublisher() }
    var seekChanges: AnyPublisher<Void, Never> { seekUpdates.eraseToAnyPublisher() }
    var favoriteChanges: AnyPublisher<Void, Never> { favoriteUpdates.eraseToAnyPublisher() }
    var dockChanges: AnyPublisher<Void, Never> { dockUpdates.eraseToAnyPublisher() }
    var accessChanges: AnyPublisher<Void, Never> { accessUpdates.eraseToAnyPublisher() }
    var isAvailable: Bool { !model.isClosed }
    var dockExpanded: Bool { mini.expanded }
    var artwork: UIImage? {
        artworkIdentity?.url.flatMap { images[$0] } ??
            (retainedSongID == model.displayTrack?.songId ? retainedArtwork : nil)
    }
    var controlTheme: PageTheme { .init(colors: appearance.preferences.colorPalette, dark: false) }
    var fence: MusicReferencePlayerFence {
        .init(occurrence: model.current?.id, attempt: model.activeNativeAttemptID,
              access: model.searchAccessGeneration, queueRevision: model.state.revision,
              settingsRevision: model.state.settingsRevision)
    }

    public convenience init(model: MusicViewModel, playback: PlaybackController,
                            appearance: MusicAppearancePreferencesStore,
                            presentation: MusicMiniPlayerPresentation) {
        self.init(model: model, playback: playback, appearance: appearance,
                  presentation: presentation, artworkImages: model.artworkImages)
    }

    init(model: MusicViewModel, playback: PlaybackController,
         appearance: MusicAppearancePreferencesStore,
         presentation: MusicMiniPlayerPresentation, artworkImages: MusicArtworkImages) {
        self.model = model
        self.playback = playback
        self.appearance = appearance
        self.presentation = presentation
        self.artworkImages = artworkImages
        artworkDemandRevision = artworkImages.revision
        favorites = model.favorites
        accessGeneration = model.searchAccessGeneration
        foreground = UIApplication.shared.applicationState == .active
        observe(model.searchAccessChanges) { [weak self] in self?.reconcileAccess() }
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
        observe(model.clock.$duration.dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in self?.updateClock() }
        subscriptions.append(model.clock.$position.dropFirst().receive(on: RunLoop.main).sink { [weak self] position in
            guard let self, position == self.model.position else { return }
            self.updateClock(isPositionSample: true)
        })
        let discreteSnapshot = playback.$snapshot
            .map { [weak playback] snapshot in
                DiscretePlaybackState(
                    owner: snapshot.owner,
                    resourceID: snapshot.resourceID,
                    phase: snapshot.phase,
                    duration: snapshot.duration,
                    error: snapshot.error,
                    isOutputPlaying: playback?.isOutputPlaying ?? false
                )
            }
            .removeDuplicates()
        observe(Publishers.CombineLatest3(
            discreteSnapshot,
            playback.$currentAttempt.removeDuplicates(),
            playback.$preferredRate.removeDuplicates()
        )
        .dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in self?.updateClock() }
        observe(Publishers.CombineLatest(playback.$currentAttempt, playback.$snapshot.map(\.phase).removeDuplicates())
            .removeDuplicates { $0.0 == $1.0 && $0.1 == $1.1 }
            .dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in self?.reconcileArtwork() }
        observe(Publishers.CombineLatest(presentation.$activation, presentation.$isFullPlayerPresented)
            .dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in self?.reconcilePresentation() }
        observe(Publishers.CombineLatest(appearance.$preferences, appearance.$effectivePalette)
            .removeDuplicates { $0.0 == $1.0 && $0.1 == $1.1 }
            .dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in
                self?.artworkAdmission.preferencesChanged()
                self?.acceptArtwork()
                self?.appearanceUpdates.send()
            }
        observe(appearance.$coverPreferenceRevision.dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in
            self?.artworkAdmission.preferencesChanged()
            self?.acceptArtwork()
        }
        observe(appearance.$displayedArtworkRevision.dropFirst().map { _ in }.eraseToAnyPublisher()) { [weak self] in
            self?.reconcileArtwork()
        }
        observe(favorites.favoriteChanges) { [weak self] in self?.favoriteUpdates.send() }
        for name in [UIApplication.didBecomeActiveNotification, UIApplication.willResignActiveNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) {
                [weak self] notification in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    let wasForeground = self.foreground
                    self.foreground = notification.name == UIApplication.didBecomeActiveNotification
                    if notification.name == UIApplication.didBecomeActiveNotification {
                        self.artworkAdmission.becameActive()
                        if !wasForeground {
                            self.artworkDemandRevision = self.artworkImages.revision
                            self.cancelledImages.removeAll()
                            self.reconcileArtwork()
                        } else { self.acceptArtwork() }
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
        return entry?.track.artwork.flatMap { images[artworkResources[$0] ?? $0] }
    }

    func collapseDock() {
        changeMini { $0.invalidate() }
        presentation.invalidate()
    }

    func holdDock() { changeMini { $0.hold() } }
    func interactOutsideDock() { changeMini { $0.interactOutside() } }

    func togglePlayback() {
        guard isAvailable else { return }
        holdDock()
        let id = UUID(), expected = fence, model = model
        // The model must consume a second toggle before cancelling its pending
        // play; cancelling the first UI parent here would erase that intent.
        playbackTasks[id] = Task { [weak self] in
            defer { self?.playbackTasks[id] = nil }
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
        guard model.seek(to: position) else { return false }
        seekUpdates.send()
        return true
    }

    func setMode(_ mode: MusicPlaybackMode, expected: MusicReferencePlayerFence) {
        runModeCommand(expected: expected) { await $0.setMode(mode) }
    }

    func setSimilarContinue(_ enabled: Bool, snapshot: UUID, expected: MusicReferencePlayerFence) {
        runModeCommand(expected: expected) { model in
            guard model.similarMode?.id == snapshot else { return }
            await model.setSimilarContinue(enabled, snapshotID: snapshot)
        }
    }

    func setQueueRecommendationContinue(_ enabled: Bool, snapshot: UUID, expected: MusicReferencePlayerFence) {
        runModeCommand(expected: expected) { model in
            guard model.queueRecommendations?.id == snapshot else { return }
            await model.setQueueRecommendationContinue(enabled, snapshotID: snapshot)
        }
    }

    func setFMContinue(_ enabled: Bool, session: UUID, expected: MusicReferencePlayerFence) {
        runModeCommand(expected: expected) { await $0.setFMContinue(enabled, sessionID: session) }
    }

    func startSongRecommendations(expected: MusicReferencePlayerFence) {
        runModeCommand(expected: expected) { model in
            guard let occurrence = expected.occurrence else { return }
            await model.startSongRecommendations(occurrenceID: occurrence, expectedRevision: expected.queueRevision)
        }
    }

    func startCurrentHeartbeat(expected: MusicReferencePlayerFence) {
        runModeCommand(expected: expected) { model in
            guard let occurrence = expected.occurrence else { return }
            await model.startCurrentHeartbeat(occurrenceID: occurrence, expectedRevision: expected.queueRevision)
        }
    }

    func returnFromRecommendations(snapshot: UUID?, expected: MusicReferencePlayerFence) {
        runModeCommand(expected: expected) { model in
            guard model.similarMode?.id == snapshot else { return }
            await model.exitSimilarMode()
        }
    }

    private func observe(_ changes: AnyPublisher<Void, Never>, update: @escaping @MainActor () -> Void) {
        subscriptions.append(changes.receive(on: RunLoop.main).sink { update() })
    }

    private func runModeCommand(expected: MusicReferencePlayerFence,
                                operation: @escaping @MainActor (MusicViewModel) async -> Void) {
        guard expected.admits(fence, permitted: isAvailable) else { return }
        let id = UUID(), model = model, account = model.searchAccessGeneration
        let command = model.recommendationCommandID
        let similarID = model.similarMode?.id, recommendationID = model.queueRecommendations?.id, fmID = model.fmPlayback?.id
        modeTasks[id] = Task { [weak self] in
            defer { self?.modeTasks[id] = nil }
            guard !Task.isCancelled, self?.fence == expected, self?.isAvailable == true,
                  model.searchAccessGeneration == account, model.recommendationCommandID == command,
                  model.similarMode?.id == similarID, model.queueRecommendations?.id == recommendationID,
                  model.fmPlayback?.id == fmID else { return }
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
        if !isAvailable || accessGeneration != model.searchAccessGeneration {
            for task in playbackTasks.values { task.cancel() }
            for task in modeTasks.values { task.cancel() }
            collapseDock()
            artworkAdmission.deactivate()
            artworkIdentity = nil
            images.removeAll()
            artworkResources.removeAll()
            failedImages.removeAll()
            cancelledImages.removeAll()
            retainedArtwork = nil; retainedSongID = nil
            artworkProcessing?.task.cancel(); artworkProcessing = nil
            for read in artworkReads.values { read.task.cancel() }
            artworkUpdates.send()
        }
        accessGeneration = model.searchAccessGeneration
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
            if full {
                artworkDemandRevision = artworkImages.revision
                cancelledImages.removeAll()
                if case .failed = chorusSettlement?.result { chorusSettlement = nil }
            }
            lastFullPresentation = full
            dockUpdates.send()
        }
        reconcileArtwork()
        updateClock()
    }

    private var currentArtworkBinding: MusicSystemMetadataBinding? {
        guard isAvailable, let entry = model.current, let attempt = playback.currentAttempt else { return nil }
        return MusicSystemMetadataBinding(entry: entry, attempt: attempt,
            activeAttemptID: model.activeNativeAttemptID, activeResourceID: model.activeResourceID,
            activeOccurrenceID: model.activeOccurrenceID, access: model.accountAccessGeneration,
            snapshot: playback.snapshot)
    }

    private func reconcileArtwork() {
        let request = MusicNowPlayingArtworkRequest(track: isAvailable ? model.displayTrack : nil)
        let songID = isAvailable ? model.displayTrack?.songId : nil
        if retainedSongID != songID { retainedArtwork = nil }
        retainedSongID = songID
        let binding = currentArtworkBinding, context = appearance.displayedArtworkRevision
        var changed = request != artworkIdentity
        let scopeChanged = artworkBinding != binding || artworkContextRevision != context
        if changed || scopeChanged {
            artworkProcessing?.task.cancel(); artworkProcessing = nil
            for read in artworkReads.values { read.task.cancel() }
            artworkBinding = binding; artworkContextRevision = context
        }
        if changed {
            artworkIdentity = request
            if isAvailable { artworkAdmission.activate(request, appearance: appearance, rendering: foreground) }
            else { artworkAdmission.deactivate() }
        } else if scopeChanged {
            artworkAdmission.artworkContextChanged()
        }
        wantedImages = Set([request.url].compactMap { $0 })
        if isAvailable, presentation.isFullPlayerPresented {
            for direction in [MusicDirection.previous, .next] {
                if let url = model.recordPreview(direction)?.track.artwork { wantedImages.insert(url) }
            }
        }
        for (url, read) in artworkReads where !wantedImages.contains(url) { read.task.cancel() }
        artworkResources = artworkResources.filter { wantedImages.contains($0.key) }
        let wantedResources = Set(wantedImages.map { artworkResources[$0] ?? $0 })
        images = images.filter { wantedResources.contains($0.key) }
        failedImages.formIntersection(wantedImages)
        cancelledImages.formIntersection(wantedImages)
        for url in wantedImages where images[artworkResources[url] ?? url] == nil && artworkReads[url] == nil &&
            !failedImages.contains(url) && !cancelledImages.contains(url) {
            let resourceURL = artworkResources[url] ?? url
            if let cached = artworkImages.cached(resourceURL) {
                images[resourceURL] = cached
                changed = true
            } else { readArtwork(url) }
        }
        if !foreground { artworkAdmission.suspendRendering() }
        acceptArtwork()
        if changed { artworkUpdates.send() }
    }

    private func readArtwork(_ url: URL) {
        let id = UUID()
        let access = model.searchAccessGeneration, expected = artworkIdentity
        let revision = artworkAdmission.artworkRevision, binding = artworkBinding
        let context = artworkContextRevision
        let demand = artworkImages.revision
        let task = Task { [weak self] in
            defer {
                if self?.artworkReads[url]?.id == id {
                    self?.artworkReads[url] = nil
                    self?.reconcileArtwork()
                }
            }
            do {
                guard let artworkImages = self?.artworkImages else { return }
                let (image, _, resourceURL) = try await artworkImages.read(url)
                try Task.checkCancellation()
                let previewSongIDs: Set<String>
                if let self, self.wantedImages.contains(url), self.isAvailable,
                   self.model.searchAccessGeneration == access,
                   self.artworkReads[url]?.id == id, self.artworkAdmission.artworkRevision == revision,
                   self.currentArtworkBinding == binding, self.appearance.displayedArtworkRevision == context,
                   MusicNowPlayingArtworkRequest(track: self.model.displayTrack) == expected {
                    // Never register rendition bytes under the original URL.
                    self.images[resourceURL] = image
                    if resourceURL != url { self.artworkResources[url] = resourceURL }
                    if self.artworkIdentity?.url == url {
                        self.retainedArtwork = image
                        self.model.artworkLoaded(url, resourceURL: resourceURL)
                        if resourceURL == url { self.acceptArtwork() }
                    }
                    let currentSongID = self.artworkIdentity?.url == url ? self.model.displayTrack?.songId : nil
                    previewSongIDs = resourceURL != url && self.presentation.isFullPlayerPresented ?
                        Set([MusicDirection.previous, .next].compactMap { direction in
                            guard let preview = self.model.recordPreview(direction), preview.track.artwork == url,
                                  preview.track.songId != currentSongID else { return nil }
                            return preview.track.songId
                        }) : []
                    self.artworkUpdates.send()
                } else { return }
                for songID in previewSongIDs {
                    try Task.checkCancellation()
                    guard self?.isAvailable == true, self?.model.searchAccessGeneration == access,
                          self?.presentation.isFullPlayerPresented == true,
                          [MusicDirection.previous, .next].contains(where: { direction in
                              let preview = self?.model.recordPreview(direction)
                              return preview?.track.songId == songID && preview?.track.artwork == url
                          }) else { continue }
                    try? await artworkImages.succeeded(songID: songID, url: resourceURL, replacing: url)
                }
            } catch {
                guard !Task.isCancelled, let self, self.wantedImages.contains(url), self.isAvailable,
                      self.model.searchAccessGeneration == access,
                      self.artworkReads[url]?.id == id, self.artworkAdmission.artworkRevision == revision,
                      self.currentArtworkBinding == binding, self.appearance.displayedArtworkRevision == context,
                      MusicNowPlayingArtworkRequest(track: self.model.displayTrack) == expected else { return }
                // A cleared cache epoch can cancel the shared producer without
                // cancelling this subscriber. It is not an artwork failure and
                // stays blocked until a new explicit visible demand.
                if error is CancellationError || (error as? URLError)?.code == .cancelled {
                    // A demand before the clear cannot authorize a refill.
                    if self.artworkDemandRevision == demand ||
                        self.artworkDemandRevision != self.artworkImages.revision {
                        self.cancelledImages.insert(url)
                    }
                    return
                }
                self.failedImages.insert(url)
                if self.artworkIdentity?.url == url { self.model.artworkFailed(url) }
            }
        }
        artworkReads[url] = (id, task)
    }

    private func acceptArtwork() {
        guard let url = artworkIdentity?.url, let image = images[url] else {
            artworkProcessing?.task.cancel()
            artworkProcessing = nil
            return
        }
        retainedArtwork = image
        let samplingRevision = artworkAdmission.samplingRevision
        let artworkRevision = artworkAdmission.artworkRevision
        let canCaptureArtwork = artworkBinding != nil &&
            artworkBinding == currentArtworkBinding &&
            artworkContextRevision == appearance.displayedArtworkRevision
        let wantsArtwork = canCaptureArtwork && artworkAdmission.needsArtwork(revision: artworkRevision)
        let wantsPalette = artworkAdmission.needsPalette(revision: samplingRevision)
        guard wantsArtwork || wantsPalette else {
            artworkProcessing?.task.cancel()
            artworkProcessing = nil
            return
        }
        let key = ArtworkProcessingKey(
            url: url,
            imageID: ObjectIdentifier(image),
            samplingRevision: samplingRevision,
            artworkRevision: artworkRevision,
            wantsArtwork: wantsArtwork,
            wantsPalette: wantsPalette
        )
        if artworkProcessing?.key == key { return }
        artworkProcessing?.task.cancel()
        let expectedIdentity = artworkIdentity
        let expectedBinding = artworkBinding
        let expectedContext = artworkContextRevision
        let expectedAccess = model.searchAccessGeneration
        let cgImage = wantsPalette ? image.cgImage : nil
        let task = Task { [weak self] in
            defer {
                if self?.artworkProcessing?.key == key {
                    self?.artworkProcessing = nil
                }
            }
            let worker = Task.detached(priority: .utility) { () -> (jpeg: Data?, rgba: [UInt8]?) in
                if Task.isCancelled { return (nil, nil) }
                let jpeg = wantsArtwork ? image.jpegData(compressionQuality: 0.85) : nil
                if Task.isCancelled { return (nil, nil) }
                let rgba = cgImage.flatMap { MusicCoverPaletteSampler.rgba($0) }
                return (jpeg, rgba)
            }
            let processed = await withTaskCancellationHandler {
                await worker.value
            } onCancel: {
                worker.cancel()
            }
            guard !Task.isCancelled, let self,
                  self.artworkProcessing?.key == key,
                  self.isAvailable,
                  self.model.searchAccessGeneration == expectedAccess,
                  self.artworkIdentity == expectedIdentity,
                  self.images[url] === image else { return }
            if wantsArtwork,
               let jpeg = processed.jpeg,
               let expectedBinding,
               self.artworkBinding == expectedBinding,
               expectedBinding == self.currentArtworkBinding,
               self.artworkContextRevision == expectedContext,
               expectedContext == self.appearance.displayedArtworkRevision {
                _ = self.artworkAdmission.captureArtwork(revision: artworkRevision) { jpeg }
            }
            if wantsPalette, let rgba = processed.rgba {
                _ = self.artworkAdmission.sample(revision: samplingRevision) { rgba }
            }
        }
        artworkProcessing = (key, task)
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

    private func updateClock(isPositionSample: Bool = false) {
        reconcileChorus()
        let snapshot = playback.snapshot
        let matching = MusicReferencePlayerFence.ownsAttempt(
            active: model.activeNativeAttemptID, observed: playback.currentAttempt?.id, owner: snapshot.owner
        ) && snapshot.resourceID == model.activeResourceID
        let source = matching ? model.candidate?.byteSource : nil
        if cacheProgressSource != source?.id {
            cacheProgressTask?.cancel()
            cacheProgressSource = source?.id
            cacheProgressTask = source.map { source in
                Task { [weak self] in
                    for await _ in source.updates() {
                        guard !Task.isCancelled, let self, self.cacheProgressSource == source.id else { return }
                        self.updateClock()
                    }
                }
            }
        }
        let progress = source?.progress
        let complete = matching && (model.activeAudioIsCached == true || progress?.complete == true)
        let fraction = progress.flatMap { $0.length > 0 ? Double($0.received) / Double($0.length) : nil } ?? 0
        let buffers: [Range<Double>] = complete ? [0..<1] : fraction > 0 ? [0..<fraction] : []
        let next = MusicPlayerClockState(
            position: model.position, duration: model.duration, rate: playback.preferredRate,
            playing: matching && model.phase == .playing && playback.isOutputPlaying,
            buffers: buffers,
            chorus: chorus.map { MusicReferenceProgressGeometry.fraction($0.start, duration: model.duration) })
        guard clock != next else { return }
        let discreteChanged = clock.duration != next.duration ||
            clock.rate != next.rate ||
            clock.playing != next.playing ||
            clock.buffers != next.buffers ||
            clock.chorus != next.chorus
        clock = next
        if isPositionSample && !discreteChanged {
            positionUpdates.send(next.position)
        } else {
            clockUpdates.send()
        }
    }

    deinit {
        for task in playbackTasks.values { task.cancel() }
        cacheProgressTask?.cancel()
        autoCollapseTask?.cancel()
        for task in modeTasks.values { task.cancel() }
        chorusRead?.task.cancel()
        artworkProcessing?.task.cancel()
        for read in artworkReads.values { read.task.cancel() }
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
    }
}
#endif
