#if os(iOS) || os(macOS)
import Foundation
@preconcurrency import AVFoundation
import UniformTypeIdentifiers
import MeowContracts

/// Lifetime follows one controller item, not one screen. AVURLAsset retains its
/// resourceLoader but not its delegate, so keep this binding until teardown.
final class NativePlaybackAsset {
    let asset: AVURLAsset
    private let progressive: ProgressiveAssetResourceLoader?
    private let hls: HLSLoopbackRoute?
    private let cached: CachedAssetResourceLoader?

    init(plan: PlaybackNetworkPlan, budget: PlaybackNetworkBudget,
         configuration: URLSessionConfiguration = .ephemeral,
         onFailure: @escaping @Sendable (PlaybackNetworkError) -> Void) throws {
        hls = nil
        cached = nil
        switch plan.route {
        case .scopedHLS:
            // A real HTTP listener must be ready first. Never hand HLS an
            // invented resourceLoader scheme or block the main actor waiting.
            throw PlaybackNetworkError.loopbackUnavailable
        case .local:
            asset = AVURLAsset(url: plan.url)
            progressive = nil
        case .systemHTTP:
            asset = AVURLAsset(url: plan.url, options: try Self.systemOptions(plan))
            progressive = nil
        case .progressive:
            // An opaque per-attempt URL cannot expose the original signed URL
            // or headers via AVFoundation-generated errors. No real listener.
            let opaque = URL(string: "meow-progressive://\(UUID().uuidString.lowercased())/media")!
            let loader = ProgressiveAssetResourceLoader(
                url: opaque, plan: plan, budget: budget, configuration: configuration, onFailure: onFailure
            )
            progressive = loader
            // This lane owns exactly one container. External QuickTime/media
            // references must not escape the URLSession scope/budget via AVF.
            asset = AVURLAsset(url: opaque, options: [
                AVURLAssetReferenceRestrictionsKey: NSNumber(value: AVAssetReferenceRestrictions.forbidAll.rawValue)
            ])
            asset.resourceLoader.setDelegate(loader, queue: loader.core.queue)
        }
    }

    init(hls: HLSLoopbackRoute, url: URL) {
        self.hls = hls
        cached = nil
        progressive = nil
        // No upstream credentials reach AVFoundation. The URL is a short-lived
        // local capability; arbitrary local request headers are never forwarded.
        asset = AVURLAsset(url: url, options: [AVURLAssetHTTPCookiesKey: [HTTPCookie]()])
    }

    init(source: any PlaybackByteSource, onFailure: @escaping @Sendable (PlaybackNetworkError) -> Void) {
        hls = nil; progressive = nil
        let url = URL(string: "meow-cache://\(source.id.uuidString.lowercased())/media")!
        let loader = CachedAssetResourceLoader(url: url, source: source, onFailure: onFailure)
        cached = loader
        asset = AVURLAsset(url: url, options: [
            AVURLAssetReferenceRestrictionsKey: NSNumber(value: AVAssetReferenceRestrictions.forbidAll.rawValue)
        ])
        asset.resourceLoader.setDelegate(loader, queue: loader.queue)
    }

    static func systemOptions(_ plan: PlaybackNetworkPlan, ambientCookies: [HTTPCookie]? = nil) throws -> [String: Any] {
        // Apple's cookie option supplements rather than replaces this store.
        // Do not mutate/clear global state or silently borrow another account.
        guard (ambientCookies ?? HTTPCookieStorage.shared.cookies ?? []).isEmpty else {
            throw PlaybackNetworkError.nativeCookieStorageNotIsolated
        }
        var options: [String: Any] = [AVURLAssetHTTPCookiesKey: [HTTPCookie]()]
        if let userAgent = plan.headers["user-agent"] { options[AVURLAssetHTTPUserAgentKey] = userAgent }
        if let cookie = plan.headers["cookie"] {
            options[AVURLAssetHTTPCookiesKey] = try PlaybackNetworkPlan.standardCookies(cookie, url: plan.url)
        }
        return options
    }

    func invalidate() {
        // Fence byte/redirect callbacks BEFORE releasing the old player item.
        progressive?.invalidate()
        cached?.invalidate()
        hls?.invalidate()
        asset.resourceLoader.setDelegate(nil, queue: nil)
        asset.cancelLoading()
    }

    deinit { invalidate() }
}

private final class CachedAssetResourceLoader: NSObject, AVAssetResourceLoaderDelegate, @unchecked Sendable {
    let queue = DispatchQueue(label: "meow.playback.cached-bytes")
    private let queueKey = DispatchSpecificKey<Bool>()
    private let url: URL
    private let source: any PlaybackByteSource
    private let onFailure: @Sendable (PlaybackNetworkError) -> Void
    private var tasks: [ObjectIdentifier: Task<Void, Never>] = [:]
    private var invalidated = false
    private var failed = false

    init(url: URL, source: any PlaybackByteSource, onFailure: @escaping @Sendable (PlaybackNetworkError) -> Void) {
        self.url = url; self.source = source; self.onFailure = onFailure
        super.init()
        queue.setSpecific(key: queueKey, value: true)
    }

