import UIKit
import Combine
import MeowContracts
import MeowPlayback
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicNativeService
import MeowFilmFeature
import MeowUI
import MeowRuntime

/// Music services are admitted once per process and revoked only on shutdown.
/// Documents, credentials and device sessions stay readable after first unlock,
/// so locking the device does not withdraw playback.
private let musicAccessGeneration: UInt64 = 1

@MainActor
final class MeowfilmAssembly: ObservableObject {
    let playback = PlaybackController(volumePreferences: PlaybackVolumePreferences())
    let appearance = MusicAppearancePreferencesStore()
    let filmLibrary = FilmLibrary()
    lazy var filmSources: [any FilmSource] = [DoubanFilmSource { [weak self] in
        guard let resources = await self?.resources else { throw FilmHomeError.network }
        return try await resources.scriptSession.connection()
    }]
    private let filmHTTP = FilmHTTP()
    lazy var filmImages = ArtworkImages(maximumBytes: 4 * 1_024 * 1_024) { [weak self, filmHTTP] in
        if $0.scheme == nil {
            guard let metadata = await self?.resources?.filmMetadata else { throw FilmSearchError.unavailable }
            return try await metadata.readImage($0)
        }
        return try await filmHTTP.readArtwork($0)
    }
    @Published private(set) var music: MusicViewModel?
    @Published private(set) var filmSettings: FilmSettingsModel?
    private(set) var filmSearch: FilmSearchService?
    @Published private(set) var startupFailed = false
    @Published private(set) var shutdownError: String?
    private(set) var resources: MeowfilmResources?
    private var startup: Task<Void, Never>?
    private var scriptStartup: Task<Void, Never>?
    private var stopping: Task<Void, Never>?
    private var loading: Task<Void, Never>?
    private var closed = false
    private var observers: [NSObjectProtocol] = []
    private var service: NativeMusicService?
    private var publicQQ: NativeMusicQQPublicService?
    private var lease: PlaybackOwnerLease?
    private var commandObservation: AnyCancellable?
    private var commandRefresh: Task<Void, Never>?
    private var navigationCommands: (Bool, Bool)?
    private var systemMetadata: MusicSystemMetadataCoordinator?
    // Capture teardown while isolated; deinit cannot read the Published getter.
    private var closeMusic: (@MainActor @Sendable () async -> Void)?
    private var automaticSync: MusicAutomaticLibrarySync?
    private var foreground = UIApplication.shared.applicationState == .active
    private(set) var filmPlayback: FilmPlaybackModel?
    private var filmMedia: MeowfilmFilmMedia?
    private var filmBackground: Task<Void, Never>?
    var restoreFilmPlaybackUI: (@MainActor (UUID) async -> Bool)? {
        didSet { filmMedia?.restoreUI = restoreFilmPlaybackUI }
    }

