#if os(iOS)
import UIKit

@MainActor
open class ArtworkView: UIImageView {
    public var onImage: ((UIImage, Data?) -> Void)?
    public var onFailure: ((URL) -> Void)?
    private var images: ArtworkImages?
    private var task: Task<Void, Never>?
    private var representedURL: URL?
    private var displayIdentity: String?
    private var generation: UInt64 = 0
    private var completed = false
    private enum Failure { case retryable, deterministic }
    private var failure: Failure?

    public init(size: CGFloat = 48, images: ArtworkImages? = nil) {
        self.images = images
        super.init(frame: .zero)
        contentMode = .scaleAspectFill
        clipsToBounds = true
        layer.cornerRadius = max(8, size * 0.14)
        backgroundColor = UIColor.white.withAlphaComponent(0.18)
        if size > 0 {
            NSLayoutConstraint.activate([
                widthAnchor.constraint(equalToConstant: size),
                heightAnchor.constraint(equalToConstant: size),
            ])
        }
    }

    @available(*, unavailable)
    required public init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public func attachImages(_ images: ArtworkImages) {
        self.images = images
    }

    public func load(_ url: URL?, displayIdentity: String? = nil, cachedData: Data? = nil) {
        if self.displayIdentity == displayIdentity, representedURL == url, task != nil || completed || failure != nil { return }
        let retainImage = displayIdentity != nil && self.displayIdentity == displayIdentity
        task?.cancel()
        task = nil
        generation &+= 1
        let expected = generation
        representedURL = url
        self.displayIdentity = displayIdentity
        completed = false
        failure = nil
        if !retainImage { image = nil }
        guard let url else { completed = true; return }
        guard let images else { return }
        if cachedData == nil, let cached = images.cached(url) {
            image = cached
            completed = true
            didLoadImage(cached, data: nil)
            return
        }
        task = Task { [weak self] in
            do {
                let (value, data) = try await images.read(url, cachedData: cachedData)
                try Task.checkCancellation()
                guard let self, self.generation == expected, self.representedURL == url else { return }
                if self.image == nil { self.image = value }
                else {
                    UIView.transition(with: self, duration: 0.2, options: .transitionCrossDissolve) { self.image = value }
                }
                self.task = nil
                self.completed = true
                self.failure = nil
                self.didLoadImage(value, data: data)
            } catch {
                guard !Task.isCancelled, let self,
                      self.generation == expected, self.representedURL == url else { return }
                self.task = nil
                if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
                switch (error as? URLError)?.code {
                case .badURL, .unsupportedURL, .cannotDecodeRawData, .cannotDecodeContentData,
                     .dataLengthExceedsMaximum:
                    self.failure = .deterministic
                default:
                    self.failure = .retryable
                }
                self.didFailImage(url, error: error)
            }
        }
    }

    open func didLoadImage(_ image: UIImage, data: Data?) { onImage?(image, data) }
    open func didFailImage(_ url: URL) { onFailure?(url) }
    open func didFailImage(_ url: URL, error: Error) { didFailImage(url) }

    open func retryFailedLoad() {
        guard failure == .retryable, let representedURL else { return }
        failure = nil
        load(representedURL, displayIdentity: displayIdentity)
    }

    /// Cancels only an in-flight admission without releasing or clearing already-admitted images.
    public func cancelPendingLoad() {
        guard let pending = task else { return }
        generation &+= 1
        pending.cancel()
        task = nil
    }

    deinit { task?.cancel() }
}
#endif
