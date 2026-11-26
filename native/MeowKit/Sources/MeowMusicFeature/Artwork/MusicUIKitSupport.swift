#if os(iOS)
import UIKit
import MeowDebug
import MeowUI

@MainActor
public class MusicPageViewController: PageViewController {
    public let appearance: MusicAppearancePreferencesStore
    public init(appearance: MusicAppearancePreferencesStore) {
        self.appearance = appearance
        super.init(themeProvider: appearance)
    }
}

@MainActor
public final class MusicArtworkImages {
    private final class WeakArtworkView {
        weak var view: MusicArtworkView?
        init(_ view: MusicArtworkView) { self.view = view }
    }

    fileprivate let images: ArtworkImages
    let effects = MusicArtworkEffectsCache()
    private var selector: NativeMusicService?
    private var failedViews: [ObjectIdentifier: WeakArtworkView] = [:]

    public convenience init() {
        self.init(http: MusicArtworkHTTP())
    }

    init(http: MusicArtworkHTTP) {
        images = ArtworkImages(maximumBytes: 4 * 1_024 * 1_024) { try await http.read($0) }
    }

    func cached(_ url: URL) -> UIImage? { images.cached(url) }
    var revision: UUID { images.revision }

    public func debugCacheStats() -> DebugJSONValue {
        .object([
            "images": images.debugCacheStats(),
            "effects": effects.debugStats(),
            "trackedFailedViews": .int(failedViews.count),
        ])
    }

    public func debugClearMemoryCache() {
        effects.clear()
        images.debugClearMemoryCache()
    }

    func configure(cache: MusicNativePresentationCache, selector: NativeMusicService?) {
        images.setByteCache(cache); self.selector = selector
    }
    func read(_ url: URL) async throws -> (UIImage, Data?, URL) {
        let value = try await images.read(url)
        return (value.0, value.1, url)
    }
    func failed(songID: String, url: URL, attempted: Set<URL>) async throws -> URL? {
        try await selector?.failedArtwork(songID: songID, url: url, attempted: attempted)
    }
    func succeeded(songID: String, url: URL, replacing requested: URL) async throws {
        _ = try await selector?.succeededArtwork(songID: songID, url: url, replacing: requested)
    }

    fileprivate func registerFailed(_ view: MusicArtworkView) {
        failedViews[ObjectIdentifier(view)] = WeakArtworkView(view)
    }

    fileprivate func unregisterFailed(_ view: MusicArtworkView) {
        failedViews.removeValue(forKey: ObjectIdentifier(view))
    }

    /// Retries only currently-visible artwork views that recorded a retryable failure — zero `subviews` crawling.
    public func retryFailedVisible() {
        var stale: [ObjectIdentifier] = []
        let snapshot = Array(failedViews)
        for (id, box) in snapshot {
            guard let view = box.view else { stale.append(id); continue }
            if view.isEligibleForVisibleRetry {
                view.retryFailedLoad()
            }
        }
        for id in stale { failedViews.removeValue(forKey: id) }
    }

    public func close() async {
        failedViews.removeAll()
        effects.clear()
        await images.close()
    }
}

@MainActor
final class MusicArtworkView: ArtworkView {
    private weak var sharedImages: MusicArtworkImages?
    private var representedSongID: String?
    private var inputURL: URL?
    private var requestedURL: URL?
    private var selectionOriginURL: URL?
    private var attempted: Set<URL> = []
    private var selectionTask: Task<Void, Never>?

