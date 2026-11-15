import Foundation

/// A user-opened read snapshot shared by browsing and queue commit.
/// Fresh loaders return public locators, adopted once by the native repository.
/// Retained pages contain references, never independently writable song copies.
public actor MusicRemoteQueueSource {
    public nonisolated let id = UUID()
    public nonisolated let context: MusicRemoteQueueContext
    public nonisolated let authorization: MusicQueueAuthorization
    private enum Loader: Sendable {
        case fresh(@Sendable (String?) async throws -> MusicNativeTrackPage)
        case references(@Sendable (String?, MusicQueueAuthorization) async throws -> MusicQueueReferencePage)
    }
    private let load: Loader
    private var adopt: (@Sendable ([MusicNativeTrackData], MusicQueueAuthorization) async throws -> [MusicQueueTrackReference])?
    private var project: (@Sendable ([MusicQueueTrackReference]) async throws -> [MusicNativeTrackData])?
    private let validateBinding: @Sendable () async throws -> Void
    private let release: @Sendable () async -> Void
    public nonisolated let dailyStyle: MusicDailyStyleEditing?
    public nonisolated let homeAction: MusicHomeAction?
    public nonisolated let fmPreferenceScope: String?
    private var pages: [String: MusicQueueReferencePage] = [:]
    private var loading = false
    private var savingStyle = false
    private var contentRevision = UUID()
    private var closed = false
    private var closing: Task<Void, Never>?
    private var byteCount = 0
    public static let maximumBytes = 6 * 1024 * 1024

    public init(context: MusicRemoteQueueContext,
                authorization: MusicQueueAuthorization = MusicQueueAuthorization(),
                loadPage: @escaping @Sendable (String?) async throws -> MusicNativeTrackPage,
                validate: @escaping @Sendable () async throws -> Void = {},
                close: @escaping @Sendable () async -> Void = {},
                dailyStyle: MusicDailyStyleEditing? = nil, homeAction: MusicHomeAction? = nil,
                fmPreferenceScope: String? = nil) {
        self.context = context; self.authorization = authorization
        load = .fresh(loadPage); validateBinding = validate; release = close
        self.dailyStyle = dailyStyle
        self.homeAction = homeAction
        self.fmPreferenceScope = fmPreferenceScope
        // No constructor, capability cast or restoration starts a request.
    }
    public init(context: MusicRemoteQueueContext,
                authorization: MusicQueueAuthorization = MusicQueueAuthorization(),
                loadReferences: @escaping @Sendable (String?, MusicQueueAuthorization) async throws -> MusicQueueReferencePage,
                validate: @escaping @Sendable () async throws -> Void = {},
                close: @escaping @Sendable () async -> Void = {},
                homeAction: MusicHomeAction? = nil, fmPreferenceScope: String? = nil) {
        self.context = context; self.authorization = authorization
        load = .references(loadReferences); validateBinding = validate; release = close
        dailyStyle = nil; self.homeAction = homeAction; self.fmPreferenceScope = fmPreferenceScope
    }
    /// A passive source acquires its song owner before its first read. Repeated
    /// consumers of the same source keep that owner and its existing references.
    public func bindSongRepository(
        adopt: @escaping @Sendable ([MusicNativeTrackData], MusicQueueAuthorization) async throws -> [MusicQueueTrackReference],
        project: @escaping @Sendable ([MusicQueueTrackReference]) async throws -> [MusicNativeTrackData]
    ) {
        guard self.project == nil else { return }
        self.adopt = adopt; self.project = project
    }
    public func validate() async throws {
        try Task.checkCancellation(); try authorization.check()
        guard !closed else { throw MusicServiceError.closed }
        try await validateBinding()
        try Task.checkCancellation(); try authorization.check()
        guard !closed else { throw MusicServiceError.closed }
    }
    public func page(cursor: String?) async throws -> MusicNativeTrackPage {
        let value = try await referencePage(cursor: cursor)
        let items = try await projectTracks(value.items)
        try await validate()
        return .init(items: items, total: value.total, nextPage: value.nextPage, cursor: value.cursor, dailyStyle: value.dailyStyle)
    }
    public func referencePage(cursor: String?) async throws -> MusicQueueReferencePage {
        try await referencePage(cursor: cursor, intent: authorization)
    }
    private func referencePage(cursor: String?, intent: MusicQueueAuthorization) async throws -> MusicQueueReferencePage {
        try await validate()
        try intent.check()
        guard !savingStyle else { throw MusicServiceError.busy }
        let key = cursor ?? ""
        if let saved = pages[key] { return saved }
        // A caller cannot skip directly to an unobserved continuation.
        if let cursor {
            guard pages.values.contains(where: { $0.cursor == cursor }) else { throw MusicServiceError.staleResult }
        }
        guard !loading else { throw MusicServiceError.busy }
        loading = true; defer { loading = false }
        let reading = MusicQueueAuthorization(parent: intent)
        let value = try await withTaskCancellationHandler {
            try await readPage(cursor, remainingBytes: Self.maximumBytes - byteCount, intent: reading, caching: true)
        } onCancel: { reading.revoke() }
        let bytes = value.items.reduce(0) { $0 + $1.byteCount }
        try reading.perform { pages[key] = value; byteCount += bytes }
        return value
    }
    private func readPage(_ cursor: String?, remainingBytes: Int, intent: MusicQueueAuthorization,
                          caching: Bool) async throws -> MusicQueueReferencePage {
        switch load {
        case .fresh(let load):
            let value = try await load(cursor)
            try await validate(); try intent.check()
            try checkPage(cursor: cursor, next: value.cursor,
                          bytes: value.items.reduce(0) { $0 + $1.data.count }, remainingBytes: remainingBytes, caching: caching)
            guard let adopt else { throw MusicServiceError.unavailable }
            let items = try await adopt(value.items, intent)
            try await validate(); try intent.check()
            return .init(items: items, total: value.total, nextPage: value.nextPage, cursor: value.cursor, dailyStyle: value.dailyStyle)
        case .references(let load):
            let value = try await load(cursor, intent)
            try await validate(); try intent.check()
            try checkPage(cursor: cursor, next: value.cursor,
                          bytes: value.items.reduce(0) { $0 + $1.byteCount }, remainingBytes: remainingBytes, caching: caching)
            return value
        }
    }
    private func checkPage(cursor: String?, next: String?, bytes: Int, remainingBytes: Int, caching: Bool) throws {
        guard next != cursor || cursor == nil,
              !caching || next == nil || pages[next!] == nil else { throw MusicServiceError.invalidResponse }
        guard bytes <= remainingBytes else { throw MusicServiceError.limit }
    }
    private func projectTracks(_ items: [MusicQueueTrackReference]) async throws -> [MusicNativeTrackData] {
        guard let project else { throw MusicServiceError.unavailable }
        return try await project(items)
    }
    public func completeTracks(intent: MusicQueueAuthorization) async throws -> [MusicNativeTrackData] {
        let items = try await completeReferences(intent: intent)
        let value = try await projectTracks(items)
        try intent.check(); try await validate()
        return value
    }
    /// Saves the content obtained by this ordinary read, not a cross-time
    /// snapshot guarantee. Destructive remote writes have their own preflight.
    public func collectionTracks(intent: MusicQueueAuthorization) async throws -> [MusicNativeTrackData] {
        try await completeTracks(intent: intent)
    }
    public func completeReferences(intent: MusicQueueAuthorization) async throws -> [MusicQueueTrackReference] {
        let revision = contentRevision
        var rows: [MusicQueueTrackReference] = [], cursor: String?, visited = Set<String>()
        repeat {
            try intent.check()
            guard visited.insert(cursor ?? "").inserted else { throw MusicServiceError.invalidResponse }
            let value = try await referencePage(cursor: cursor, intent: intent)
            guard revision == contentRevision else { throw MusicServiceError.staleResult }
            rows += value.items; cursor = value.cursor
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
        pages.removeAll(); byteCount = 0
        defer { savingStyle = false }
        let result = try await dailyStyle.save(selection)
        // Preserve the actual dispatched write receipt even after the page
        // closes. Only current resources may cache the following read.
        let bytes = result.page.items.reduce(0) { $0 + $1.data.count }
        if !closed, authorization.isActive, bytes <= Self.maximumBytes, let adopt {
            // Adoption is optional readback work, not the dispatched write.
            // Its failure must never replace or replay the original receipt.
            if let items = try? await adopt(result.page.items, authorization), !closed, authorization.isActive {
                pages = ["": .init(items: items, total: result.page.total, nextPage: result.page.nextPage,
                    cursor: result.page.cursor, dailyStyle: result.page.dailyStyle)]
                byteCount = bytes
            }
        }
        return result
    }
    /// Only a live FM boundary invokes this. It does not reuse the preview batch,
    /// change the queue, resolve audio, play, retry, or prefetch on a timer.
    public func nextFMBatch(intent: MusicQueueAuthorization) async throws -> [MusicNativeTrackData] {
        let items = try await nextFMReferences(intent: intent)
        let value = try await projectTracks(items)
        try intent.check(); try await validate()
        return value
    }
    public func nextFMReferences(intent: MusicQueueAuthorization) async throws -> [MusicQueueTrackReference] {
        guard context.kind == .fm else { throw MusicServiceError.invalidRequest }
        try await validate(); try intent.check()
        guard !loading else { throw MusicServiceError.busy }
        loading = true; defer { loading = false }
        var rows: [MusicQueueTrackReference] = [], cursor: String?, bytes = 0, visited = Set<String>()
        repeat {
            try intent.check(); try await validate()
            guard visited.insert(cursor ?? "").inserted else { throw MusicServiceError.invalidResponse }
            let value = try await readPage(cursor, remainingBytes: Self.maximumBytes - bytes, intent: intent, caching: false)
            try intent.check(); try await validate()
            bytes += value.items.reduce(0) { $0 + $1.byteCount }; rows += value.items; cursor = value.cursor
        } while cursor != nil
        return rows
    }
    public nonisolated func revoke() { authorization.revoke() }
    public func close() async {
        revoke()
        if let closing { await closing.value; return }
        closed = true; pages.removeAll(); byteCount = 0
        let release = self.release
        let closing = Task { await release() }
        self.closing = closing
        await closing.value
    }
}
