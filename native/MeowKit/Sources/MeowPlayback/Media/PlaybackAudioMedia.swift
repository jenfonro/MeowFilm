#if os(iOS) || os(macOS)
import Foundation
import MeowAudio
import MeowContracts

/// Owns inputs from before asynchronous preparation until physical retirement.
/// Neither a candidate nor a cache probe creates a native audio output.
@MainActor
final class PlaybackAudioMedia {
    let resource: PlaybackResource
    private let options: PlaybackNetworkOptions
    private let budget: PlaybackNetworkBudget
    private var input: (any AudioByteInput)?
    private var route: HLSLoopbackRoute?
    private var preparing: Task<Void, Error>?
    private var closing: Task<Void, Error>?
    private var cancelled = false
    private var failure: Error?
    private var prepared = false
    private var playlistStorage: PlaybackAuxiliaryReservation?
    private(set) var session: AudioPlaybackSession?
    var onFailure: (@MainActor (Error) -> Void)?
    var duration: Double? { session?.information?.duration }
    var position: Double { session?.position ?? 0 }
    var localMediaFetch: Bool { resource.byteSource != nil || route != nil }
    var isReady: Bool { prepared && !cancelled && failure == nil }

    init(resource: PlaybackResource, options: PlaybackNetworkOptions = .init(), budget: PlaybackNetworkBudget) {
        self.resource = resource; self.options = options; self.budget = budget
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
                    input = PlaybackHTTPStreamInput(plan: plan, budget: budget)
                } else {
                    input = PlaybackAudioInput(plan: plan, budget: budget, onFailure: onFailure)
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
        let route = try HLSLoopbackRoute(plan: plan, budget: budget, onFailure: onFailure)
        self.route = route
        let url = try await withCheckedThrowingContinuation { continuation in
            startScopedHLS(route) { continuation.resume(with: $0) }
        }
        try check()
        guard let storage = budget.acquireAuxiliary(.storage, bytes: HLSPlaylist.maximumOutputBytes) else {
            throw PlaybackNetworkError.resourceBudgetExceeded
        }
        playlistStorage = storage
        let data = try await LoopbackAudioPlaylist.read(url)
        try check()
        input = PlaybackAudioInput(playlist: data, route: route, url: url)
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
                playlistStorage?.complete(); playlistStorage = nil
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
        let session = session, input = input, route = route, storage = playlistStorage
        Task {
            if let session { try? await session.close() }
            else { try? await input?.close() }
            await route?.close()
            storage?.complete()
        }
    }
}

/// The primary manifest is a bounded response from our exact local capability.
/// All upstream requests, rewrites and credentials remain in HLSLoopbackRoute.
private final class LoopbackAudioPlaylist: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let url: URL
    private var data = Data()
    private var failure: PlaybackNetworkError?
    private var continuation: CheckedContinuation<Data, Error>?
    private var session: URLSession!
    private let lock = NSLock()
    private var task: URLSessionDataTask?
    private var cancelled = false

    private init(_ url: URL) { self.url = url }

    static func read(_ url: URL) async throws -> Data {
        let request = LoopbackAudioPlaylist(url)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { request.start($0) }
        } onCancel: { request.cancel() }
    }

    private func start(_ continuation: CheckedContinuation<Data, Error>) {
        lock.lock(); defer { lock.unlock() }
        guard !cancelled else { continuation.resume(throwing: CancellationError()); return }
        self.continuation = continuation
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.httpShouldSetCookies = false
        config.urlCredentialStorage = nil; config.urlCache = nil
        config.httpAdditionalHeaders = nil; config.timeoutIntervalForResource = 30
        let queue = OperationQueue(); queue.maxConcurrentOperationCount = 1
        session = URLSession(configuration: config, delegate: self, delegateQueue: queue)
        var request = URLRequest(url: url)
        request.httpShouldHandleCookies = false
        task = session.dataTask(with: request)
        task!.resume()
    }

    private func cancel() {
        lock.lock(); defer { lock.unlock() }
        cancelled = true; task?.cancel()
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let response = response as? HTTPURLResponse, response.url == url, response.statusCode == 200 else {
            failure = .invalidResponse; completionHandler(.cancel); return
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard data.count <= HLSPlaylist.maximumOutputBytes - self.data.count else {
            failure = .resourceBudgetExceeded; dataTask.cancel(); return
        }
        self.data.append(data)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock()
        let continuation = continuation; self.continuation = nil
        let cancelled = cancelled
        self.task = nil
        lock.unlock()
        session.finishTasksAndInvalidate()
        self.session = nil
        if cancelled { continuation?.resume(throwing: CancellationError()) }
        else if let failure { continuation?.resume(throwing: failure) }
        else if error != nil { continuation?.resume(throwing: PlaybackNetworkError.transportFailed) }
        else if !ProgressiveMediaResponse.isPlaylistPrefix(data) {
            continuation?.resume(throwing: PlaybackNetworkError.hlsPlaylistInvalid)
        } else { continuation?.resume(returning: data) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        failure = .redirectDenied; completionHandler(nil)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        failure = .redirectDenied; completionHandler(.cancelAuthenticationChallenge, nil)
    }
}
#endif
