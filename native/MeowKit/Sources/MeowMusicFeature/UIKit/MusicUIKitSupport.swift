#if os(iOS)
import UIKit
import Combine
import MeowUI
import ImageIO

@MainActor
public class MusicPageViewController: PageViewController {
    public let appearance: MusicAppearancePreferencesStore
    public init(appearance: MusicAppearancePreferencesStore) {
        self.appearance = appearance
        super.init(themeProvider: appearance)
    }
}

@MainActor
final class MusicArtworkImages {
    static let shared = MusicArtworkImages()
    nonisolated private let http: MusicArtworkHTTP
    private let cache: NSCache<NSURL, UIImage> = {
        let value = NSCache<NSURL, UIImage>()
        value.countLimit = 128
        value.totalCostLimit = 24 * 1_024 * 1_024
        return value
    }()
    private final class Flight {
        var consumers: [UUID: CheckedContinuation<(UIImage, Data?), Error>] = [:]
        var task: Task<Void, Never>?
        var abandoned = false
        let cachedData: Data?

        init(cachedData: Data?) { self.cachedData = cachedData }
    }
    private var flights: [URL: Flight] = [:]

    init(http: MusicArtworkHTTP = MusicArtworkHTTP()) { self.http = http }

    func cached(_ url: URL) -> UIImage? { cache.object(forKey: url as NSURL) }

    func read(_ url: URL, cachedData: Data? = nil) async throws -> (UIImage, Data?) {
        try Task.checkCancellation()
        if cachedData == nil, let image = cached(url) { return (image, nil) }

        let consumerID = UUID()
        let result = try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation {
                (continuation: CheckedContinuation<(UIImage, Data?), Error>) in
                guard !Task.isCancelled else {
                    continuation.resume(throwing: CancellationError())
                    return
                }
                if let flight = flights[url], !flight.abandoned {
                    flight.consumers[consumerID] = continuation
                    return
                }
                let flight = Flight(cachedData: cachedData)
                flight.consumers[consumerID] = continuation
                flights[url] = flight
                flight.task = Task {
                    do {
                        let (image, data) = try await self.readBytes(url, cachedData: flight.cachedData)
                        try Task.checkCancellation()
                        self.complete(flight, for: url, with: .success((image, data)))
                    } catch {
                        self.complete(flight, for: url, with: .failure(error))
                    }
                }
            }
        } onCancel: {
            Task { @MainActor in self.cancel(consumerID, for: url) }
        }
        try Task.checkCancellation()
        return result
    }

    private func store(_ image: UIImage, for url: URL) {
        let cost = (image.cgImage?.width ?? 0) * (image.cgImage?.height ?? 0) * 4
        cache.setObject(image, forKey: url as NSURL, cost: cost)
    }

    private func complete(
        _ flight: Flight,
        for url: URL,
        with result: Result<(UIImage, Data), Error>
    ) {
        if flights[url] === flight { flights[url] = nil }
        flight.task = nil
        if !flight.abandoned, case .success(let (image, _)) = result { store(image, for: url) }
        let value: Result<(UIImage, Data?), Error> = result.map { ($0.0, Optional($0.1)) }
        let consumers = Array(flight.consumers.values)
        flight.consumers.removeAll()
        for continuation in consumers { continuation.resume(with: value) }
    }

    private func cancel(_ consumerID: UUID, for url: URL) {
        guard let flight = flights[url], let continuation = flight.consumers.removeValue(forKey: consumerID)
        else { return }
        guard flight.consumers.isEmpty else {
            continuation.resume(throwing: CancellationError())
            return
        }
        // The last consumer settles only when the physical read has stopped.
        flight.consumers[consumerID] = continuation
        flight.abandoned = true
        flight.task?.cancel()
    }

    /// Cancellation follows the physical read; decoding stays off the UI actor.
    nonisolated private func readBytes(_ url: URL, cachedData: Data?) async throws -> (UIImage, Data) {
        let maximum = MusicArtworkHTTP.maximumBytes
        if let cachedData, cachedData.count <= maximum, let value = Self.decode(cachedData) {
            try Task.checkCancellation()
            return (value, cachedData)
        }
        let data = try await http.read(url)
        try Task.checkCancellation()
        guard let value = Self.decode(data) else { throw URLError(.cannotDecodeContentData) }
        return (value, data)
    }

    nonisolated private static func decode(_ data: Data) -> UIImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceThumbnailMaxPixelSize: 1_024,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceShouldCacheImmediately: true,
              ] as CFDictionary) else { return nil }
        return UIImage(cgImage: image)
    }
}

@MainActor
final class MusicArtworkView: UIImageView {
    var onImage: ((UIImage, Data?) -> Void)?
    var onFailure: ((URL) -> Void)?
    private let images: MusicArtworkImages
    private var task: Task<Void, Never>?
    private var representedURL: URL?
    private var completed = false
    private var failed = false

