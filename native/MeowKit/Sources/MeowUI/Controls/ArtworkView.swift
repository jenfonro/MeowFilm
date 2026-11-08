#if os(iOS)
import UIKit

@MainActor
open class ArtworkView: UIImageView {
    public var onImage: ((UIImage, Data?) -> Void)?
    public var onFailure: ((URL) -> Void)?
    private let images: ArtworkImages
    private var task: Task<Void, Never>?
    private var representedURL: URL?
    private var generation: UInt64 = 0
    private var completed = false
    private enum Failure { case retryable, deterministic }
    private var failure: Failure?

    public init(size: CGFloat = 48, images: ArtworkImages) {
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

    public func load(_ url: URL?, cachedData: Data? = nil) {
        if representedURL == url, task != nil || completed || failure != nil { return }
        task?.cancel()
        task = nil
        generation &+= 1
        let expected = generation
        representedURL = url
        completed = false
        failure = nil
        image = nil
        guard let url else { completed = true; return }
        if cachedData == nil, let cached = images.cached(url) {
            image = cached
            completed = true
            onImage?(cached, nil)
            return
        }
        let images = self.images
        task = Task { [weak self] in
            do {
                let (value, data) = try await images.read(url, cachedData: cachedData)
                try Task.checkCancellation()
                guard let self, self.generation == expected, self.representedURL == url else { return }
                self.image = value
                self.task = nil
                self.completed = true
                self.failure = nil
                self.onImage?(value, data)
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
                self.onFailure?(url)
            }
        }
    }

    public func retryFailedLoad() {
        guard failure == .retryable, let representedURL else { return }
        failure = nil
        load(representedURL)
    }

    deinit { task?.cancel() }
}
#endif
