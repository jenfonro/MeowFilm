import Foundation

/// A user-opened, bounded read snapshot shared by browsing and queue commit.
/// Loaders return public music locators, never account credentials or media URLs.
/// Public playlist/album consumers can construct this without an account module.
public actor MusicRemoteQueueSource {
    public nonisolated let id = UUID()
    public nonisolated let context: MusicRemoteQueueContext
    public nonisolated let authorization: MusicQueueAuthorization
    private let load: @Sendable (String?) async throws -> MusicNativeTrackPage
    private let validateBinding: @Sendable () async throws -> Void
    private let release: @Sendable () async -> Void
    public nonisolated let dailyStyle: MusicDailyStyleEditing?
    private var pages: [String: MusicNativeTrackPage] = [:]
    private var loading = false
    private var savingStyle = false
    private var contentRevision = UUID()
    private var closed = false
    private var itemCount = 0
    private var byteCount = 0
    public static let maximumTracks = 2000
    public static let maximumPages = 100
    public static let maximumBytes = 6 * 1024 * 1024

    public init(context: MusicRemoteQueueContext,
                authorization: MusicQueueAuthorization = MusicQueueAuthorization(),
                loadPage: @escaping @Sendable (String?) async throws -> MusicNativeTrackPage,
                validate: @escaping @Sendable () async throws -> Void = {},
                close: @escaping @Sendable () async -> Void = {},
                dailyStyle: MusicDailyStyleEditing? = nil) {
        self.context = context; self.authorization = authorization
        load = loadPage; validateBinding = validate; release = close
        self.dailyStyle = dailyStyle
        // No constructor, capability cast or restoration starts a request.
    }
    public func validate() async throws {
        try Task.checkCancellation(); try authorization.check()
        guard !closed else { throw MusicServiceError.closed }
        try await validateBinding()
        try Task.checkCancellation(); try authorization.check()
        guard !closed else { throw MusicServiceError.closed }
    }
    private func checked(_ page: MusicNativeTrackPage) throws -> Int {
        guard page.items.count <= 200,
              page.items.allSatisfy({ !$0.data.isEmpty && $0.data.count <= 64 * 1024 }) else { throw MusicServiceError.limit }
        if let cursor = page.cursor {
            guard !cursor.isEmpty, cursor.utf8.count <= 512, !page.items.isEmpty else { throw MusicServiceError.invalidResponse }
        }
        return page.items.reduce(0) { $0 + $1.data.count }
    }
    public func page(cursor: String?) async throws -> MusicNativeTrackPage {
        try await validate()
        guard !savingStyle else { throw MusicServiceError.busy }
        let key = cursor ?? ""
        if let saved = pages[key] { return saved }
        // A caller cannot skip directly to an unobserved continuation.
        if let cursor {
            guard pages.values.contains(where: { $0.cursor == cursor }) else { throw MusicServiceError.staleResult }
        }
        guard !loading else { throw MusicServiceError.busy }
        guard pages.count < Self.maximumPages else { throw MusicServiceError.limit }
        loading = true; defer { loading = false }
        let value = try await load(cursor)
        try await validate()
        let bytes = try checked(value)
        guard value.cursor != cursor || cursor == nil,
              value.cursor == nil || pages[value.cursor!] == nil else { throw MusicServiceError.invalidResponse }
        guard itemCount + value.items.count <= Self.maximumTracks,
              byteCount + bytes <= Self.maximumBytes else { throw MusicServiceError.limit }
        pages[key] = value; itemCount += value.items.count; byteCount += bytes
        return value
    }
    public func completeTracks(intent: MusicQueueAuthorization) async throws -> [MusicNativeTrackData] {
        let revision = contentRevision
        var rows: [MusicNativeTrackData] = [], cursor: String?, visited = Set<String>()
        repeat {
            try intent.check()
            guard visited.insert(cursor ?? "").inserted, visited.count <= Self.maximumPages else { throw MusicServiceError.limit }
            let value = try await page(cursor: cursor)
            guard revision == contentRevision else { throw MusicServiceError.staleResult }
            rows += value.items; cursor = value.cursor
            guard rows.count <= Self.maximumTracks else { throw MusicServiceError.limit }
        } while cursor != nil
        try intent.check(); try await validate()
        guard revision == contentRevision else { throw MusicServiceError.staleResult }
        return rows
    }
    /// The returned songs and selection come from the single read after the
    /// write. Never POST again to reconcile a lost acknowledgement.
    public func saveDailyStyle(_ selection: MusicDailyStyleSelection) async throws -> MusicDailyStyleSave {
        try await validate()
        guard let dailyStyle else { throw MusicServiceError.unsupported }
        guard !loading, !savingStyle else { throw MusicServiceError.busy }
        savingStyle = true; contentRevision = UUID()
        // The write may succeed remotely even if its acknowledgement or the
        // subsequent read fails. Never expose pre-write pages under a new style
        // revision, and never POST again merely to reconcile that uncertainty.
        pages.removeAll(); itemCount = 0; byteCount = 0
        defer { savingStyle = false }
        let result = try await dailyStyle.save(selection)
        try await validate()
        let bytes = try checked(result.page)
        guard bytes <= Self.maximumBytes, result.page.dailyStyle != nil else { throw MusicServiceError.invalidResponse }
        pages = ["": result.page]; itemCount = result.page.items.count; byteCount = bytes
        return result
    }
    /// Only a live FM boundary invokes this. It does not reuse the preview batch,
    /// change the queue, resolve audio, play, retry, or prefetch on a timer.
    public func nextFMBatch(intent: MusicQueueAuthorization) async throws -> [MusicNativeTrackData] {
        guard context.kind == .fm else { throw MusicServiceError.invalidRequest }
        try await validate(); try intent.check()
        guard !loading else { throw MusicServiceError.busy }
        loading = true; defer { loading = false }
        var rows: [MusicNativeTrackData] = [], cursor: String?, bytes = 0, visited = Set<String>()
        repeat {
            try intent.check(); try await validate()
            guard visited.insert(cursor ?? "").inserted, visited.count <= Self.maximumPages else { throw MusicServiceError.limit }
            let value = try await load(cursor)
            try intent.check(); try await validate()
            bytes += try checked(value); rows += value.items; cursor = value.cursor
            guard rows.count <= Self.maximumTracks, bytes <= Self.maximumBytes else { throw MusicServiceError.limit }
        } while cursor != nil
        guard !rows.isEmpty else { throw MusicServiceError.upstreamInvalid }
        return rows
    }
    public nonisolated func revoke() { authorization.revoke() }
    public func close() async {
        revoke()
        guard !closed else { return }
        closed = true; pages.removeAll(); itemCount = 0; byteCount = 0
        await release()
    }
}
