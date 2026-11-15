import XCTest
import UIKit
import AVFoundation
import Darwin
import MeowMusicFeature
import MeowMusicPlatform
import MeowStorage
@testable import Meowfilm

/// Run only this opt-in test in a dedicated, newly installed empty test host.
/// MEOWFILM_LIVE_MUSIC_IOS=1
/// MEOWFILM_LIVE_MUSIC_IOS_RUN_ID=<unique ASCII letters, digits and hyphens>
/// After the READY marker, the runner verifies this host's final empty sandbox
/// and atomically writes tmp/<runId>-authorization.json with isolatedHome and
/// accountFile; the backup must be tmp/<runId>-accounts.json in that sandbox.
/// This test only reads those files; isolation and transfer belong to the runner.
@MainActor
final class MusicLiveAcceptanceTests: XCTestCase {
    private enum Phase: String { case guest, imported }
    private struct RunnerAuthorization: Decodable {
        let isolatedHome: String
        let accountFile: String
    }
    private enum Failure: String, Error {
        case isolationRequired = "ISOLATION_REQUIRED"
        case nonemptyData = "NONEMPTY_DATA"
        case startupFailed = "STARTUP_FAILED"
        case unsafeSettings = "SYNC_OR_FEEDBACK_ENABLED"
        case noHome = "NO_HOME_RESOURCE"
        case noRadar = "NO_RADAR_RESOURCE"
        case noRoaming = "NO_ROAMING_RESOURCE"
        case noPlaylist = "NO_PLAYLIST_RESOURCE"
        case noAlbum = "NO_ALBUM_RESOURCE"
        case noTrack = "NO_TRACK_RESOURCE"
        case notCovered = "NOT_COVERED"
        case importFailed = "IMPORT_FAILED"
        case importIncomplete = "IMPORT_INCOMPLETE"
        case publicationFailed = "PUBLICATION_FAILED"
        case queueNotReplaced = "QUEUE_NOT_REPLACED"
        case mediaNotPrepared = "MEDIA_NOT_PREPARED"
        case mediaFailed = "MEDIA_FAILED"
        case shutdownFailed = "SHUTDOWN_FAILED"
    }

    private struct Context {
        let assembly: MeowfilmAssembly
        let resources: MeowfilmResources
        let model: MusicViewModel
        let accounts: NativeMusicAccountService
        let browser: any MusicHomeBrowsing
    }

    // Reuse the existing task-scoped, closed diagnostic schema for nonthrowing
    // model commands. Never retain arbitrary errors or upstream diagnostic text.
    private final class SafeFailures: @unchecked Sendable {
        private let lock = NSLock()
        private var last: MusicServiceError?
        func receive(_ event: MusicRequestTrace.Event) {
            guard event.phase == .failed,
                  [.playback, .audio, .queue, .settings, .accountAPI].contains(event.operation),
                  let failure = event.failure else { return }
            lock.lock(); last = failure; lock.unlock()
        }
        func reset() { lock.lock(); last = nil; lock.unlock() }
        func latest() -> MusicServiceError? {
            lock.lock(); defer { lock.unlock() }; return last
        }
    }

    private let safeFailures = SafeFailures()
    private var assembly: MeowfilmAssembly?
    private var ownedAccounts: NativeMusicAccountService?
    private var importAttempted = false
    private var pages: [MusicPublicBrowseViewModel] = []
    private var retiredSources: [MusicRemoteQueueSource] = []
    private var failureCount = 0

