#if os(iOS) || os(macOS)
import Foundation
import MeowAudio
import MeowContracts

/// Owns inputs from before asynchronous preparation until physical retirement.
@MainActor
final class PlaybackAudioMedia {
    let resource: PlaybackResource
    private let options: PlaybackNetworkOptions
    private nonisolated let lifetime: PlaybackAudioMediaLifetime
    private var preparing: Task<Void, Error>?
    private var closing: Task<Void, Error>?
    private var cancelled = false
    private var failure: Error?
    private var prepared = false
    private(set) var inputRoute = "unknown"
    var session: AudioPlaybackSession? {
        get { lifetime.session }
        set { lifetime.session = newValue }
    }
    var onFailure: (@MainActor (Error) -> Void)?
    var duration: Double? { session?.information?.duration }
    var position: Double { session?.position ?? 0 }
    var isReady: Bool { prepared && !cancelled && failure == nil }

    init(resource: PlaybackResource, options: PlaybackNetworkOptions = .init()) {
        self.resource = resource
        self.options = options
        self.lifetime = PlaybackAudioMediaLifetime(byteSource: resource.byteSource)
    }

    func prepare() async throws {
        if preparing == nil {
            preparing = Task { [self] in
                try check()
                let plan = try PlaybackNetworkPlan(resource: resource, options: options)
                let onFailure: @Sendable (PlaybackNetworkError) -> Void = { [weak self] error in
                    Task { @MainActor [weak self] in self?.report(error) }
                }
                if let source = resource.byteSource {
                    inputRoute = "byteSource"
                    lifetime.input = PlaybackAudioInput(source: source)
                } else if plan.route == .local {
                    inputRoute = "local"
                    let url = plan.url
                    lifetime.input = try await Task.detached { try PlaybackAudioInput(file: url) }.value
                } else if plan.route == .scopedHLS {
                    inputRoute = "scopedHLS"
                    try await prepareHLS(plan: plan, onFailure: onFailure)
                } else {
                    inputRoute = "http"
                    lifetime.input = PlaybackHTTPStreamInput(plan: plan)
                }
                guard let input = lifetime.input else { throw NativePlaybackError.preparationFailed }
                try check()
                let session = AudioPlaybackSession(input: input)
                self.session = session
                session.onFailure = { [weak self] error in self?.report(error) }
                do { try await session.prepare() }
                catch where error as? PlaybackNetworkError == .hlsRequiresExplicitFormat && options.format == .automatic {
                    try await session.close()
                    self.session = nil
                    lifetime.input = nil
                    try check()
                    let hls = try PlaybackNetworkPlan(
                        resource: resource,
                        options: .init(format: .httpLiveStreaming)
                    )
                    inputRoute = "scopedHLS"
                    try await prepareHLS(plan: hls, onFailure: onFailure)
                    guard let hlsInput = lifetime.input else { throw NativePlaybackError.preparationFailed }
                    let classified = AudioPlaybackSession(input: hlsInput)
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
                            onFailure: @escaping @Sendable (PlaybackNetworkError) -> Void) async throws {
        let route = try HLSLoopbackRoute(plan: plan, onFailure: onFailure)
        lifetime.route = route
        let url = try await withCheckedThrowingContinuation { continuation in
            route.start { continuation.resume(with: $0) }
        }
        try check()
        let data = try await readLoopbackPlaylist(url)
        try check()
        lifetime.input = PlaybackAudioInput(playlist: data, route: route, url: url)
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
        guard PlaybackNetworkPlan.isPlaylistPrefix(data) else {
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
        onFailure = nil
        lifetime.cancelNow()
    }

    func close() async throws {
        if closing == nil {
            cancel()
            let preparingTask = preparing
            preparing = nil
            let holder = lifetime
            closing = Task {
                _ = try? await preparingTask?.value
                try await holder.closeAll()
            }
        }
        try await closing!.value
    }

    nonisolated deinit {
        let holder = lifetime
        if Thread.isMainThread {
            MainActor.assumeIsolated { holder.finishOnDeinit() }
        } else {
            DispatchQueue.main.async { MainActor.assumeIsolated { holder.finishOnDeinit() } }
        }
    }
}

@MainActor
private final class PlaybackAudioMediaLifetime {
    let byteSource: (any PlaybackByteSource)?
    var session: AudioPlaybackSession?
    var input: (any AudioByteInput)?
    var route: HLSLoopbackRoute?

    init(byteSource: (any PlaybackByteSource)?) {
        self.byteSource = byteSource
    }

    func cancelNow() {
        session?.cancel()
        input?.cancelReads()
        byteSource?.retire()
        route?.invalidate()
    }

    func closeAll() async throws {
        cancelNow()
        let activeSession = session
        let activeInput = input
        let activeRoute = route
        session = nil
        input = nil
        route = nil
        var failure: Error?
        do {
            if let activeSession { try await activeSession.close() }
            else { try await activeInput?.close() }
        } catch { failure = error }
        await activeRoute?.close()
        if let failure { throw failure }
    }

    func finishOnDeinit() {
        cancelNow()
        let activeSession = session
        let activeInput = input
        let activeRoute = route
        session = nil
        input = nil
        route = nil
        guard activeSession != nil || activeInput != nil || activeRoute != nil else { return }
        Task {
            if let activeSession { try? await activeSession.close() }
            else { try? await activeInput?.close() }
            await activeRoute?.close()
        }
    }
}
#endif
