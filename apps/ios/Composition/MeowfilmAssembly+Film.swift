import Foundation
import MeowFilmFeature
import MeowRuntime

@MainActor
extension MeowfilmAssembly {
    func makeFilmPlayback(item: FilmItem,
                          entrySurface: String? = nil) throws -> FilmPlaybackModel {
        guard !closed, let resources else { throw FilmPlaybackError(code: "CLOSED") }
        guard let target = item.playbackTarget, let businessID = item.businessID, !businessID.isEmpty,
              target.id.utf8.elementsEqual(businessID.utf8) else {
            throw FilmPlaybackError(code: "INVALID_REQUEST")
        }
        let metadata = try item.metadataSeed.map(RuntimeJSON.decode)
        return try makeFilmPlayback { [weak self] in
            guard let self else { throw FilmPlaybackError(code: "CLOSED") }
            let sources = try await self.filmPlaybackSources(resources: resources)
            return FilmPlaybackRequest(target: target, sources: sources, metadata: metadata, entrySurface: entrySurface)
        }
    }

    func makeFilmPlayback(context: FilmSearchContext) throws -> FilmPlaybackModel {
        guard !closed, let resources else { throw FilmPlaybackError(code: "CLOSED") }
        return try makeFilmPlayback(searchContext: context) { [weak self, context] in
            guard let self else { throw FilmPlaybackError(code: "CLOSED") }
            let sources = try await self.filmPlaybackSources(resources: resources)
            return try await context.playbackRequest(sources: sources)
        }
    }

    /// The fixed Node target can open independently of a live search reference.
    func makeFilmPlayback(searchContext: FilmSearchContext? = nil,
                          request: @escaping @Sendable () async throws -> FilmPlaybackRequest) throws -> FilmPlaybackModel {
        guard !closed, let resources else { throw FilmPlaybackError(code: "CLOSED") }
        if filmMedia == nil {
            let media = try FilmPlaybackMedia(playback: playback, preferences: playerPreferences, willCommit: { [weak self] in
                self?.music?.mediaHandoffCompletion()
            })
            media.restoreUI = restoreFilmPlaybackUI
            filmMedia = media
        }
        let binding = FilmPlaybackMedia.Session(owner: filmMedia!)
        let model = FilmPlaybackModel(transport: FilmPlaybackTransport {
            try await resources.scriptSession.connection()
        }, media: binding, searchContext: searchContext, playerPreferences: playerPreferences,
           playbackPreferences: filmSettings?.playback, request: request)
        binding.model = model
        return model
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