    func testOptInRealGuestImportAndLogoutThroughApp() async throws {
        let environment = ProcessInfo.processInfo.environment
        guard environment["MEOWFILM_LIVE_MUSIC_IOS"] == "1" else {
            throw XCTSkip("Real music acceptance requires explicit opt-in.")
        }
        // Before asking the App for resources, require the runner's exact
        // dedicated sandbox. Never fall back to a daily installation or reset it.
        do {
            guard let runID = environment["MEOWFILM_LIVE_MUSIC_IOS_RUN_ID"],
                  !runID.isEmpty,
                  runID.utf8.allSatisfy({ byte in
                      byte == 45 || (48...57).contains(byte) ||
                          (65...90).contains(byte) || (97...122).contains(byte)
                  }) else { throw Failure.isolationRequired }
            let home = URL(fileURLWithPath: NSHomeDirectory())
                .resolvingSymlinksInPath().standardizedFileURL
            let temporary = home.appendingPathComponent("tmp", isDirectory: true)
            let authorizationFile = temporary.appendingPathComponent("\(runID)-authorization.json")
            let accountFile = temporary.appendingPathComponent("\(runID)-accounts.json")
            try Task.checkCancellation()
            let authorizationDeadline = ProcessInfo.processInfo.systemUptime + 60
            print("MUSIC_LIVE stage=isolation.awaiting-runner result=READY count=1")
            fflush(nil)
            try await waitUntil(timeout: 60) {
                FileManager.default.fileExists(atPath: authorizationFile.path)
            }
            try Task.checkCancellation()
            try require(ProcessInfo.processInfo.systemUptime < authorizationDeadline,
                        Failure.isolationRequired)
            let authorization: RunnerAuthorization
            do {
                authorization = try JSONDecoder().decode(
                    RunnerAuthorization.self, from: Data(contentsOf: authorizationFile))
            } catch { throw Failure.isolationRequired }
            guard authorization.isolatedHome.hasPrefix("/"),
                  authorization.accountFile.hasPrefix("/"),
                  URL(fileURLWithPath: authorization.isolatedHome)
                    .resolvingSymlinksInPath().standardizedFileURL == home,
                  URL(fileURLWithPath: authorization.accountFile)
                    .resolvingSymlinksInPath().standardizedFileURL == accountFile.standardizedFileURL else {
                throw Failure.isolationRequired
            }
            let diagnostics = safeFailures
            try await MusicRequestTrace.$observer.withValue({ diagnostics.receive($0) }) {
                let context = try await openEmptyHost()
                let guest = try await context.accounts.nativeHomeBinding()
                try require(!guest.loggedIn, Failure.nonemptyData)
                record("guest.empty.sync-feedback-off")
                await exercise(.guest, context: context, binding: guest)

                var summary: MusicAccountImportSummary?
                _ = await step("account.file-import") {
                    // The supported reader and owner codec validate the original
                    // file. No JSON rewriting, credential extraction or session injection.
                    let bytes = try await ImportedFileReader.read(
                        accountFile, maximumBytes: MusicAccountFilePayload.maximumBytes)
                    let plan = try await context.accounts.prepareAccountImport(
                        MusicAccountFilePayload(fileData: bytes))
                    guard plan.conflicts.isEmpty else {
                        plan.cancel()
                        await context.accounts.discardAccountImport(plan)
                        throw Failure.nonemptyData
                    }
                    importAttempted = true
                    let result = await context.accounts.importAccounts(plan, policy: .skip)
                    summary = result
                    print("MUSIC_LIVE stage=account.import-summary result=COUNT imported=\(result.imported.count) failed=\(result.failed.count) skipped=\(result.skipped.count) cancelled=\(result.cancelled.count)")
                    try require(result.failed.isEmpty,
                                safeFailures.latest().map { $0 as any Error } ?? Failure.importFailed)
                    try require(!result.imported.isEmpty && result.skipped.isEmpty && result.cancelled.isEmpty,
                                Failure.importIncomplete)
                }
                let published = await step("account.wy-publication") {
                    try require(summary?.imported.contains(.netease) == true, Failure.importIncomplete)
                    try await context.resources.musicPlatform.drain()
                    let statuses = try await context.accounts.statuses()
                    try require(statuses.first { $0.platform == .netease }?.phase == .signedIn,
                                Failure.publicationFailed)
                    let binding = try await context.accounts.nativeHomeBinding()
                    try require(binding.loggedIn && binding.revision != guest.revision &&
                                !guest.authorization.isActive, Failure.publicationFailed)
                    try await checkSafety(context)
                    return binding
                }
                if let published {
                    await exercise(.imported, context: context, binding: published)
                } else {
                    fail("imported.wy.business", Failure.notCovered)
                }

                let loggedOut = await step("account.logout") {
                    try require(importAttempted, Failure.notCovered)
                    try await logoutOwnedAccounts()
                    try await context.resources.musicPlatform.drain()
                    let binding = try await context.accounts.nativeHomeBinding()
                    try require(!binding.loggedIn && published?.authorization.isActive != true,
                                Failure.publicationFailed)
                    return binding
                }
                if loggedOut != nil {
                    _ = await step("logged-out.wy.home") {
                        try await checkSafety(context)
                        return try await readHome(.netease, context: context)
                    }
                } else {
                    fail("logged-out.wy.home", Failure.notCovered)
                }
            }
        } catch { fail("bootstrap", error) }
        // Cleanup has its own cancellation lifetime: a cancelled test must still
        // remove its saved credentials and await the actual runtime/media drain.
        await Task { @MainActor in await finish() }.value
        print("MUSIC_LIVE stage=complete result=\(failureCount == 0 ? "PASS" : "FAIL") count=\(failureCount)")
    }

