import Foundation
import MeowRuntime

extension FilmSearchSite {
    public var playbackSource: FilmPlaybackSource {
        var fields = FilmPlaybackSource(key: key, engine: "cat", installationID: cat.installationId,
            siteID: source.key.siteKey, generation: cat.generation, name: source.name,
            enabled: cat.enabled && source.options.enabled, searchable: source.options.searchEnabled).value
        fields["order"] = .number(Double(source.order))
        fields["aggregateImages"] = .bool(source.options.aggregateImages)
        return try! FilmPlaybackSource(.object(fields))
    }
}