    init(size: CGFloat = 48, images: MusicArtworkImages = .shared) {
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
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func load(_ url: URL?, cachedData: Data? = nil) {
        if representedURL == url, task != nil || completed || failed { return }
        task?.cancel()
        task = nil
        representedURL = url
        completed = false
        failed = false
        image = nil
        guard let url else { completed = true; return }
        if cachedData == nil, let cached = images.cached(url) {
            image = cached
            completed = true
            onImage?(cached, nil)
            return
        }
        let images = images
        task = Task { [weak self] in
            do {
                let (value, data) = try await images.read(url, cachedData: cachedData)
                try Task.checkCancellation()
                guard self?.representedURL == url else { return }
                self?.image = value
                self?.task = nil
                self?.completed = true
                self?.failed = false
                self?.onImage?(value, data)
            } catch {
                guard !Task.isCancelled, self?.representedURL == url else { return }
                self?.task = nil
                self?.failed = true
                self?.onFailure?(url)
            }
        }
    }

    func retryFailedLoad() {
        guard failed, let representedURL else { return }
        failed = false
        load(representedURL)
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        Self.retryFailedLoads(in: self)
    }

    static func retryFailedLoads(in view: UIView) {
        guard let window = view.window, !view.isHidden, view.alpha > 0,
              view.convert(view.bounds, to: window).intersects(window.bounds) else { return }
        var ancestor = view.superview
        while let current = ancestor {
            guard !current.isHidden, current.alpha > 0 else { return }
            ancestor = current.superview
        }
        (view as? MusicArtworkView)?.retryFailedLoad()
        for child in view.subviews { retryFailedLoads(in: child) }
    }

    deinit { task?.cancel() }
}

@MainActor
final class MusicTrackCell: UITableViewCell {
    static let reuseIdentifier = "MusicTrackCell"
    private(set) var representedReference: MusicReference?
    private let artwork = MusicArtworkView()
    private let titleLabel = UILabel()
    private let subtitleLabel = UILabel()
    private let activeView = UIView()

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        selectionStyle = .none
        activeView.layer.cornerRadius = 2
        activeView.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(activeView)
        let labels = UIStackView(arrangedSubviews: [titleLabel, subtitleLabel])
        labels.axis = .vertical
        labels.spacing = 3
        labels.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(artwork)
        contentView.addSubview(labels)
        artwork.translatesAutoresizingMaskIntoConstraints = false
        titleLabel.font = .systemFont(ofSize: 16, weight: .medium)
        subtitleLabel.font = .systemFont(ofSize: 12)
        subtitleLabel.numberOfLines = 1
        NSLayoutConstraint.activate([
            activeView.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 7),
            activeView.centerYAnchor.constraint(equalTo: contentView.centerYAnchor),
            activeView.widthAnchor.constraint(equalToConstant: 3),
            activeView.heightAnchor.constraint(equalToConstant: 26),
            artwork.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 16),
            artwork.centerYAnchor.constraint(equalTo: contentView.centerYAnchor),
            labels.leadingAnchor.constraint(equalTo: artwork.trailingAnchor, constant: 12),
            labels.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -16),
            labels.centerYAnchor.constraint(equalTo: contentView.centerYAnchor),
            contentView.heightAnchor.constraint(greaterThanOrEqualToConstant: 64),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(track: MusicRemoteTrack, active: Bool, theme: PageTheme, index: Int? = nil) {
        representedReference = track.ref
        artwork.load(track.artwork)
        titleLabel.text = index.map { "\($0 + 1). \(track.title)" } ?? track.title
        subtitleLabel.text = [track.artist, track.album].filter { !$0.isEmpty }.joined(separator: " · ")
        titleLabel.textColor = UIColor(active ? theme.accent : theme.ink)
        subtitleLabel.textColor = UIColor(theme.muted)
        activeView.backgroundColor = active ? UIColor(theme.accent) : .clear
        accessibilityLabel = [track.title, track.artist].filter { !$0.isEmpty }.joined(separator: "，")
    }
}

@MainActor
final class MusicCollectionCell: UITableViewCell {
    static let reuseIdentifier = "MusicCollectionCell"
    private let artwork = MusicArtworkView()
    private let titleLabel = UILabel()
    private let subtitleLabel = UILabel()

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        accessoryType = .disclosureIndicator
        let labels = UIStackView(arrangedSubviews: [titleLabel, subtitleLabel])
        labels.axis = .vertical
        labels.spacing = 3
        labels.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(artwork)
        contentView.addSubview(labels)
        artwork.translatesAutoresizingMaskIntoConstraints = false
        titleLabel.font = .systemFont(ofSize: 16, weight: .semibold)
        subtitleLabel.font = .systemFont(ofSize: 12)
        NSLayoutConstraint.activate([
            artwork.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 16),
            artwork.centerYAnchor.constraint(equalTo: contentView.centerYAnchor),
            labels.leadingAnchor.constraint(equalTo: artwork.trailingAnchor, constant: 12),
            labels.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -28),
            labels.centerYAnchor.constraint(equalTo: contentView.centerYAnchor),
            contentView.heightAnchor.constraint(greaterThanOrEqualToConstant: 68),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(title: String, subtitle: String, artworkURL: URL?, theme: PageTheme) {
        artwork.load(artworkURL)
        titleLabel.text = title
        subtitleLabel.text = subtitle
        titleLabel.textColor = UIColor(theme.ink)
        subtitleLabel.textColor = UIColor(theme.muted)
        tintColor = UIColor(theme.muted)
        accessibilityLabel = [title, subtitle].filter { !$0.isEmpty }.joined(separator: "，")
    }
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

@MainActor
func musicSystemButton(
    symbol: String,
    accessibilityLabel: String,
    action: @escaping @MainActor () -> Void
) -> UIButton {
    let button = UIButton(type: .system)
    button.setImage(UIImage(systemName: symbol), for: .normal)
    button.accessibilityLabel = accessibilityLabel
    button.addAction(UIAction { _ in action() }, for: .touchUpInside)
    return button
}

#endif