    func resourceLoader(_ resourceLoader: AVAssetResourceLoader,
                        shouldWaitForLoadingOfRequestedResource request: AVAssetResourceLoadingRequest) -> Bool {
        guard !invalidated, request.request.url == url else { return false }
        let id = ObjectIdentifier(request)
        do {
            let sink = try NativeProgressiveSink(request, onFinish: {})
            let source = self.source
            tasks[id] = Task { [weak self] in
                do {
                    let information = try await source.information()
                    try Task.checkCancellation()
                    self?.deliver(id) {
                        sink.receive(.init(length: information.length, mime: information.mime,
                                           supportsRanges: true, validator: nil))
                    }
                    if let range = sink.range {
                        var offset = range.start
                        let end = min(range.endExclusive ?? information.length, information.length)
                        while offset < end {
                            try Task.checkCancellation()
                            let data = try await source.read(offset: offset, count: Int(min(65_536, end - offset)))
                            try Task.checkCancellation()
                            guard !data.isEmpty else { throw PlaybackNetworkError.invalidResponse }
                            self?.deliver(id) { sink.receive(data) }
                            offset += Int64(data.count)
                        }
                    }
                    self?.finish(id, sink: sink, error: nil)
                } catch is CancellationError {
                    self?.finish(id, sink: sink, error: .cancelled)
                } catch {
                    self?.finish(id, sink: sink, error: error as? PlaybackNetworkError ?? .transportFailed)
                }
            }
        } catch { request.finishLoading(with: PlaybackNetworkError.invalidResponse) }
        return true
    }

    private func deliver(_ id: ObjectIdentifier, _ body: () -> Void) {
        queue.sync { if !invalidated, tasks[id] != nil { body() } }
    }

    private func finish(_ id: ObjectIdentifier, sink: NativeProgressiveSink, error: PlaybackNetworkError?) {
        queue.async { [weak self] in
            guard let self, !self.invalidated, self.tasks.removeValue(forKey: id) != nil else { return }
            sink.finish(error)
            if let error, error != .cancelled, !self.failed {
                self.failed = true
                self.onFailure(error)
            }
        }
    }

    func resourceLoader(_ resourceLoader: AVAssetResourceLoader,
                        didCancel loadingRequest: AVAssetResourceLoadingRequest) {
        tasks.removeValue(forKey: ObjectIdentifier(loadingRequest))?.cancel()
    }

    func invalidate() {
        let stop = {
            guard !self.invalidated else { return }
            self.invalidated = true
            for task in self.tasks.values { task.cancel() }
            self.tasks.removeAll()
        }
        if DispatchQueue.getSpecific(key: queueKey) == true { stop() } else { queue.sync(execute: stop) }
        source.retire()
    }
}

private final class ProgressiveAssetResourceLoader: NSObject, AVAssetResourceLoaderDelegate {
    let core: ProgressiveMediaLoader
    private let url: URL
    private var sinks: [ObjectIdentifier: NativeProgressiveSink] = [:]
    private var invalidated = false // core.queue only

    init(url: URL, plan: PlaybackNetworkPlan, budget: PlaybackNetworkBudget,
         configuration: URLSessionConfiguration,
         onFailure: @escaping @Sendable (PlaybackNetworkError) -> Void) {
        self.url = url
        core = ProgressiveMediaLoader(plan: plan, budget: budget, configuration: configuration, onFailure: onFailure)
    }

    func resourceLoader(_ resourceLoader: AVAssetResourceLoader,
                        shouldWaitForLoadingOfRequestedResource loadingRequest: AVAssetResourceLoadingRequest) -> Bool {
        guard !invalidated, loadingRequest.request.url == url else { return false }
        do {
            let id = ObjectIdentifier(loadingRequest)
            let sink = try NativeProgressiveSink(loadingRequest) { [weak self] in self?.sinks[id] = nil }
            sinks[id] = sink
            core.enqueue(sink)
        } catch {
            loadingRequest.finishLoading(with: PlaybackNetworkError.invalidResponse)
        }
        return true
    }

    func resourceLoader(_ resourceLoader: AVAssetResourceLoader,
                        didCancel loadingRequest: AVAssetResourceLoadingRequest) {
        if let sink = sinks.removeValue(forKey: ObjectIdentifier(loadingRequest)) { core.cancel(sink) }
    }

    func invalidate() {
        core.performSynchronized {
            invalidated = true
            core.invalidate()
            sinks.removeAll()
        }
    }
}

private final class NativeProgressiveSink: ProgressiveMediaSink {
    let range: ProgressiveReadRange?
    private var request: AVAssetResourceLoadingRequest?
    private let onFinish: () -> Void

    init(_ request: AVAssetResourceLoadingRequest, onFinish: @escaping () -> Void) throws {
        self.request = request; self.onFinish = onFinish
        if let data = request.dataRequest {
            range = try ProgressiveReadRange(requestedOffset: data.requestedOffset, currentOffset: data.currentOffset,
                                             requestedLength: data.requestedLength,
                                             toEnd: data.requestsAllDataToEndOfResource)
        } else {
            guard request.contentInformationRequest != nil else { throw PlaybackNetworkError.invalidResponse }
            range = nil
        }
    }

    func receive(_ information: ProgressiveContentInformation) {
        guard let request, !request.isCancelled else { return }
        let content = request.contentInformationRequest
        content?.contentType = (UTType(mimeType: information.mime) ?? .data).identifier
        content?.contentLength = information.length
        content?.isByteRangeAccessSupported = information.supportsRanges
    }

    func receive(_ data: Data) {
        guard let request, !request.isCancelled else { return }
        request.dataRequest?.respond(with: data)
    }

    func finish(_ error: PlaybackNetworkError?) {
        guard let request else { return }
        self.request = nil
        if !request.isCancelled {
            if let error { request.finishLoading(with: error) } else { request.finishLoading() }
        }
        onFinish()
    }
}
#endif
