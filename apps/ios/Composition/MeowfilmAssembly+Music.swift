import Combine
import Foundation
import MeowDebug
import MeowMusicFeature
import MeowMusicNativeService
import MeowPlayback

@MainActor
extension MeowfilmAssembly {
    /// Account preparation is local only; the service retains the pre-read home for the first page.
    func prepareMusic(_ service: NativeMusicService,
                      prepareAccounts: () async throws -> Void,
                      makeModel: () async throws -> MusicViewModel) async {
        guard !closed, !Task.isCancelled else { await service.close(); return }
        self.service = service
        do {
            try await service.preparePlaybackFeedback()
            try await prepareAccounts()
            try Task.checkCancellation()
            guard !closed else { return }
            _ = try await service.homeSourceSelection()
            _ = try await service.restoredHome()
            guard !closed, !Task.isCancelled else { return }
            let model = try await makeModel()
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
            if !Task.isCancelled {
                DebugRuntime.logError(.lifecycle, sub: "assembly.music", event: "prepare.failed", error: error)
            }
            failedStartup()
        }
    }

    func bindPlayback(_ model: MusicViewModel) {
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
            DebugRuntime.logError(.lifecycle, sub: "assembly.music", event: "bindPlayback.failed", error: error)
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

    func refreshCommands() {
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

    func refreshAutomaticSync() {
        guard let music else { automaticSync?.stop(); return }
        automaticSync?.setActive(!music.isClosed && !closed && foreground)
    }
}