    init() {
        let center = NotificationCenter.default
        observers.append(center.addObserver(
            forName: UIApplication.willTerminateNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.shutdown() }
        })
        observers.append(center.addObserver(
            forName: UIApplication.willResignActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.foreground = false
                self?.refreshAutomaticSync()
            }
        })
        observers.append(center.addObserver(
            forName: UIApplication.didBecomeActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.foreground = true
                self?.refreshAutomaticSync()
                guard let self, let model = self.filmPlayback else { return }
                let previous = self.filmBackground
                self.filmBackground = Task {
                    await previous?.value
                    await model.applicationDidBecomeActive()
                }
            }
        })
        observers.append(center.addObserver(
            forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, let model = self.filmPlayback else { return }
                let previous = self.filmBackground
                self.filmBackground = Task {
                    await previous?.value
                    await model.applicationDidEnterBackground()
                }
            }
        })
    }

    func start() {
        guard startup == nil, resources == nil, !closed else { return }
        startup = Task.detached(priority: .userInitiated) { [weak self] in
            do {
                let resources = try MeowfilmResources()
                guard !Task.isCancelled, let self else {
                    await Self.closeSettings(resources)
                    return
                }
                await self.install(resources)
            } catch {
                await self?.failedStartup()
            }
        }
    }

    private func failedStartup() {
        guard !closed, !Task.isCancelled else { return }
        startupFailed = true
    }

    private func install(_ resources: MeowfilmResources) async {
        guard !closed, !Task.isCancelled, self.resources == nil else {
            await Self.closeSettings(resources)
            return
        }
        self.resources = resources
        filmSearch = FilmSearchService(sources: resources.filmSourceSettings,
            connection: { try await resources.scriptSession.connection() })
        let publicQQ = resources.musicPublicQQ
        self.publicQQ = publicQQ
        let tmdb = FilmTMDBSettingsModel(service: resources.filmMetadata) { [weak filmSearch = self.filmSearch] in
            filmSearch?.metadataSettingsChanged()
        }
        let playbackSettings = FilmPlaybackSettingsModel(transport: FilmPlaybackTransport {
            try await resources.scriptSession.connection()
        })
        playbackSettings.playbackSnapshot = { [weak self] in self?.filmPlayback?.snapshot }
        let filmSettings = FilmSettingsModel(rules: resources.filmRules, sources: resources.filmSourceSettings,
                                             tmdb: tmdb, playback: playbackSettings)
        self.filmSettings = filmSettings
        filmSettings.start()
        guard !closed, !Task.isCancelled else { return }
        await resources.musicScripts.start()
        guard !closed, !Task.isCancelled else { return }
        scriptStartup = Task {
            // The source owner publishes a failed startup in its observable
            // snapshot. Node rules are initialized independently by their model.
            do { try await resources.filmSourceSettings.start() }
            catch { /* Source state owns the user-visible failure and retry. */ }
        }
        do {
            try resources.musicClients.setAccess(available: true, generation: musicAccessGeneration)
            try await publicQQ.setAccess(available: true, generation: musicAccessGeneration)
        } catch {
            resources.musicClients.revokeAccess(generation: musicAccessGeneration)
            publicQQ.revokeAccess(generation: musicAccessGeneration)
            await resources.musicAccountGate?.setAvailable(false)
            failedStartup()
            return
        }
        guard !closed, !Task.isCancelled else { return }
        await resources.musicAccountGate?.setAvailable(true)
        guard !closed, !Task.isCancelled else { return }
        let service = NativeMusicService(
            document: resources.musicDocument, scripts: resources.musicScripts,
            accounts: resources.musicAccounts, qq: publicQQ,
            sourceDiscovery: resources.musicSourceDiscoveryCache,
            enabledPlatforms: [.netease, .qq, .kugou, .kuwo, .migu],
            homeSnapshot: resources.musicHomeSnapshot,
            audioFileCache: resources.musicAudioCache,
            clientContexts: resources.musicClients
        )
        await prepareMusic(service, prepareAccounts: {
            guard let accounts = resources.musicAccounts else { throw MusicServiceError.accessUnavailable }
            try await accounts.prepareLocalAccounts()
        }, makeModel: {
            let model = MusicViewModel(service: service, playback: playback, accounts: resources.musicAccounts,
                                       scriptSources: resources.musicScripts)
            model.setPlaybackDisplayCache(resources.musicDisplayCache)
            return model
        })
    }

    /// The App and host tests share this publication boundary. Account preparation
    /// is local only; the service retains the pre-read home for the first page.
    func prepareMusic(_ service: NativeMusicService,
                      prepareAccounts: () async throws -> Void,
                      makeModel: () -> MusicViewModel) async {
        guard !closed, !Task.isCancelled else { await service.close(); return }
        self.service = service
        do {
            try await service.setAccess(available: true, generation: musicAccessGeneration)
            try Task.checkCancellation()
            try await service.preparePlaybackFeedback()
            try await prepareAccounts()
            try Task.checkCancellation()
            guard !closed else { return }
            _ = try await service.homeSourceSelection()
            _ = try await service.restoredHome()
            guard !closed, !Task.isCancelled else { return }
            let model = makeModel()
            model.setAvailable(true)
            systemMetadata = MusicSystemMetadataCoordinator(model: model, playback: playback, appearance: appearance)
            automaticSync = MusicAutomaticLibrarySync(service: service, onLibraryChanged: { [weak model] in
                await model?.load()
            })
            bindPlayback(model)
            closeMusic = { await model.close() }
            music = model
            guard !closed else { return }
            refreshCommands()
            refreshAutomaticSync()
            loading = Task { await model.load() }
        } catch {
            failedStartup()
        }
    }

    private func bindPlayback(_ model: MusicViewModel) {
        do {
            lease = try playback.bind(owner: "music", onPlayPause: { [weak model] attempt, command in
                model?.acceptPlayPause(command, attempt: attempt) ?? false
            }, onOutputUnavailable: { [weak model] in
                model?.onOutputUnavailable()
            }) { [weak self, weak model] event in
                model?.onPlayback(event)
                self?.refreshCommands()
            }
        } catch {
            model.error = MusicServiceError.unavailable.localizedDescription
            return
        }
        commandObservation = Publishers.Merge(model.playbackChanges, model.accessChanges).sink { [weak self] _ in
            self?.commandRefresh?.cancel()
            self?.commandRefresh = Task { @MainActor [weak self] in
                guard !Task.isCancelled else { return }
                self?.refreshCommands()
                self?.refreshAutomaticSync()
            }
        }
        refreshCommands()
    }

    private func refreshCommands() {
        guard let music, let lease else { return }
        let canPrevious = music.canPrevious
        let canNext = music.canNext
        if let current = navigationCommands, current.0 == canPrevious, current.1 == canNext { return }
        navigationCommands = (canPrevious, canNext)
        let previous: (@MainActor (PlayAttempt) -> Bool)?
        if canPrevious {
            previous = { [weak music] attempt in music?.acceptPrevious(attempt) ?? false }
        } else { previous = nil }
        let next: (@MainActor (PlayAttempt) -> Bool)?
        if canNext {
            next = { [weak music] attempt in music?.acceptNext(attempt) ?? false }
        } else { next = nil }
        lease.updateCommands(.init(previous: previous, next: next))
    }

    private func refreshAutomaticSync() {
        guard let music else { automaticSync?.stop(); return }
        automaticSync?.activate(
            isAvailable: music.isAvailable && !music.isClosed && !closed,
            accessGeneration: music.accountAccessGeneration, isForeground: foreground
        )
    }

    func makeFilmPlayback(item: FilmItem, kind: FilmPlaybackKind) throws -> FilmPlaybackModel {
        guard !closed, let resources else { throw FilmPlaybackError(code: "CLOSED") }
        guard let provider = FilmMetadataProvider(rawValue: item.sourceID), (kind == .movie || kind == .tv) else {
            // A catalog item has no script installation/detail prototype. The
            // search route supplies its full request rather than guessing one.
            throw FilmPlaybackError(code: "INVALID_REQUEST")
        }
        let target = FilmPlaybackTarget(id: "\(provider.rawValue):\(kind.rawValue):\(item.id)",
                                        kind: kind, title: item.title, metadataProvider: provider, metadataID: item.id)
        return try makeFilmPlayback { [weak self] in
            guard let self else { throw FilmPlaybackError(code: "CLOSED") }
            return try await self.filmRequest(target: target, resources: resources)
        }
    }

    func makeFilmPlayback(context: FilmSearchContext) throws -> FilmPlaybackModel {
        guard !closed, let resources else { throw FilmPlaybackError(code: "CLOSED") }
        return try makeFilmPlayback(searchContext: context) { [weak self, context] in
            guard let self else { throw FilmPlaybackError(code: "CLOSED") }
            let sources = try await self.filmPlaybackSources(resources: resources)
            return try await context.playbackRequest(sources: sources, owner: resources.scriptSession.connection())
        }
    }

    /// The final search handoff can supply its complete request and cache seeds
    /// without a second search/cache adapter in App composition.
    func makeFilmPlayback(searchContext: FilmSearchContext? = nil,
                          request: @escaping @Sendable () async throws -> FilmPlaybackRequest) throws -> FilmPlaybackModel {
        guard !closed, let resources else { throw FilmPlaybackError(code: "CLOSED") }
        if filmMedia == nil {
            let media = try MeowfilmFilmMedia(playback: playback, willCommit: { [weak self] in
                self?.music?.mediaHandoffCompletion()
            })
            media.restoreUI = restoreFilmPlaybackUI
            filmMedia = media
        }
        return FilmPlaybackModel(transport: FilmPlaybackTransport {
            try await resources.scriptSession.connection()
        }, media: filmMedia!, openExternalURL: { url in
            await withCheckedContinuation { continuation in
                UIApplication.shared.open(url, options: [:]) { continuation.resume(returning: $0) }
            }
        }, searchContext: searchContext, request: request)
    }

    private func filmRequest(target: FilmPlaybackTarget, resources: MeowfilmResources) async throws -> FilmPlaybackRequest {
        FilmPlaybackRequest(target: target, sources: try await filmPlaybackSources(resources: resources))
    }

    private func filmPlaybackSources(resources: MeowfilmResources) async throws -> [FilmPlaybackSource] {
        await scriptStartup?.value
        try Task.checkCancellation()
        guard !closed else { throw FilmPlaybackError(code: "CLOSED") }
        try await resources.filmSourceSettings.refresh()
        try Task.checkCancellation()
        guard !closed else { throw FilmPlaybackError(code: "CLOSED") }
        let catalog = await resources.filmSourceSettings.snapshot
        let sources = catalog.videos.compactMap { video -> FilmPlaybackSource? in
            guard let cat = catalog.cats.first(where: { $0.installationId.utf8.elementsEqual(video.key.installationId.utf8) }),
                  cat.canDetect else { return nil }
            return FilmSearchSite(cat: cat, source: video).playbackSource
        }
        return sources
    }

    func activateFilm(_ model: FilmPlaybackModel) async throws {
        await filmBackground?.value
        guard !closed else { throw FilmPlaybackError(code: "CLOSED") }
        if let current = filmPlayback, current !== model {
            await current.close()
            guard current.canLeave else { throw current.actionError ?? FilmPlaybackError(code: "MEDIA_UNAVAILABLE") }
        }
        try Task.checkCancellation()
        guard !closed else { throw FilmPlaybackError(code: "CLOSED") }
        filmPlayback = model
        filmMedia?.model = model
    }

    func leaveFilm(allowSystemOwner: Bool = true) async throws {
        guard let model = filmPlayback else { return }
        await model.close(allowSystemOwner: allowSystemOwner)
        guard model.canLeave || allowSystemOwner && model.hasSystemOwner else {
            throw model.actionError ?? FilmPlaybackError(code: "MEDIA_RELEASE_FAILED")
        }
    }

    func shutdown() {
        guard !closed else { return }
        closed = true
        music?.prepareForClose()
        filmSettings?.close()
        automaticSync?.stop()
        startup?.cancel(); scriptStartup?.cancel(); loading?.cancel(); commandRefresh?.cancel()
        systemMetadata?.close(); systemMetadata = nil
        commandObservation?.cancel()
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers.removeAll()
        // Deliver terminal media facts before closing the same business owner.
        // Film's async stop delivers the captured native facts before closing
        // its Node session. Stopping only music here must not bypass that drain.
        playback.stop(owner: "music")
        let model = music, service = service, publicQQ = publicQQ
        let resources = resources, pendingScripts = scriptStartup, lease = lease
        let pendingStartup = startup
        let filmSearch = filmSearch, film = filmPlayback, filmMedia = filmMedia, filmBackground = filmBackground
        stopping = Task { [weak self] in
            await model?.close()
            await service?.prepareForClose()
            // Local facts and frozen ends now belong to the feedback owner.
            // Never wait for a network ACK before retiring product access.
            service?.revokeAccess(generation: musicAccessGeneration)
            publicQQ?.revokeAccess(generation: musicAccessGeneration)
            resources?.musicClients.close()
            await pendingStartup?.value
            await filmSearch?.close()
            await filmBackground?.value
            await film?.close()
            if let film, !film.isReleased {
                self?.shutdownError = (film.actionError ?? FilmPlaybackError(code: "MEDIA_RELEASE_FAILED")).localizedDescription
            }
            do { try await filmMedia?.close() }
            catch { if self?.shutdownError == nil { self?.shutdownError = error.localizedDescription } }
            if let resources {
                async let rules: Void = resources.filmRules.close()
                async let sources: Void = resources.filmSourceSettings.close()
                _ = await (rules, sources)
            }
            await pendingScripts?.value
            await service?.close()
            await publicQQ?.close()
            do { try await resources?.musicScripts.close() }
            catch { if self?.shutdownError == nil { self?.shutdownError = error.localizedDescription } }
            await resources?.scriptSession.close()
            lease?.cancel()
            await resources?.musicAccountGate?.close()
        }
    }

    func waitForStartup() async {
        await startup?.value
        await scriptStartup?.value
    }

    func waitForShutdown() async { await stopping?.value }

    /// Resources produced after cancellation still belong to this start attempt.
    /// No service may survive just because its install was never published.
    private nonisolated static func closeSettings(_ resources: MeowfilmResources) async {
        await resources.filmRules.close()
        await resources.filmSourceSettings.close()
        try? await resources.musicScripts.close()
        await resources.scriptSession.close()
        resources.musicClients.close()
        await resources.musicPublicQQ.close()
        await resources.musicAccountGate?.close()
    }

    deinit {
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        startup?.cancel(); scriptStartup?.cancel(); loading?.cancel(); commandRefresh?.cancel()
        let closeMusic = closeMusic, service = service, publicQQ = publicQQ, resources = resources, lease = lease
        let pendingScripts = scriptStartup, stopping = stopping, filmSearch = filmSearch
        let film = filmPlayback, filmMedia = filmMedia, filmBackground = filmBackground
        Task { @MainActor in
            if let stopping { await stopping.value; return }
            await closeMusic?()
            await service?.prepareForClose()
            service?.revokeAccess(generation: musicAccessGeneration)
            publicQQ?.revokeAccess(generation: musicAccessGeneration)
            resources?.musicClients.close()
            await filmSearch?.close()
            await filmBackground?.value
            await film?.close()
            if let film, !film.isReleased { NSLog("Film session shutdown failed") }
            do { try await filmMedia?.close() }
            catch { NSLog("Film media shutdown failed") }
            if let resources {
                await resources.filmRules.close()
                await resources.filmSourceSettings.close()
            }
            await pendingScripts?.value
            await service?.close()
            await publicQQ?.close()
            try? await resources?.musicScripts.close()
            await resources?.scriptSession.close()
            lease?.cancel()
            await resources?.musicAccountGate?.close()
        }
    }
}

