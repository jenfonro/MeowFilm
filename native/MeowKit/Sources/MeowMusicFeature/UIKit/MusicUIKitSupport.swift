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
public final class MusicArtworkImages {
    public static let shared = MusicArtworkImages()
    fileprivate let images: ArtworkImages
    private var selector: (any MusicArtworkSelecting)?

    init(http: MusicArtworkHTTP = MusicArtworkHTTP()) {
        images = ArtworkImages(maximumBytes: MusicArtworkHTTP.maximumBytes) { try await http.read($0) }
    }

    func cached(_ url: URL) -> UIImage? { images.cached(url) }
    var revision: UUID { images.revision }

    func configure(cache: any MusicPresentationCaching, selector: (any MusicArtworkSelecting)?) {
        images.setByteCache(cache); self.selector = selector
    }
    func read(_ url: URL) async throws -> (UIImage, Data?, URL) {
        do {
            let value = try await images.read(url)
            return (value.0, value.1, url)
        } catch let error as URLError where error.code == .dataLengthExceedsMaximum {
            guard let rendition = MusicArtworkHTTP.boundedNeteaseRendition(url) else { throw error }
            let value = try await images.read(rendition)
            return (value.0, value.1, rendition)
        }
    }
    func failed(songID: String, url: URL, attempted: Set<URL>) async throws -> URL? {
        try await selector?.failedArtwork(songID: songID, url: url, attempted: attempted)
    }
    func succeeded(songID: String, url: URL, replacing requested: URL) async throws {
        _ = try await selector?.succeededArtwork(songID: songID, url: url, replacing: requested)
    }
    public func close() async { await images.close() }
}

@MainActor
final class MusicArtworkView: ArtworkView {
    private let sharedImages: MusicArtworkImages
    private var representedSongID: String?
    private var inputURL: URL?
    private var requestedURL: URL?
    private var selectionOriginURL: URL?
    private var attempted: Set<URL> = []
    private var selectionTask: Task<Void, Never>?
    init(size: CGFloat = 48, images: MusicArtworkImages = .shared) {
        sharedImages = images
        super.init(size: size, images: images.images)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func load(_ track: MusicRemoteTrack) {
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
    func load(_ url: URL?) {
        selectionTask?.cancel()
        representedSongID = nil; inputURL = url; requestedURL = url; selectionOriginURL = url; attempted.removeAll()
        super.load(url)
    }
    override func didLoadImage(_ image: UIImage, data: Data?) {
        if let songID = representedSongID, let url = requestedURL, let requested = selectionOriginURL {
            selectionTask?.cancel()
            selectionTask = Task { [sharedImages] in
                try? await sharedImages.succeeded(songID: songID, url: url, replacing: requested)
            }
        }
        super.didLoadImage(image, data: data)
    }
    override func didFailImage(_ url: URL) {
        guard let songID = representedSongID, requestedURL == url else { super.didFailImage(url); return }
        attempted.insert(url)
        let attempted = attempted, images = sharedImages, origin = selectionOriginURL ?? url
        selectionTask?.cancel()
        selectionTask = Task { [weak self] in
            do {
                let next = try await images.failed(songID: songID, url: origin, attempted: attempted.union([origin]))
                guard !Task.isCancelled, let self, self.representedSongID == songID, self.requestedURL == url else { return }
                self.requestedURL = next; self.selectionOriginURL = next; self.loadSelected(next, songID: songID)
            } catch {}
        }
        super.didFailImage(url)
    }
    override func didFailImage(_ url: URL, error: Error) {
        if (error as? URLError)?.code == .dataLengthExceedsMaximum,
           let rendition = MusicArtworkHTTP.boundedNeteaseRendition(url) {
            attempted.insert(url); requestedURL = rendition
            if let songID = representedSongID { loadSelected(rendition, songID: songID) }
            else { super.load(rendition) }
            return
        }
        didFailImage(url)
    }
    private func loadSelected(_ url: URL?, songID: String) { super.load(url, displayIdentity: songID) }

    override func retryFailedLoad() {
        if requestedURL == nil, !attempted.isEmpty, let inputURL, let songID = representedSongID {
            attempted.removeAll(); requestedURL = inputURL; selectionOriginURL = inputURL
            loadSelected(inputURL, songID: songID)
        } else {
            super.retryFailedLoad()
            if let songID = representedSongID { loadSelected(requestedURL, songID: songID) }
        }
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
    deinit { selectionTask?.cancel() }
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
        titleLabel.font = .systemFont(ofSize: 16)
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
        artwork.load(track)
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
        titleLabel.font = .systemFont(ofSize: 16)
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
