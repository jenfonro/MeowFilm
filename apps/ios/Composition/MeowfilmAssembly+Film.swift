import Foundation
import MeowFilmFeature
import MeowRuntime

@MainActor
extension MeowfilmAssembly {
    func makeFilmPlayback(item: FilmItem,
                          entrySurface: String? = nil) throws -> FilmPlaybackModel {
        guard !closed, resources != nil else { throw FilmPlaybackError(code: "CLOSED") }
        guard let entry = item.entry, !entry.targetId.isEmpty else {
            throw FilmPlaybackError(code: "INVALID_REQUEST")
        }
        return try makeFilmPlayback { [weak self] in
            guard let self, await !self.closed else { throw FilmPlaybackError(code: "CLOSED") }
            await self.scriptStartup?.value
            try Task.checkCancellation()
            guard await !self.closed else { throw FilmPlaybackError(code: "CLOSED") }
            return FilmPlaybackRequest(entry: entry)
        }
    }

    func makeFilmPlayback(context: FilmSearchContext) throws -> FilmPlaybackModel {
        guard !closed, resources != nil else { throw FilmPlaybackError(code: "CLOSED") }
        return try makeFilmPlayback(searchContext: context) { [weak self, context] in
            guard let self, await !self.closed else { throw FilmPlaybackError(code: "CLOSED") }
            await self.scriptStartup?.value
            try Task.checkCancellation()
            guard await !self.closed else { throw FilmPlaybackError(code: "CLOSED") }
            return await context.playbackRequest()
        }
    }

    /// Node resolves live video sources via `film.video-sources` and owns session state.
    func makeFilmPlayback(searchContext: FilmSearchContext? = nil,
                          request: @escaping @Sendable () async throws -> FilmPlaybackRequest) throws -> FilmPlaybackModel {
        guard !closed, let resources else { throw FilmPlaybackError(code: "CLOSED") }
        if filmMedia == nil {
            let media = try MeowfilmFilmMedia(playback: playback, preferences: playerPreferences, willCommit: { [weak self] in
                self?.music?.mediaHandoffCompletion()
            })
            media.restoreUI = restoreFilmPlaybackUI
            filmMedia = media
        }
        let binding = MeowfilmFilmMedia.Session(owner: filmMedia!)
        let model = FilmPlaybackModel(transport: FilmPlaybackTransport {
            try await resources.scriptSession.connection()
        }, media: binding, searchContext: searchContext, playerPreferences: playerPreferences,
           playbackPreferences: filmSettings?.playback, request: request)
        binding.model = model
        return model
    }

    func activateFilm(_ model: FilmPlaybackModel) async throws {
        await filmBackground?.value
        guard !closed else { throw FilmPlaybackError(code: "CLOSED") }
        if let current = filmPlayback, current !== model {
            await current.close(allowSystemOwner: true)
            guard current.canLeave || current.hasSystemOwner else {
                throw current.actionError ?? FilmPlaybackError(code: "MEDIA_UNAVAILABLE")
            }
            if filmPlayback === current { filmPlayback = nil }
        }
        try Task.checkCancellation()
        guard !closed else { throw FilmPlaybackError(code: "CLOSED") }
        filmPlayback = model
    }

    func leaveFilm(allowSystemOwner: Bool = true) async throws {
        let models = allowSystemOwner ? [filmPlayback].compactMap { $0 } : filmModels
        for model in models {
            await model.close(allowSystemOwner: allowSystemOwner)
            guard model.canLeave || allowSystemOwner && model.hasSystemOwner else {
                throw model.actionError ?? FilmPlaybackError(code: "MEDIA_RELEASE_FAILED")
            }
            if filmPlayback === model { filmPlayback = nil }
        }
    }
}