    private func openEmptyHost() async throws -> Context {
        guard let app = UIApplication.shared.delegate as? MeowfilmAppDelegate else {
            throw Failure.startupFailed
        }
        try await waitUntil(timeout: 15) { app.launch.phase == .ready }
        guard let assembly = app.readyAssembly() else { throw Failure.startupFailed }
        self.assembly = assembly
        await assembly.waitForStartup()
        guard !assembly.startupFailed, let resources = assembly.resources,
              let model = assembly.music, let accounts = resources.musicAccounts,
              let browser = model.publicBrowser as? any MusicHomeBrowsing else {
            throw Failure.startupFailed
        }
        // Fail closed; do not erase existing documents, credentials or caches.
        try require(try resources.musicDocument.read() == nil, Failure.nonemptyData)
        await model.ensureLibraryLoaded()
        try await waitUntil(timeout: 15) {
            model.hasLoadedLibrary || (!model.libraryState.isLoading && model.notices.error != nil)
        }
        try require(model.hasLoadedLibrary, safeFailures.latest() ?? .storageFailed)
        try require(model.playbackState.queue.isEmpty &&
                    model.libraryState.collections.allSatisfy { $0.count == 0 }, Failure.nonemptyData)
        let statuses = try await accounts.statuses()
        try require(statuses.count == MusicAccountPlatform.allCases.count &&
                    statuses.allSatisfy { $0.phase == .signedOut }, Failure.nonemptyData)
        let context = Context(assembly: assembly, resources: resources, model: model,
                              accounts: accounts, browser: browser)
        try await checkSafety(context)
        ownedAccounts = accounts
        // Local test-owned playback preferences, not an upstream mutation.
        // Pin WY so an automatic cross-platform fallback cannot pass WY acceptance.
        await model.updateSettings(.init(ifRevision: model.playbackState.state.settingsRevision,
                                        officialSources: [.netease], requestQuality: "128k"))
        try require(model.playbackState.state.requestQuality == "128k" &&
                    model.playbackState.state.officialSources == [.netease],
                    safeFailures.latest() ?? .invalidResponse)
        return context
    }

    private func checkSafety(_ context: Context) async throws {
        guard let feedback = context.model.playbackFeedbackSettings,
              let sync = context.model.librarySync as? any MusicAutomaticLibrarySyncing else {
            throw Failure.unsafeSettings
        }
        let feedbackSettings = try await feedback.playbackFeedbackSettings()
        let automatic = try await sync.automaticLibrarySyncPreferences()
        let state = try await sync.librarySyncState()
        try require(feedbackSettings.platforms.values.allSatisfy { !$0.enabled } &&
                    automatic.selections.allSatisfy { !$0.favorites && !$0.playlists } &&
                    state.phase == .idle && state.pending == 0, Failure.unsafeSettings)
    }

    private func readHome(_ platform: MusicPublicBrowsePlatform,
                          context: Context) async throws -> [MusicPublicBrowseItem] {
        let selection = try await context.browser.homeSourceSelection()
        if selection.selected != platform {
            _ = try await context.browser.selectHomeSource(platform, ifRevision: selection.revision)
        }
        let page = try await context.browser.homePage(cursor: nil, refresh: true)
        if let failure = page.sections.compactMap(\.error).first { throw failure }
        let items = page.sections.flatMap(\.items)
        try require(!items.isEmpty, Failure.noHome)
        return items
    }

