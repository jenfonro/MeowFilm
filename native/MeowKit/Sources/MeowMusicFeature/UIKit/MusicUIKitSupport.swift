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
enum MusicArtworkImages {
    nonisolated private static let http = MusicArtworkHTTP()
    private static let cache: NSCache<NSURL, UIImage> = {
        let value = NSCache<NSURL, UIImage>()
        value.countLimit = 128
        value.totalCostLimit = 24 * 1_024 * 1_024
        return value
    }()

    static func cached(_ url: URL) -> UIImage? { cache.object(forKey: url as NSURL) }

    static func read(_ url: URL, cachedData: Data? = nil) async throws -> (UIImage, Data?) {
        if cachedData == nil, let image = cached(url) { return (image, nil) }
        let (image, data) = try await readBytes(url, cachedData: cachedData)
        try Task.checkCancellation()
        let cost = (image.cgImage?.width ?? 0) * (image.cgImage?.height ?? 0) * 4
        cache.setObject(image, forKey: url as NSURL, cost: cost)
        return (image, data)
    }

    /// Cancellation follows the physical read; decoding stays off the UI actor.
    nonisolated private static func readBytes(_ url: URL, cachedData: Data?) async throws -> (UIImage, Data) {
        let maximum = MusicArtworkHTTP.maximumBytes
        if let cachedData, cachedData.count <= maximum, let value = decode(cachedData) {
            try Task.checkCancellation()
            return (value, cachedData)
        }
        let data = try await http.read(url)
        try Task.checkCancellation()
        guard let value = decode(data) else { throw URLError(.cannotDecodeContentData) }
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
    private var task: Task<Void, Never>?
    private var representedURL: URL?
    private var completed = false

    init(size: CGFloat = 48) {
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
        if representedURL == url, task != nil || completed { return }
        task?.cancel()
        task = nil
        representedURL = url
        completed = false
        image = nil
        guard let url else { completed = true; return }
        if cachedData == nil, let cached = MusicArtworkImages.cached(url) {
            image = cached
            completed = true
            onImage?(cached, nil)
            return
        }
        task = Task { [weak self] in
            do {
                let (value, data) = try await MusicArtworkImages.read(url, cachedData: cachedData)
                try Task.checkCancellation()
                guard self?.representedURL == url else { return }
                self?.image = value
                self?.task = nil
                self?.completed = true
                self?.onImage?(value, data)
            } catch {
                guard !Task.isCancelled, self?.representedURL == url else { return }
                self?.task = nil
                self?.completed = true
                self?.onFailure?(url)
            }
        }
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
