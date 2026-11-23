#if os(iOS) || os(macOS)
import Foundation
import MeowAudio
import MeowContracts

/// Owns inputs from before asynchronous preparation until physical retirement.
@MainActor
final class PlaybackAudioMedia {
    let resource: PlaybackResource
    private let options: PlaybackNetworkOptions
    private var input: (any AudioByteInput)?
    private var route: HLSLoopbackRoute?
    private var preparing: Task<Void, Error>?
    private var closing: Task<Void, Error>?
    private var cancelled = false
    private var failure: Error?
    private var prepared = false
    private(set) var session: AudioPlaybackSession?
    var onFailure: (@MainActor (Error) -> Void)?
    var duration: Double? { session?.information?.duration }
    var position: Double { session?.position ?? 0 }
    var isReady: Bool { prepared && !cancelled && failure == nil }

    init(resource: PlaybackResource, options: PlaybackNetworkOptions = .init()) {
        self.resource = resource; self.options = options
    }

    func prepare(startScopedHLS: @escaping @MainActor (
        HLSLoopbackRoute, @escaping @Sendable (Result<URL, PlaybackNetworkError>) -> Void
    ) -> Void = { $0.start($1) }) async throws {
        if preparing == nil {
            preparing = Task { [self] in
                try check()
                let plan = try PlaybackNetworkPlan(resource: resource, options: options)
                let onFailure: @Sendable (PlaybackNetworkError) -> Void = { [weak self] error in
                    Task { @MainActor [weak self] in self?.report(error) }
                }
                if let source = resource.byteSource {
                    input = PlaybackAudioInput(source: source)
                } else if plan.route == .local {
                    let url = plan.url
                    input = try await Task.detached { try PlaybackAudioInput(file: url) }.value
                } else if plan.route == .scopedHLS {
                    try await prepareHLS(plan: plan, onFailure: onFailure, startScopedHLS: startScopedHLS)
                } else if plan.route == .systemHTTP {
                    input = PlaybackHTTPStreamInput(plan: plan)
                } else {
                    input = PlaybackAudioInput(plan: plan, onFailure: onFailure)
                }
                guard let input else { throw NativePlaybackError.preparationFailed }
                try check()
                let session = AudioPlaybackSession(input: input)
                self.session = session
                session.onFailure = { [weak self] error in self?.report(error) }
                do { try await session.prepare() }
                catch where error as? PlaybackNetworkError == .hlsRequiresExplicitFormat && options.format == .automatic {
                    try await session.close()
                    self.session = nil; self.input = nil
                    try check()
                    let hls = try PlaybackNetworkPlan(resource: resource, options: .init(format: .httpLiveStreaming))
                    try await prepareHLS(plan: hls, onFailure: onFailure, startScopedHLS: startScopedHLS)
                    guard let input = self.input else { throw NativePlaybackError.preparationFailed }
                    let classified = AudioPlaybackSession(input: input)
                    self.session = classified
                    classified.onFailure = { [weak self] in self?.report($0) }
                    try await classified.prepare()
                }
                try check()
                prepared = true
            }
        }
        try await withTaskCancellationHandler {
            try await preparing!.value
            try check()
        } onCancel: { Task { @MainActor [weak self] in self?.cancel() } }
    }

    private func prepareHLS(plan: PlaybackNetworkPlan,
                            onFailure: @escaping @Sendable (PlaybackNetworkError) -> Void,
                            startScopedHLS: @MainActor (HLSLoopbackRoute,
                                @escaping @Sendable (Result<URL, PlaybackNetworkError>) -> Void) -> Void) async throws {
        let route = try HLSLoopbackRoute(plan: plan, onFailure: onFailure)
        self.route = route
        let url = try await withCheckedThrowingContinuation { continuation in
            startScopedHLS(route) { continuation.resume(with: $0) }
        }
        try check()
        let data = try await readLoopbackPlaylist(url)
        try check()
        input = PlaybackAudioInput(playlist: data, route: route, url: url)
    }

    private func readLoopbackPlaylist(_ url: URL) async throws -> Data {
        let config = URLSessionConfiguration.ephemeral
        config.urlCache = nil
        config.timeoutIntervalForResource = 30
        let session = URLSession(configuration: config)
        defer { session.finishTasksAndInvalidate() }
        let (data, response): (Data, URLResponse)
        do { (data, response) = try await session.data(from: url) }
        catch is CancellationError { throw CancellationError() }
        catch { throw PlaybackNetworkError.transportFailed }
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw PlaybackNetworkError.invalidResponse
        }
        guard ProgressiveMediaResponse.isPlaylistPrefix(data) else {
            throw PlaybackNetworkError.hlsPlaylistInvalid
        }
        return data
    }

    func check() throws {
        try Task.checkCancellation()
        if cancelled { throw CancellationError() }
        if let failure { throw failure }
        try session?.check()
    }

    func report(_ error: Error) {
        guard !cancelled else { return }
        failure = error
        onFailure?(error)
    }

    func cancel() {
        guard !cancelled else { return }
        cancelled = true
        preparing?.cancel()
        session?.cancel()
        input?.cancelReads()
        resource.byteSource?.retire()
        route?.invalidate()
        onFailure = nil
    }

    func close() async throws {
        if closing == nil {
            cancel()
            closing = Task { [self] in
                _ = try? await preparing?.value
                preparing = nil
                var failure: Error?
                do {
                    if let session { try await session.close() }
                    else { try await input?.close() }
                } catch { failure = error }
                await route?.close()
                session = nil; input = nil; route = nil
                if let failure { throw failure }
            }
        }
        try await closing!.value
    }

    isolated deinit {
        preparing?.cancel()
        session?.cancel()
        input?.cancelReads()
        resource.byteSource?.retire()
        route?.invalidate()
        let session = session, input = input, route = route
        Task {
            if let session { try? await session.close() }
            else { try? await input?.close() }
            await route?.close()
        }
    }
}
#endif