    private func exercise(_ phase: Phase, context: Context, binding: MusicNativeHomeBinding) async {
        guard let items = await step(phase.rawValue + ".wy.home", operation: {
            try await context.accounts.checkNativeHomeBinding(binding)
            try await checkSafety(context)
            let items = try await readHome(.netease, context: context)
            try await context.accounts.checkNativeHomeBinding(binding)
            return items
        }) else {
            fail(phase.rawValue + ".wy.business", Failure.notCovered)
            return
        }
        for roaming in [false, true] {
            _ = await step(phase.rawValue + (roaming ? ".wy.roaming" : ".wy.radar")) {
                let item = items.first { item in
                    guard case .action(let action) = item.content else { return false }
                    return roaming ? action.isPersonalFM : action.isSimilar
                }
                guard let item, case .action(let action) = item.content else {
                    throw roaming ? Failure.noRoaming : Failure.noRadar
                }
                let oldAttempt = context.assembly.playback.currentAttempt?.id
                context.model.notices.error = nil
                defer { pause(context) }
                try await context.accounts.checkNativeHomeBinding(binding)
                try await context.model.playHomeAction(action, title: item.title, forceReload: true)
                try await mediaReady(context, binding: binding, replacing: oldAttempt)
            }
        }
        var singles = items.compactMap { item -> MusicRemoteTrack? in
            if case .track(let track) = item.content { return track }; return nil
        }
        for kind in [MusicPublicBrowseCollection.Kind.playlist, .album] {
            let name = phase.rawValue + ".wy." + kind.rawValue
            let detail = await step(name + ".detail") {
                try await context.accounts.checkNativeHomeBinding(binding)
                var collections = items.compactMap { item -> MusicPublicBrowseCollection? in
                    if case .collection(let value) = item.content { return value }; return nil
                }
                if kind == .album, !collections.contains(where: { $0.kind == .album && $0.platform == .netease }) {
                    // A current native discovery page, never a fabricated album ID.
                    let featured = try await context.browser.publicDiscovery(.featured, cursor: nil)
                    if let failure = featured.sections.compactMap(\.error).first { throw failure }
                    collections += featured.sections.flatMap(\.items).compactMap { item in
                        if case .collection(let value) = item.content { return value }; return nil
                    }
                }
                guard let collection = collections.first(where: { $0.kind == kind && $0.platform == .netease }) else {
                    throw kind == .album ? Failure.noAlbum : Failure.noPlaylist
                }
                let page = makeDetail(collection, context: context)
                await page.refresh()
                if let failure = page.error { throw failure }
                try require(page.loaded && page.contentIsCurrent && !page.tracks.isEmpty, Failure.noTrack)
                try await context.accounts.checkNativeHomeBinding(binding)
                return page
            }
            if let detail {
                singles += detail.tracks
                _ = await step(name + ".play-all") {
                    let oldAttempt = context.assembly.playback.currentAttempt?.id
                    let (source, index) = try await detail.playbackSource()
                    context.model.notices.error = nil
                    defer { pause(context) }
                    await context.model.playRemoteQueue(source, start: .init(index: index))
                    // Detail and play-all are separate ordinary reads. Assert
                    // the committed queue, not old page IDs/counts/content.
                    let queue = context.model.playbackState.queue
                    try require(!queue.isEmpty &&
                                context.model.playbackState.state.total == queue.count &&
                                context.model.current?.id == queue.first?.id,
                                safeFailures.latest().map { $0 as any Error } ?? Failure.queueNotReplaced)
                    try await mediaReady(context, binding: binding, replacing: oldAttempt)
                }
                detail.disappear()
                await closeRetiredSources()
            } else { fail(name + ".play-all", Failure.notCovered) }
        }
        _ = await step(phase.rawValue + ".wy.single.real-media") {
            guard let track = singles.first else { throw Failure.noTrack }
            let oldAttempt = context.assembly.playback.currentAttempt?.id
            context.model.notices.error = nil
            defer { pause(context) }
            try await context.accounts.checkNativeHomeBinding(binding)
            await context.model.play(track)
            try require(context.model.current?.track.songId == track.songId,
                        safeFailures.latest().map { $0 as any Error } ?? Failure.queueNotReplaced)
            try await mediaReady(context, binding: binding, replacing: oldAttempt)
        }
        // KG's existing public home is the selected additional provider branch;
        // do not invent visitor/private capabilities for the other providers.
        _ = await step(phase.rawValue + ".kg.home") {
            try await readHome(.kugou, context: context)
        }
    }

    private func makeDetail(_ collection: MusicPublicBrowseCollection,
                            context: Context) -> MusicPublicBrowseViewModel {
        let page = MusicPublicBrowseViewModel(service: context.browser, request: .detail(collection),
            queuePage: { source, cursor in
                try await context.model.remoteQueuePage(source, cursor: cursor)
            }, releaseSource: { [weak self] source in
                source.revoke()
                self?.retiredSources.append(source)
            })
        page.setAccess(available: true, generation: context.model.accountAccessGeneration)
        page.appear()
        pages.append(page)
        return page
    }

