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

/// Content is borrowed from the one Node data owner, not the Cat RPC or a
/// native response cache. Synthetic readers may omit producer evidence.
public struct FilmSearchData: Sendable {
    public typealias Read = @Sendable (FilmSearchSite, String, Int) async throws -> FilmSearchRead
    private let read: Read

    public init(connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        read = { site, query, page in
            let client = try await connection()
            let input: RuntimeObject = [
                "source": .object(site.playbackSource.value), "query": .string(query), "page": .number(Double(page)),
            ]
            let response = try await client.call(module: .film, operation: "data.search", parameters: .object(input))
            guard let raw = response.catField("raw") else { throw FilmSearchError.invalidResponse }
            var key = input; key["kind"] = .string("search")
            let entry = try await client.call(module: .film, operation: "data.cache.entry", parameters: .object(key))
            try Task.checkCancellation()
            guard !client.isClosed else { throw RuntimeHTTPError.closed }
            return FilmSearchRead(value: raw, producer: client, cacheEntry: entry == .null ? nil : entry)
        }
    }

    public init(read: @escaping Read) { self.read = read }

    func search(site: FilmSearchSite, query: String, page: Int) async throws -> FilmSearchRead {
        try await read(site, query, page)
    }
}

extension FilmSearchSite {
    public var playbackSource: FilmPlaybackSource {
        .init(key: key, engine: "cat", installationID: cat.installationId, siteID: source.key.siteKey,
              generation: cat.generation, name: source.name,
              enabled: cat.enabled && source.options.enabled, searchable: source.options.searchEnabled)
    }
}
