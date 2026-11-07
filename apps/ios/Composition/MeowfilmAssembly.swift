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
    let filmSources: [any FilmSource] = [DoubanFilmSource()]
    private let filmHTTP = FilmHTTP()
    lazy var filmImages = ArtworkImages(maximumBytes: 4 * 1_024 * 1_024) { [filmHTTP] in
        try await filmHTTP.readArtwork($0)
    }
    @Published private(set) var music: MusicViewModel?
    @Published private(set) var filmSettings: FilmSettingsModel?
    @Published private(set) var startupFailed = false
    @Published private(set) var shutdownError: String?
    private(set) var resources: MeowfilmResources?
    private var startup: Task<Void, Never>?
    private var scriptStartup: Task<Void, Never>?
    private var stopping: Task<Void, Never>?
    private var accessTask: Task<Void, Never>?
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
    private var automaticSync: MusicAutomaticLibrarySync?
    private var foreground = UIApplication.shared.applicationState == .active

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
        guard !closed else { return }
        startupFailed = true
    }

    private func install(_ resources: MeowfilmResources) async {
        guard !closed, self.resources == nil else {
            await Self.closeSettings(resources)
            return
        }
        self.resources = resources
        let filmSettings = FilmSettingsModel(rules: resources.filmRules, sources: resources.filmSourceSettings)
        self.filmSettings = filmSettings
        filmSettings.start()
        // Both modules own their guest storage inside Node; no reverse handlers.
        // Configure and start before publishing any music consumer that borrows.
        do { try await resources.scriptSession.configure(handlers: [:]) }
        catch {
            startupFailed = true
            return
        }
        guard !closed else { return }
        await resources.musicScripts.start()
        guard !closed else { return }
        let publicQQ = resources.musicPublicQQ
        self.publicQQ = publicQQ
        let service = NativeMusicService(
            document: resources.musicDocument, scripts: resources.musicScripts,
            accounts: resources.musicAccounts, qq: publicQQ,
            sourceDiscovery: resources.musicSourceDiscoveryCache,
            enabledPlatforms: [.netease, .qq, .kugou, .kuwo, .migu],
            homeSnapshot: resources.musicHomeSnapshot,
            audioFileCache: resources.musicAudioCache,
            clientContexts: resources.musicClients
        )
        self.service = service
        let model = MusicViewModel(service: service, playback: playback, accounts: resources.musicAccounts,
                                   scriptSources: resources.musicScripts)
        model.setPlaybackDisplayCache(resources.musicDisplayCache)
        music = model
        systemMetadata = MusicSystemMetadataCoordinator(model: model, playback: playback, appearance: appearance)
        automaticSync = MusicAutomaticLibrarySync(service: service, onLibraryChanged: { [weak model] in
            await model?.load()
        })
        bindPlayback(model)
        admit(resources)
        scriptStartup = Task {
            // The source owner publishes a failed startup in its observable
            // snapshot. Local rules are initialized independently by their model.
            do { try await resources.filmSourceSettings.start() }
            catch { /* Source state owns the user-visible failure and retry. */ }
        }
    }

    /// The model stays unavailable until every owner acknowledges the single grant.
    private func admit(_ resources: MeowfilmResources) {
        music?.setAvailable(false)
        refreshCommands()
        let generation = musicAccessGeneration, service = service, publicQQ = publicQQ
        accessTask = Task { [weak self] in
            var admitted = true
            do {
                try resources.musicClients.setAccess(available: true, generation: generation)
                try await publicQQ?.setAccess(available: true, generation: generation)
            } catch {
                admitted = false
                resources.musicClients.revokeAccess(generation: generation)
                publicQQ?.revokeAccess(generation: generation)
            }
            await resources.musicAccountGate?.setAvailable(admitted)
            do { try await service?.setAccess(available: admitted, generation: generation) }
            catch { admitted = false }
            guard let self, !self.closed, admitted else { return }
            self.music?.setAvailable(true)
            self.refreshCommands()
            self.refreshAutomaticSync()
            if let model = self.music {
                self.loading = Task { await model.load() }
            }
        }
    }

    private func bindPlayback(_ model: MusicViewModel) {
        do {
            lease = try playback.bind(owner: "music") { [weak self, weak model] event in
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

    func shutdown() {
        guard !closed else { return }
        closed = true
        music?.setAvailable(false)
        filmSettings?.close()
        automaticSync?.stop()
        startup?.cancel(); scriptStartup?.cancel(); loading?.cancel(); commandRefresh?.cancel()
        service?.revokeAccess(generation: musicAccessGeneration)
        publicQQ?.revokeAccess(generation: musicAccessGeneration)
        resources?.musicClients.close()
        systemMetadata?.close(); systemMetadata = nil
        commandObservation?.cancel()
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers.removeAll()
        // Deliver terminal media facts before closing the same business owner.
        playback.stop()
        let model = music, service = service, publicQQ = publicQQ
        let resources = resources, pendingAccess = accessTask, pendingScripts = scriptStartup, lease = lease
        let pendingStartup = startup
        stopping = Task { [weak self] in
            await pendingStartup?.value
            if let resources {
                async let rules: Void = resources.filmRules.close()
                async let sources: Void = resources.filmSourceSettings.close()
                _ = await (rules, sources)
            }
            await pendingScripts?.value
            await pendingAccess?.value
            await model?.close()
            await service?.close()
            await publicQQ?.close()
            do { try await resources?.musicScripts.close() }
            catch { self?.shutdownError = error.localizedDescription }
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
    }

    deinit {
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        startup?.cancel(); scriptStartup?.cancel(); loading?.cancel(); commandRefresh?.cancel()
        let service = service, publicQQ = publicQQ, resources = resources, pendingAccess = accessTask
        let pendingScripts = scriptStartup, stopping = stopping
        service?.revokeAccess(generation: musicAccessGeneration)
        publicQQ?.revokeAccess(generation: musicAccessGeneration)
        resources?.musicClients.close()
        Task {
            if let stopping { await stopping.value; return }
            if let resources {
                await resources.filmRules.close()
                await resources.filmSourceSettings.close()
            }
            await pendingScripts?.value
            await pendingAccess?.value
            await service?.close()
            await publicQQ?.close()
            try? await resources?.musicScripts.close()
            await resources?.scriptSession.close()
            await resources?.musicAccountGate?.close()
        }
    }
}
