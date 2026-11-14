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
    fileprivate let images: ArtworkImages

    init(http: MusicArtworkHTTP = MusicArtworkHTTP()) {
        images = ArtworkImages(maximumBytes: MusicArtworkHTTP.maximumBytes) { try await http.read($0) }
    }

    func cached(_ url: URL) -> UIImage? { images.cached(url) }

    func read(_ url: URL, cachedData: Data? = nil) async throws -> (UIImage, Data?) {
        try await images.read(url, cachedData: cachedData)
    }
}

@MainActor
final class MusicArtworkView: ArtworkView {
    init(size: CGFloat = 48, images: MusicArtworkImages = .shared) {
        super.init(size: size, images: images.images)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

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
