import Foundation
import MeowRuntime

/// The response keeps its actual producer. Display snapshots alone never prove
/// runtime ownership or cache freshness.
public struct FilmSearchRead: Sendable {
    public let value: RuntimeValue
    public let producer: RuntimeHTTPClient?
    public let cacheEntry: RuntimeValue?

    public init(value: RuntimeValue, producer: RuntimeHTTPClient? = nil, cacheEntry: RuntimeValue? = nil) {
        self.value = value; self.producer = producer; self.cacheEntry = cacheEntry
    }
}

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