/// The product supplies only this adapter to FilmFeature. Credentials remain in
/// the runtime receipt/private native input, and one stable video lease owns the
/// decoder, system commands and surface across inline/fullscreen/PiP lifetimes.
@MainActor
private final class MeowfilmFilmMedia: FilmPlaybackMediaControlling {
    // Only physically live or draining transports. The native receipt callback
    // removes its exact record after decoder/PiP close, before the remote ACK.
    private var undrained: [Media] = []
    weak var model: FilmPlaybackModel?
    var restoreUI: (@MainActor (UUID) async -> Bool)?
    private let playback: PlaybackController
    private var lease: PlaybackVideoLease!
    private var pending: Media?
    private var current: Media?
    private var stopping = false

    private final class Media {
        let sessionID: UUID
        let attemptID: String
        let receive: @MainActor (FilmMediaEvent) -> Void
        var nativeAttempt: PlayAttempt?
        var state = FilmMediaState()
        init(input: FilmPlaybackMediaInput, receive: @escaping @MainActor (FilmMediaEvent) -> Void) {
            sessionID = input.sessionID; attemptID = input.attemptID; self.receive = receive
            state.sessionID = input.sessionID; state.attemptID = input.attemptID
            state.position = input.position; state.paused = input.paused
        }
    }