    private func mediaReady(_ context: Context, binding: MusicNativeHomeBinding,
                            replacing oldAttempt: UUID?) async throws {
        try await context.accounts.checkNativeHomeBinding(binding)
        let playback = context.assembly.playback
        let deadline = ProcessInfo.processInfo.systemUptime + 60
        while ProcessInfo.processInfo.systemUptime < deadline {
            try Task.checkCancellation()
            if context.model.notices.error != nil {
                throw safeFailures.latest().map { $0 as any Error } ?? Failure.mediaFailed
            }
            if playback.snapshot.phase == .failed || playback.player.currentItem?.status == .failed {
                throw Failure.mediaFailed
            }
            if let attempt = playback.currentAttempt, attempt.id != oldAttempt,
               attempt.owner == "music", context.model.activeNativeAttemptID == attempt.id,
               context.model.activePlaybackSelection?.siteId == "wy",
               playback.snapshot.resourceID == attempt.resourceID,
               playback.player.currentItem?.status == .readyToPlay,
               [.playing, .paused, .buffering].contains(playback.snapshot.phase) {
                // This is AVFoundation readiness for the exact admitted attempt,
                // not a resolved URL, persisted queue or simulated media event.
                try await context.accounts.checkNativeHomeBinding(binding)
                return
            }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        throw safeFailures.latest().map { $0 as any Error } ?? Failure.mediaNotPrepared
    }

    private func pause(_ context: Context) {
        if let attempt = context.assembly.playback.currentAttempt {
            _ = context.assembly.playback.pause(attempt: attempt)
        }
    }

    private func logoutOwnedAccounts() async throws {
        guard importAttempted, let accounts = ownedAccounts else { return }
        var failure: (any Error)?
        // All slots were proven empty before this test's import. Even a partly
        // failed import may have durably saved a record, so settle every slot.
        for platform in MusicAccountPlatform.allCases {
            do { try await accounts.logout(platform: platform) }
            catch { if failure == nil { failure = error } }
        }
        if let failure { throw failure }
        let statuses = try await accounts.statuses()
        try require(statuses.allSatisfy { $0.phase == .signedOut }, Failure.publicationFailed)
        importAttempted = false
    }

    private func closeRetiredSources() async {
        let sources = retiredSources
        retiredSources.removeAll()
        for source in sources { await source.close() }
    }

    private func finish() async {
        assembly?.playback.stop(owner: "music")
        for page in pages { page.disappear() }
        pages.removeAll()
        await closeRetiredSources()
        do { try await logoutOwnedAccounts() }
        catch { fail("cleanup.accounts", error) }
        if let assembly {
            assembly.shutdown()
            await assembly.waitForShutdown()
            do {
                try require(assembly.shutdownError == nil, Failure.shutdownFailed)
                try await waitUntil(timeout: 15) {
                    let usage = assembly.playback.networkUsage
                    return assembly.playback.player.currentItem == nil && usage.activeTasks == 0 &&
                        usage.activeConnections == 0 && usage.activeListeners == 0 && usage.reservedPayloadBytes == 0
                }
                record("cleanup.closed-and-drained")
            } catch { fail("cleanup.closed-and-drained", Failure.shutdownFailed) }
        }
    }

    private func waitUntil(timeout: TimeInterval, _ predicate: () -> Bool) async throws {
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        while !predicate() {
            try Task.checkCancellation()
            try require(ProcessInfo.processInfo.systemUptime < deadline, MusicServiceError.timeout)
            try await Task.sleep(nanoseconds: 100_000_000)
        }
    }

    private func require(_ value: Bool, _ failure: any Error) throws {
        if !value { throw failure }
    }

    private func step<T>(_ stage: String, operation: () async throws -> T) async -> T? {
        safeFailures.reset()
        do {
            let result = try await operation()
            record(stage)
            return result
        } catch {
            fail(stage, error)
            return nil
        }
    }

    private func record(_ stage: String) {
        print("MUSIC_LIVE stage=\(stage) result=PASS count=1")
    }

    private func fail(_ stage: String, _ error: any Error) {
        let code: String
        if let failure = error as? Failure { code = failure.rawValue }
        else if error is MusicAccountTransferError || error is ImportedFileError { code = "INVALID_IMPORT" }
        else { code = MusicServiceError.sanitized(error).rawValue }
        failureCount += 1
        // Catch before XCTest can render an arbitrary Error/DTO in its output.
        print("MUSIC_LIVE stage=\(stage) result=FAIL code=\(code) count=1")
        XCTFail("MUSIC_LIVE stage=\(stage) code=\(code)")
    }
}
