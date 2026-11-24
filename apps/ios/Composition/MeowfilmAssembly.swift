import UIKit
import Combine
import MeowContracts
import MeowDebug
import MeowPlayback
import MeowPlaybackVideo
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicNativeService
import MeowFilmFeature
import MeowUI
import MeowRuntime
import MeowNovelFeature

@MainActor
final class MeowfilmAssembly: ObservableObject {
    let dataRootOverride: URL?
    let playerPreferences = FilmPlayerPreferencesStore()
    let volumePreferences = PlaybackVolumePreferences()
    private var initializedPlayback: PlaybackController?
    var playback: PlaybackController {
        if let existing = initializedPlayback { return existing }
        let created = PlaybackController(volumePreferences: volumePreferences, videoDecoderFactory: {
            [playerPreferences] engine in try makePlaybackVideoDecoder(engine: engine ?? playerPreferences.preferences.engine)
        })
        initializedPlayback = created
        return created
    }
    let appearance = MusicAppearancePreferencesStore()
    let filmLibrary = FilmLibrary()
    lazy var filmSources: [any FilmSource] = [DoubanFilmSource { [weak self] in
        guard let resources = await self?.resources else { throw FilmHomeError.network }
        return try await resources.scriptSession.connection()
    }]
    private let filmHTTP = FilmHTTP()
    private var initializedFilmImages: ArtworkImages?
    var filmImages: ArtworkImages {
        if let existing = initializedFilmImages { return existing }
        let created = ArtworkImages(maximumBytes: 4 * 1_024 * 1_024) { [weak self, filmHTTP] in
            if $0.scheme == nil {
                guard let metadata = await self?.resources?.filmMetadata else { throw FilmSearchError.unavailable }
                return try await metadata.readImage($0)
            }
            return try await filmHTTP.readArtwork($0)
        }
        initializedFilmImages = created
        return created
    }
    var initializedFilmImagesForDiagnostics: ArtworkImages? { initializedFilmImages }
    let musicImages = MusicArtworkImages()
    @Published var music: MusicViewModel?
    private(set) var filmSettings: FilmSettingsModel?
    private(set) var filmSearch: FilmSearchService?
    @Published private(set) var novel: NovelFeature?
    private var closeNovel: (@MainActor @Sendable () async -> Void)?
    @Published private(set) var startupFailed = false
    private(set) var shutdownError: String?
    private(set) var resources: MeowfilmResources?
    private var startup: Task<Void, Never>?
    var scriptStartup: Task<Void, Never>?
    private var runtimeAvailability: Task<Void, Never>?
    private var stopping: Task<Void, Never>?
    var loading: Task<Void, Never>?
    var closed = false
    private var observers: [NSObjectProtocol] = []
    var service: NativeMusicService?
    var lease: PlaybackOwnerLease?
    var commandObservation: AnyCancellable?
    var commandRefresh: Task<Void, Never>?
    var navigationCommands: (Bool, Bool)?
    var systemMetadata: MusicSystemMetadataCoordinator?
    // Capture teardown while isolated; deinit cannot read the Published getter.
    var closeMusic: (@MainActor @Sendable () async -> Void)?
    var automaticSync: MusicAutomaticLibrarySync?
    var foreground = UIApplication.shared.applicationState == .active
    var filmPlayback: FilmPlaybackModel?
    var filmMedia: MeowfilmFilmMedia?
    var filmMediaPlayback: FilmPlaybackModel? { filmMedia?.currentModel }
    var filmModels: [FilmPlaybackModel] {
        var identities = Set<ObjectIdentifier>()
        return ([filmPlayback].compactMap { $0 } + (filmMedia?.models ?? [])).filter {
            identities.insert(ObjectIdentifier($0)).inserted
        }
    }
    var activeFilmModels: [FilmPlaybackModel] {
        filmModels.filter { !$0.canLeave || $0.hasSystemOwner }
    }
    var filmBackground: Task<Void, Never>?
    private var filmBackgroundTaskID = UIBackgroundTaskIdentifier.invalid
    var restoreFilmPlaybackUI: (@MainActor (UUID) async -> Bool)? {
        didSet { filmMedia?.restoreUI = restoreFilmPlaybackUI }
    }