    init(playback: PlaybackController,
         willCommit: @escaping @MainActor () -> (@MainActor () -> Void)?) throws {
        self.playback = playback
        lease = try playback.bindVideo(owner: "film", commands: .init(
            previous: { [weak self] attempt in self?.navigate(attempt, forward: false) ?? false },
            next: { [weak self] attempt in self?.navigate(attempt, forward: true) ?? false }
        ), willCommit: willCommit) { [weak self] event in self?.receive(event) }
        lease.restorePictureInPictureUI = { [weak self] in
            guard let self, let current else { return false }
            return await restoreUI?(current.sessionID) ?? false
        }
    }

    func play(_ input: FilmPlaybackMediaInput, preservePosition: Bool,
              waitForCommit: @escaping @MainActor () async throws -> Void,
              canCommit: @escaping @MainActor () -> Bool,
              release: @escaping @MainActor () async throws -> Void,
              receive: @escaping @MainActor (FilmMediaEvent) -> Void) async throws {
        let record = Media(input: input, receive: receive)
        let native: PlaybackVideoInput
        do {
            native = try PlaybackVideoInput(resourceID: input.resourceID, url: input.url, title: input.title,
                format: .init(runtimeValue: input.format),
                startPosition: input.position, decryption: try Self.decryption(input.drm))
        } catch { throw Self.failure(error) }
        let receipt = try PlaybackVideoMediaLease(attemptID: input.attemptID, input: native) { [weak self, record] in
            self?.undrained.removeAll { $0 === record }
            try await release()
        }
        undrained.append(record)
        pending = record
        defer { if pending === record { pending = nil } }
        do {
            record.nativeAttempt = try await lease.play(receipt, paused: input.paused, preservePosition: preservePosition,
                                                       waitForCommit: waitForCommit, canCommit: canCommit)
        } catch {
            do { try await receipt.close() } catch { throw FilmPlaybackError(code: "MEDIA_RELEASE_FAILED") }
            throw Self.failure(error)
        }
    }