    init(size: CGFloat = 48, images: MusicArtworkImages? = nil) {
        sharedImages = images
        super.init(size: size, images: images?.images)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func bind(images: MusicArtworkImages) {
        if sharedImages !== images {
            sharedImages?.unregisterFailed(self)
            sharedImages = images
            attachImages(images.images)
        }
    }

    func load(_ track: MusicRemoteTrack, images: MusicArtworkImages? = nil) {
        if let images { bind(images: images) }
        sharedImages?.unregisterFailed(self)
        if representedSongID == track.songId, inputURL == track.artwork {
            loadSelected(requestedURL, songID: track.songId)
            return
        }
        selectionTask?.cancel()
        representedSongID = track.songId; inputURL = track.artwork; requestedURL = track.artwork
        selectionOriginURL = track.artwork
        attempted.removeAll()
        super.load(track.artwork, displayIdentity: track.songId)
    }
    func load(_ url: URL?, images: MusicArtworkImages? = nil) {
        if let images { bind(images: images) }
        sharedImages?.unregisterFailed(self)
        selectionTask?.cancel()
        representedSongID = nil; inputURL = url; requestedURL = url; selectionOriginURL = url; attempted.removeAll()
        super.load(url)
    }
    override func didLoadImage(_ image: UIImage, data: Data?) {
        sharedImages?.unregisterFailed(self)
        if let sharedImages, let songID = representedSongID, let url = requestedURL, let requested = selectionOriginURL {
            selectionTask?.cancel()
            selectionTask = Task { [sharedImages] in
                try? await sharedImages.succeeded(songID: songID, url: url, replacing: requested)
            }
        }
        super.didLoadImage(image, data: data)
    }
    override func didFailImage(_ url: URL) {
        sharedImages?.registerFailed(self)
        guard let sharedImages, let songID = representedSongID, requestedURL == url else {
            super.didFailImage(url)
            return
        }
        attempted.insert(url)
        let attempted = attempted, images = sharedImages, origin = selectionOriginURL ?? url
        selectionTask?.cancel()
        selectionTask = Task { [weak self] in
            do {
                let next = try await images.failed(songID: songID, url: origin, attempted: attempted.union([origin]))
                guard !Task.isCancelled, let self, self.representedSongID == songID, self.requestedURL == url else { return }
                self.requestedURL = next; self.selectionOriginURL = next; self.loadSelected(next, songID: songID)
            } catch {
                if !(error is CancellationError) && !Task.isCancelled {
                    DebugRuntime.logError(.musicCatalog, sub: "artwork", event: "fallback.failed", error: error, level: .warn)
                }
            }
        }
        super.didFailImage(url)
    }
    override func didFailImage(_ url: URL, error: Error) {
        didFailImage(url)
    }
    private func loadSelected(_ url: URL?, songID: String) { super.load(url, displayIdentity: songID) }

    private var hasPendingRetryableFailure: Bool {
        hasRetryableFailure || (requestedURL == nil && !attempted.isEmpty && inputURL != nil && representedSongID != nil)
    }

    override func retryFailedLoad() {
        guard hasPendingRetryableFailure else { return }
        sharedImages?.unregisterFailed(self)
        if requestedURL == nil, !attempted.isEmpty, let inputURL, let songID = representedSongID {
            attempted.removeAll(); requestedURL = inputURL; selectionOriginURL = inputURL
            loadSelected(inputURL, songID: songID)
        } else {
            super.retryFailedLoad()
        }
    }

    var isEligibleForVisibleRetry: Bool {
        guard let window, !isHidden, alpha > 0,
              convert(bounds, to: window).intersects(window.bounds) else { return false }
        return true
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        if hasPendingRetryableFailure, isEligibleForVisibleRetry {
            retryFailedLoad()
        }
    }

    func cancelPendingArtworkLoad() {
        selectionTask?.cancel()
        selectionTask = nil
        cancelPendingLoad()
    }

    func resumePendingLoadIfNeeded() {
        guard image == nil else { return }
        if let songID = representedSongID {
            loadSelected(requestedURL, songID: songID)
        } else if let requestedURL {
            super.load(requestedURL)
        }
    }

    deinit { selectionTask?.cancel() }
}

enum MusicPagingTabTransition {
    static func selectionAmount(position: CGFloat, index: Int) -> CGFloat {
        max(0, 1 - abs(position - CGFloat(index)))
    }

    static func interval(position: CGFloat, count: Int) -> (lower: Int, upper: Int, progress: CGFloat)? {
        guard count > 0 else { return nil }
        let value = min(CGFloat(count - 1), max(0, position))
        let lower = Int(value)
        return (lower, min(lower + 1, count - 1), value - CGFloat(lower))
    }

    static func interpolate(_ lower: CGFloat, _ upper: CGFloat, progress: CGFloat) -> CGFloat {
        lower + (upper - lower) * progress
    }
}
#endif