    private func endFilmBackgroundTask(matching expected: UIBackgroundTaskIdentifier? = nil) {
        if filmBackgroundTaskID != .invalid && (expected == nil || filmBackgroundTaskID == expected) {
            UIApplication.shared.endBackgroundTask(filmBackgroundTaskID)
            filmBackgroundTaskID = .invalid
        }
    }

    private func cancelFilmBackgroundTask() {
        filmBackground?.cancel()
        filmBackground = nil
        endFilmBackgroundTask()
    }

    init(dataRootOverride: URL? = nil) {
        self.dataRootOverride = dataRootOverride
        let center = NotificationCenter.default
        observers.append(center.addObserver(
            forName: UIApplication.willTerminateNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                // iOS may terminate without this notification. Durable state is
                // committed during use; stop all active media output (music and film) here.
                self?.initializedPlayback?.stop()
            }
        })
        observers.append(center.addObserver(
            forName: UIApplication.willResignActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.novel?.suspend()
                self?.saveNovelInBackground()
                self?.foreground = false
                self?.refreshAutomaticSync()
            }
        })
        observers.append(center.addObserver(
            forName: UIApplication.willEnterForegroundNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.cancelFilmBackgroundTask()
            }
        })
        observers.append(center.addObserver(
            forName: UIApplication.didBecomeActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.cancelFilmBackgroundTask()
                self?.foreground = true
                self?.refreshAutomaticSync()
            }
        })
        observers.append(center.addObserver(
            forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                let models = self.filmModels
                guard !models.isEmpty else { return }
                let previous = self.filmBackground
                self.endFilmBackgroundTask()
                let taskID = UIApplication.shared.beginBackgroundTask(withName: "film-background") { [weak self] in
                    Task { @MainActor [weak self] in
                        self?.cancelFilmBackgroundTask()
                    }
                }
                self.filmBackgroundTaskID = taskID
                self.filmBackground = Task { [weak self] in
                    defer { self?.endFilmBackgroundTask(matching: taskID) }
                    await previous?.value
                    for model in models {
                        guard !Task.isCancelled else { break }
                        await model.applicationDidEnterBackground()
                    }
                }
            }
        })
    }

    func start() {
        guard startup == nil, resources == nil, !closed else { return }
        let dataRootOverride = self.dataRootOverride
        let handshake = DebugRuntime.current?.session.nodeHandshake()
        DebugRuntime.log(.lifecycle, sub: "assembly", event: "start", data: .object([
            "dataRootOverride": .optional(dataRootOverride?.path)
        ]))
        startup = Task.detached(priority: .userInitiated) { [weak self] in
            do {
                let resources = try MeowfilmResources(dataRootOverride: dataRootOverride, debugHandshake: handshake)
                guard !Task.isCancelled, let self else {
                    await Self.closeSettings(resources)
                    return
                }
                await self.install(resources)
            } catch {
                DebugRuntime.logError(.lifecycle, sub: "assembly", event: "resources.failed", error: error)
                await self?.failedStartup()
            }
        }
    }

    func failedStartup() {
        guard !closed, !Task.isCancelled else { return }
        DebugRuntime.log(.lifecycle, sub: "assembly", event: "startup.failed", level: .error)
        startupFailed = true
    }

    private func install(_ resources: MeowfilmResources) async {
        guard !closed, !Task.isCancelled, self.resources == nil else {
            await Self.closeSettings(resources)
            return
        }
        self.resources = resources
        do {
            try await appearance.bind(database: resources.database, cacheDatabase: resources.cacheDatabase)
            try await volumePreferences.bind(database: resources.database)
            await playerPreferences.bind(database: resources.database)
            try await filmLibrary.bind(database: resources.database)
        } catch {
            DebugRuntime.logError(.lifecycle, sub: "assembly", event: "bind.failed", error: error)
            failedStartup(); return
        }
        DebugRuntime.log(.lifecycle, sub: "assembly", event: "install.ready", data: .object([
            "dataRoot": .string(resources.database.dataRoot.path)
        ]))
        guard !closed, !Task.isCancelled else { return }
        runtimeAvailability = Task { [weak self] in
            for await _ in await resources.scriptSession.connections() {
                guard !Task.isCancelled, let self, !self.closed else { return }
                self.filmSearch?.runtimeDidBecomeAvailable()
                for model in self.filmModels { model.runtimeDidBecomeAvailable() }
            }
        }
        filmSearch = FilmSearchService(sources: resources.filmSourceSettings,
            connection: { try await resources.scriptSession.connection() })
        let novel = NovelFeature(library: resources.novelLibrary, rules: resources.novelRules, runtime: resources.novelRuntime)
        closeNovel = { await novel.close() }
        self.novel = novel; novel.start()
        let tmdb = FilmTMDBSettingsModel(service: resources.filmMetadata) { [weak filmSearch = self.filmSearch] in
            filmSearch?.metadataSettingsChanged()
        }
        let douban = FilmDoubanSettingsModel(service: resources.filmDoubanSettings)
        let playbackSettings = FilmPlaybackSettingsModel(transport: FilmPlaybackTransport {
            try await resources.scriptSession.connection()
        }, settingsRepository: resources.filmSettings.repository)
        playbackSettings.playbackSnapshot = { [weak self] in
            self?.filmMediaPlayback?.snapshot ?? self?.filmPlayback?.snapshot
        }
        let pan = PanAccountsModel(service: PanAccountService(settingsRepository: resources.filmSettings.repository,
            connection: { try await resources.scriptSession.connection() }))
        let filmSettings = FilmSettingsModel(rules: resources.filmRules, sources: resources.filmSourceSettings,
                                             tmdb: tmdb, douban: douban, pan: pan, playback: playbackSettings,
                                             player: playerPreferences)
        self.filmSettings = filmSettings
        filmSettings.start()
        guard !closed, !Task.isCancelled else { return }
        await resources.musicScripts.start()
        guard !closed, !Task.isCancelled else { return }
        scriptStartup = Task {
            // The source owner publishes a failed startup in its observable
            // snapshot. Node rules are initialized independently by their model.
            do { try await resources.filmSourceSettings.start() }
            catch {
                DebugRuntime.logError(.lifecycle, sub: "assembly", event: "filmSources.start.error", error: error)
            }
        }
        await resources.musicAccounts?.bindPlatformSessions()
        guard !closed, !Task.isCancelled else { return }
        let service = NativeMusicService(
            database: resources.musicLibrary, scripts: resources.musicScripts,
            accounts: resources.musicAccounts, platformClient: resources.musicPlatform,
            sourceDiscovery: resources.musicSourceDiscoveryCache,
            enabledPlatforms: [.netease, .qq, .kugou, .kuwo, .migu],
            homeSnapshot: resources.musicHomeSnapshot,
            audioFileCache: resources.musicAudioCache,
            presentationCache: resources.musicPresentationCache
        )
        await prepareMusic(service, prepareAccounts: {
            guard let accounts = resources.musicAccounts else { throw MusicServiceError.accessUnavailable }
            try await accounts.prepareLocalAccounts()
        }, makeModel: {
            let model = MusicViewModel(service: service, playback: playback, accounts: resources.musicAccounts,
                                       scriptSources: resources.musicScripts, artworkImages: self.musicImages)
            model.setPresentationCache(resources.musicPresentationCache)
            try await model.bindPreferences(database: resources.database)
            return model
        })
    }

    /// Unified explicit clear across all persistent and in-memory music cache domains.
    func clearAllMusicCaches() async throws {
        guard let resources else { throw MusicServiceError.unavailable }
        musicImages.debugClearMemoryCache()
        await music?.debugClearMemoryCaches()
        try await resources.clearMusicCaches()
        try await appearance.clearSongPalettes()
    }

    func shutdown() {
        guard !closed else { return }
        closed = true
        music?.prepareForClose()
        filmSettings?.close()
        automaticSync?.stop()
        runtimeAvailability?.cancel(); runtimeAvailability = nil
        startup?.cancel(); scriptStartup?.cancel(); loading?.cancel(); commandRefresh?.cancel()
        systemMetadata?.close(); systemMetadata = nil
        commandObservation?.cancel()
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers.removeAll()
        // Deliver terminal media facts before closing the same business owner.
        // Film's async stop delivers the captured native facts before closing
        // its Node session. Stopping only music here must not bypass that drain.
        initializedPlayback?.stop(owner: "music")
        endFilmBackgroundTask()
        let model = music, service = service
        let resources = resources, pendingScripts = scriptStartup, lease = lease
        let pendingStartup = startup, playback = initializedPlayback
        let filmSearch = filmSearch, films = filmModels, filmMedia = filmMedia, filmBackground = filmBackground
        filmPlayback = nil
        let novel = novel
        let filmImages = initializedFilmImages
        let musicImages = self.musicImages
        let appearance = self.appearance
        let playerPreferences = self.playerPreferences
        let volumePreferences = self.volumePreferences
        let filmLibrary = self.filmLibrary
        stopping = Task { [weak self] in
            let firstError = await Self.drainResources(
                resources: resources,
                closeMusic: { await model?.close() },
                service: service,
                pendingStartup: pendingStartup,
                closeNovel: { await novel?.close() },
                filmSearch: filmSearch,
                filmBackground: filmBackground,
                films: films,
                filmMedia: filmMedia,
                playback: playback,
                pendingScripts: pendingScripts,
                filmImages: filmImages,
                musicImages: musicImages,
                closeStores: {
                    await appearance.close()
                    await playerPreferences.close()
                    await volumePreferences.close()
                    await filmLibrary.close()
                },
                lease: lease
            )
            if let firstError, self?.shutdownError == nil {
                self?.shutdownError = firstError
            }
        }
    }

    private func saveNovelInBackground() {
        guard let novel else { return }
        var identifier = UIBackgroundTaskIdentifier.invalid
        identifier = UIApplication.shared.beginBackgroundTask(withName: "novel-position") {
            if identifier != .invalid { UIApplication.shared.endBackgroundTask(identifier); identifier = .invalid }
        }
        Task {
            await novel.flush()
            if identifier != .invalid { UIApplication.shared.endBackgroundTask(identifier); identifier = .invalid }
        }
    }

    /// Resources produced after cancellation still belong to this start attempt.
    /// No service may survive just because its install was never published.
    private nonisolated static func closeSettings(_ resources: MeowfilmResources) async {
        async let executor: Void = resources.scriptSession.close()
        async let platform: Void = resources.musicPlatform.close()
        await resources.novelRules.close()
        do { try await resources.novelRuntime.close() }
        catch { DebugRuntime.logError(.lifecycle, sub: "assembly", event: "closeSettings.novelRuntime.error", error: error) }
        await resources.novelLibrary.close()
        await resources.filmRules.close()
        await resources.filmSourceSettings.close()
        do { try await resources.musicScripts.close() }
        catch { DebugRuntime.logError(.lifecycle, sub: "assembly", event: "closeSettings.musicScripts.error", error: error) }
        _ = await resources.musicAccounts?.close()
        do { try await platform }
        catch { DebugRuntime.logError(.lifecycle, sub: "assembly", event: "closeSettings.musicPlatform.error", error: error) }
        await executor
        await resources.musicAudioCache?.close()
        await resources.musicPresentationCache.close()
        do { try resources.musicLibrary.close() } catch { NSLog("Music repository shutdown failed") }
        do { try await resources.cacheDatabase.close() } catch { NSLog("Cache database shutdown failed") }
        do { try await resources.database.close() } catch { NSLog("Database shutdown failed") }
    }

    private static func drainResources(
        resources: MeowfilmResources?,
        closeMusic: (@MainActor @Sendable () async -> Void)?,
        service: NativeMusicService?,
        pendingStartup: Task<Void, Never>?,
        closeNovel: (@MainActor @Sendable () async -> Void)?,
        filmSearch: FilmSearchService?,
        filmBackground: Task<Void, Never>?,
        films: [FilmPlaybackModel],
        filmMedia: MeowfilmFilmMedia?,
        playback: PlaybackController?,
        pendingScripts: Task<Void, Never>?,
        filmImages: ArtworkImages?,
        musicImages: MusicArtworkImages,
        closeStores: (@MainActor () async -> Void)?,
        lease: PlaybackOwnerLease?
    ) async -> String? {
        var firstError: String?
        func record(_ message: String) {
            if firstError == nil { firstError = message }
        }
        // Strict topological shutdown: drain business sessions & flush progress while
        // Node Runtime is still alive, then drain native playback, then close Node & DBs.
        await closeMusic?()
        await service?.prepareForClose()
        await pendingStartup?.value
        await closeNovel?()
        await filmSearch?.close()
        await filmBackground?.value
        for film in films {
            await film.close()
            if !film.canLeave {
                record((film.actionError ?? FilmPlaybackError(code: "MEDIA_RELEASE_FAILED")).localizedDescription)
            }
        }
        do { try await filmMedia?.close() }
        catch { record(error.localizedDescription) }
        do { try await playback?.close() }
        catch { record(error.localizedDescription) }
        if let resources {
            async let rules: Void = resources.filmRules.close()
            async let sources: Void = resources.filmSourceSettings.close()
            _ = await (rules, sources)
        }
        await pendingScripts?.value
        await service?.close()
        await filmImages?.close()
        await musicImages.close()
        do { try await resources?.musicScripts.close() }
        catch { record(error.localizedDescription) }
        if let report = await resources?.musicAccounts?.close(), !report.cleanupComplete {
            record(MusicServiceError.commitUnconfirmed.localizedDescription)
        }
        do { try await resources?.musicPlatform.close() }
        catch { record(error.localizedDescription) }
        await resources?.scriptSession.close()
        await resources?.musicAudioCache?.close()
        await resources?.musicPresentationCache.close()
        await closeStores?()
        do { try await resources?.cacheDatabase.close() }
        catch { record("缓存数据库关闭失败") }
        do { try await resources?.database.close() }
        catch { record("数据库关闭失败") }
        lease?.cancel()
        return firstError
    }

    deinit {
        runtimeAvailability?.cancel()
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        startup?.cancel(); scriptStartup?.cancel(); loading?.cancel(); commandRefresh?.cancel()
        let closeMusic = closeMusic, service = service, resources = resources, lease = lease
        let pendingScripts = scriptStartup, stopping = stopping, filmSearch = filmSearch
        let film = filmPlayback, filmMedia = filmMedia, filmBackground = filmBackground
        let closeNovel = closeNovel
        let musicImages = musicImages, filmImages = initializedFilmImages
        let playback = initializedPlayback
        let appearance = appearance, playerPreferences = playerPreferences
        let volumePreferences = volumePreferences, filmLibrary = filmLibrary
        Task { @MainActor in
            if let stopping { await stopping.value; return }
            var identities = Set<ObjectIdentifier>()
            let films = ([film].compactMap { $0 } + (filmMedia?.models ?? [])).filter {
                identities.insert(ObjectIdentifier($0)).inserted
            }
            if let error = await Self.drainResources(
                resources: resources,
                closeMusic: closeMusic,
                service: service,
                pendingStartup: nil,
                closeNovel: closeNovel,
                filmSearch: filmSearch,
                filmBackground: filmBackground,
                films: films,
                filmMedia: filmMedia,
                playback: playback,
                pendingScripts: pendingScripts,
                filmImages: filmImages,
                musicImages: musicImages,
                closeStores: {
                    await appearance.close()
                    await playerPreferences.close()
                    await volumePreferences.close()
                    await filmLibrary.close()
                },
                lease: lease
            ) {
                NSLog("Assembly deinit shutdown error: %@", error)
            }
        }
    }
}