    private func receive(_ event: PlaybackVideoEvent) {
        let record = pending?.attemptID == event.attemptID ? pending : current?.attemptID == event.attemptID ? current : nil
        guard let record else { return }
        record.nativeAttempt = event.nativeAttempt ?? record.nativeAttempt
        record.state.position = event.snapshot.position
        record.state.duration = event.snapshot.duration ?? 0
        record.state.paused = !event.wantsPlayback
        record.state.buffering = event.snapshot.phase == .preparing || event.snapshot.phase == .buffering
        record.state.rate = playback.preferredRate; record.state.volume = playback.volume
        record.state.pictureInPicture = event.pictureInPicture == .active
        record.state.pictureInPictureAvailable = [.possible, .active].contains(event.pictureInPicture)
        switch event.kind {
        case .preparing, .prepared: return
        case .started:
            current = record
            if pending === record { pending = nil }
            record.receive(.state(record.state))
            record.receive(.started(attemptID: record.attemptID))
        case .progress, .paused:
            record.receive(.state(record.state))
        case .stopped:
            record.receive(.state(record.state))
            // Own replacement commits are atomic and already carry the new
            // film fence. Music takeover/owner withdrawal has no film fence.
            if !stopping, playback.currentAttempt?.owner != lease.owner {
                record.receive(.ownerEnded(attemptID: record.attemptID))
            }
        case .failed:
            record.receive(.state(record.state))
            record.receive(.failed(attemptID: record.attemptID, error: Self.failure(event.error ?? .outputFailed)))
        case .ended:
            record.receive(.state(record.state))
            record.receive(.ended(attemptID: record.attemptID))
        case .pictureInPictureChanged:
            record.receive(.state(record.state))
            if event.pictureInPicture == .failed {
                model?.reportMediaControlError(Self.failure(PlaybackVideoError.pictureInPictureFailed))
            }
        case .systemOwnerEnded:
            record.receive(.state(record.state))
            record.receive(.systemOwnerEnded(attemptID: record.attemptID))
        }
    }

