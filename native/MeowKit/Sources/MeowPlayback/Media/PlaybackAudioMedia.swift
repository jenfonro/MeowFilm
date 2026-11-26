#if os(iOS) || os(macOS)
import Foundation
import MeowAudio
import MeowContracts

/// Owns inputs from before asynchronous preparation until physical retirement.
@MainActor
final class PlaybackAudioMedia {
    let resource: PlaybackResource
    private let options: PlaybackNetworkOptions
    private var preparing: Task<Void, Error>?
    private var closing: Task<Void, Error>?
    private var cancelled = false
    private var failure: Error?
    private var prepared = false
    private(set) var inputRoute = "unknown"
    nonisolated(unsafe) var session: AudioPlaybackSession?
    private nonisolated(unsafe) var input: (any AudioByteInput)?
    private nonisolated(unsafe) var route: HLSLoopbackRoute?
    var onFailure: (@MainActor (Error) -> Void)?
    var duration: Double? { session?.information?.duration }
    var position: Double { session?.position ?? 0 }
    var isReady: Bool { prepared && !cancelled && failure == nil }

    init(resource: PlaybackResource, options: PlaybackNetworkOptions = .init()) {
        self.resource = resource
        self.options = options
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
                    input = PlaybackAudioInput(source: source)
                } else if plan.route == .local {
                    inputRoute = "local"
                    let url = plan.url
                    input = try await Task.detached { try PlaybackAudioInput(file: url) }.value
                } else if plan.route == .scopedHLS {
                    inputRoute = "scopedHLS"
                    try await prepareHLS(plan: plan, onFailure: onFailure)
                } else {
                    inputRoute = "http"
                    input = PlaybackHTTPStreamInput(plan: plan)
                }
                guard let activeInput = input else { throw NativePlaybackError.preparationFailed }
                try check()
                let session = AudioPlaybackSession(input: activeInput)
                self.session = session
                session.onFailure = { [weak self] error in self?.report(error) }
                do { try await session.prepare() }
                catch where error as? PlaybackNetworkError == .hlsRequiresExplicitFormat && options.format == .automatic {
                    try await session.close()
                    self.session = nil
                    self.input = nil
                    try check()
                    let hls = try PlaybackNetworkPlan(
                        resource: resource,
                        options: .init(format: .httpLiveStreaming)
                    )
                    inputRoute = "scopedHLS"
                    try await prepareHLS(plan: hls, onFailure: onFailure)
                    guard let hlsInput = self.input else { throw NativePlaybackError.preparationFailed }
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
        self.route = route
        let url = try await withCheckedThrowingContinuation { continuation in
            route.start { continuation.resume(with: $0) }
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

    private func cancelResources() {
        session?.cancel()
        input?.cancelReads()
        resource.byteSource?.retire()
        route?.invalidate()
    }

    func cancel() {
        guard !cancelled else { return }
        cancelled = true
        preparing?.cancel()
        onFailure = nil
        cancelResources()
    }

    func close() async throws {
        if closing == nil {
            cancel()
            let preparingTask = preparing
            let activeSession = session
            let activeInput = input
            let activeRoute = route
            preparing = nil
            session = nil
            input = nil
            route = nil
            closing = Task {
                _ = try? await preparingTask?.value
                var failure: Error?
                do {
                    if let activeSession { try await activeSession.close() }
                    else { try await activeInput?.close() }
                } catch { failure = error }
                await activeRoute?.close()
                if let failure { throw failure }
            }
        }
        try await closing!.value
    }

    nonisolated deinit {
        let activeSession = session
        let activeInput = input
        let activeRoute = route
        let activeSource = resource.byteSource
        guard activeSession != nil || activeInput != nil || activeRoute != nil || activeSource != nil else { return }
        activeInput?.cancelReads()
        activeSource?.retire()
        activeRoute?.invalidate()
        let cleanup: @MainActor @Sendable () -> Void = {
            activeSession?.cancel()
            guard activeSession != nil || activeInput != nil || activeRoute != nil else { return }
            Task {
                if let activeSession { try? await activeSession.close() }
                else { try? await activeInput?.close() }
                await activeRoute?.close()
            }
        }
        if Thread.isMainThread { MainActor.assumeIsolated(cleanup) }
        else { DispatchQueue.main.async { MainActor.assumeIsolated(cleanup) } }
    }
}
#endif
