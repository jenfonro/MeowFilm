import UIKit
import Combine
import MeowContracts
import MeowPlayback
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicNativeService

@MainActor
final class MeowfilmAssembly: ObservableObject {
    let playback = PlaybackController(volumePreferences: PlaybackVolumePreferences())
    let appearance = MusicAppearancePreferencesStore()
    @Published private(set) var music: MusicViewModel?
    @Published private(set) var startupFailed = false
    private var resources: MeowfilmResources?
    private var startup: Task<Void, Never>?
    private var accessTask: Task<Void, Never>?
    private var loading: Task<Void, Never>?
    private var accessEpoch = UUID()
    private var accessGeneration: UInt64 = 0
    private var protectedAvailable = false
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
        // Synchronous withdrawal precedes any asynchronous acknowledgement.
        observers.append(center.addObserver(
            forName: UIApplication.protectedDataWillBecomeUnavailableNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.setProtection(false) }
        })
        observers.append(center.addObserver(
            forName: UIApplication.protectedDataDidBecomeAvailableNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.setProtection(UIApplication.shared.isProtectedDataAvailable) }
        })
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
                guard !Task.isCancelled else { return }
                await self?.install(resources)
            } catch {
                await self?.failedStartup()
            }
        }
    }

    private func failedStartup() {
        guard !closed else { return }
        startupFailed = true
    }

    private func install(_ resources: MeowfilmResources) {
        guard !closed, self.resources == nil else { return }
        self.resources = resources
        let publicQQ = NativeMusicQQPublicService(
            storeFactory: resources.documentFactory(MusicQQPublicPersistenceDocument.namespace),
            requestAdmission: resources.musicRequests
        )
        self.publicQQ = publicQQ
        let service = NativeMusicService(
            document: resources.musicDocument, accounts: resources.musicAccounts, qq: publicQQ,
            enabledPlatforms: [.netease, .qq, .kugou],
            homeSnapshot: MusicNativeHomeSnapshotDocument(
                storeFactory: resources.documentFactory(MusicNativeHomeSnapshotDocument.namespace)
            ),
            audioFileCache: resources.musicAudioCache, requestAdmission: resources.musicRequests
        )
        self.service = service
        let model = MusicViewModel(service: service, playback: playback, accounts: resources.musicAccounts)
        model.setPlaybackDisplayCache(resources.musicDisplayCache)
        music = model
        systemMetadata = MusicSystemMetadataCoordinator(model: model, playback: playback, appearance: appearance)
        automaticSync = MusicAutomaticLibrarySync(service: service, onLibraryChanged: { [weak model] in
            await model?.load()
        })
        bindPlayback(model)
        setProtection(UIApplication.shared.isProtectedDataAvailable)
    }

    private func setProtection(_ available: Bool) {
        let epoch = UUID()
        accessEpoch = epoch
        protectedAvailable = false
        guard !closed, let resources else { return }
        guard accessGeneration <= MeowfilmIdentity.maximumAccessGeneration - 2 else {
            shutdown(); return
        }
        accessGeneration += 2
        let generation = accessGeneration
        if !available {
            service?.revokeAccess(generation: generation)
            publicQQ?.revokeAccess(generation: generation)
        }
        music?.setAvailable(false)
        automaticSync?.stop()
        loading?.cancel()
        refreshCommands()
        let service = service, publicQQ = publicQQ, previous = accessTask
        accessTask = Task { [weak self] in
            await previous?.value
            await resources.musicAccountGate?.setAvailable(available)
            do { try await publicQQ?.setAccess(available: available, generation: generation) }
            catch { publicQQ?.revokeAccess(generation: generation) }
            var admitted = available
            do { try await service?.setAccess(available: available, generation: generation) }
            catch { admitted = false }
            guard let self, !self.closed, self.accessEpoch == epoch, available else { return }
            self.protectedAvailable = admitted
            self.music?.setAvailable(admitted)
            self.refreshCommands()
            self.refreshAutomaticSync()
            if admitted, let model = self.music {
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
        let canPrevious = protectedAvailable && music.canPrevious
        let canNext = protectedAvailable && music.canNext
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
            isAvailable: protectedAvailable && music.isAvailable && !music.isClosed && !closed,
            accessGeneration: music.accountAccessGeneration, isForeground: foreground
        )
    }

    private func shutdown() {
        guard !closed else { return }
        closed = true; protectedAvailable = false; accessEpoch = UUID()
        automaticSync?.stop()
        startup?.cancel(); loading?.cancel(); commandRefresh?.cancel()
        service?.revokeAccess(generation: accessGeneration)
        publicQQ?.revokeAccess(generation: accessGeneration)
        systemMetadata?.close(); systemMetadata = nil
        commandObservation?.cancel()
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers.removeAll()
        // Deliver terminal media facts before closing the same business owner.
        playback.stop()
        let model = music, service = service, publicQQ = publicQQ
        let resources = resources, pendingAccess = accessTask, lease = lease
        Task {
            await pendingAccess?.value
            await model?.close()
            await service?.close()
            await publicQQ?.close()
            lease?.cancel()
            await resources?.musicAccountGate?.close()
        }
    }

    deinit {
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        startup?.cancel(); loading?.cancel(); commandRefresh?.cancel()
        let service = service, publicQQ = publicQQ, resources = resources, pendingAccess = accessTask
        service?.revokeAccess(generation: accessGeneration)
        publicQQ?.revokeAccess(generation: accessGeneration)
        Task {
            await pendingAccess?.value
            await service?.close()
            await publicQQ?.close()
            await resources?.musicAccountGate?.close()
        }
    }
}