    private func navigate(_ attempt: PlayAttempt, forward: Bool) -> Bool {
        guard current?.nativeAttempt == attempt, model?.sessionID == current?.sessionID else { return false }
        return model?.navigateFromSystem(forward: forward) ?? false
    }
    func cancelPending(sessionID: UUID) async throws {
        guard pending?.sessionID == sessionID else { return }
        do { try await lease.cancelPending() } catch { throw Self.failure(error) }
    }
    func stop(sessionID: UUID) async throws {
        guard pending?.sessionID == sessionID || current?.sessionID == sessionID else { return }
        stopping = true
        defer { stopping = false }
        do { try await lease.stop() } catch { throw Self.failure(error) }
        if pending?.sessionID == sessionID { pending = nil }
        if current?.sessionID == sessionID { current = nil }
    }
    func isLocallyDrained(sessionID: UUID) -> Bool {
        !undrained.contains { $0.sessionID == sessionID }
    }
    func close() async throws { try await lease.close() }
    private func attempt(_ sessionID: UUID) -> PlayAttempt? {
        guard current?.sessionID == sessionID, lease.currentAttempt == current?.nativeAttempt else { return nil }
        return current?.nativeAttempt
    }
    func isCurrent(sessionID: UUID, attemptID: String) -> Bool {
        current?.attemptID == attemptID && attempt(sessionID) != nil
    }
    func pause(sessionID: UUID, attemptID: String) -> Bool {
        guard isCurrent(sessionID: sessionID, attemptID: attemptID), playback.wantsPlayback,
              let value = attempt(sessionID) else { return false }
        return playback.pause(attempt: value)
    }
    func resume(sessionID: UUID, attemptID: String) {
        guard isCurrent(sessionID: sessionID, attemptID: attemptID), !playback.wantsPlayback,
              let value = attempt(sessionID) else { return }
        playback.resume(attempt: value)
    }
    func seek(sessionID: UUID, to position: Double) {
        if let value = attempt(sessionID) { playback.seek(to: position, attempt: value) }
    }
    func setRate(sessionID: UUID, rate: Double) {
        if let value = attempt(sessionID), playback.setPreferredRate(rate, attempt: value), let current {
            current.state.rate = playback.preferredRate; current.receive(.state(current.state))
        }
    }
    func setVolume(sessionID: UUID, volume: Double) {
        if let value = attempt(sessionID), playback.setVolume(volume, attempt: value), let current {
            current.state.volume = playback.volume; current.receive(.state(current.state))
        }
    }
    func startPictureInPicture(sessionID: UUID) {
        if let value = attempt(sessionID), !lease.startPictureInPicture(attempt: value) {
            model?.reportMediaControlError(.init(code: "PICTURE_IN_PICTURE_UNAVAILABLE"))
        }
    }
    func stopPictureInPicture(sessionID: UUID) {
        if let value = attempt(sessionID) { lease.stopPictureInPicture(attempt: value) }
    }
    func hasSystemOwner(sessionID: UUID) -> Bool { attempt(sessionID) != nil && lease.canContinueInBackground }
    func makeVideoView(sessionID: UUID) -> UIView { lease.surface }
    func setVideoScale(sessionID: UUID, scale: FilmVideoScale) {
        switch scale {
        case .fit: lease.surface.aspect = .fit
        case .fill: lease.surface.aspect = .fill
        case .stretch: lease.surface.aspect = .stretch
        }
    }

    private static func decryption(_ value: RuntimeValue?) throws -> PlaybackVideoDecryption? {
        guard let value, value != .null else { return nil }
        guard case .object(let fields) = value,
              fields["type"] == .string("clearkey") || fields["keySystem"] == .string("org.w3.clearkey") else {
            throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED")
        }
        var keys: [String: String] = [:]
        if case .object(let values)? = fields["clearKeys"] {
            for (kid, value) in values {
                guard case .string(let key) = value else { throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED") }
                keys[kid] = key
            }
        } else if case .array(let values)? = fields["keys"] {
            for value in values {
                guard case .object(let row) = value, row["kty"] == .string("oct"),
                      case .string(let kid)? = row["kid"], case .string(let key)? = row["k"], keys[kid] == nil else {
                    throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED")
                }
                keys[kid] = key
            }
        } else { throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED") }
        do { return try PlaybackVideoDecryption(clearKeys: keys) }
        catch { throw FilmPlaybackError(code: "MEDIA_DRM_UNSUPPORTED") }
    }

    private static func failure(_ error: Error) -> FilmPlaybackError {
        if let error = error as? FilmPlaybackError { return error }
        if error is CancellationError { return .init(code: "CANCELLED") }
        guard let error = error as? PlaybackVideoError else { return .init(code: "MEDIA_INPUT_INVALID") }
        let code: String
        switch error {
        case .cancelled, .staleAttempt: code = "CANCELLED"
        case .invalidInput: code = "MEDIA_INPUT_INVALID"
        case .inputFailed: code = "MEDIA_READ_FAILED"
        case .unsupportedFormat: code = "MEDIA_FORMAT_UNSUPPORTED"
        case .decodingFailed: code = "MEDIA_DECODING_FAILED"
        case .outputFailed: code = "MEDIA_OUTPUT_FAILED"
        case .seekFailed: code = "MEDIA_SEEK_FAILED"
        case .timedOut: code = "TIMEOUT"
        case .decryptionFailed: code = "MEDIA_DECRYPTION_FAILED"
        case .resourceReleaseFailed: code = "MEDIA_RELEASE_FAILED"
        case .pictureInPictureUnavailable: code = "PICTURE_IN_PICTURE_UNAVAILABLE"
        case .pictureInPictureFailed: code = "PICTURE_IN_PICTURE_FAILED"
        }
        return .init(code: code)
    }
}
