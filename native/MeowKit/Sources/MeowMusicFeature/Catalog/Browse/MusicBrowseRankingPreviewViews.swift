#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicBrowseRankingPreviewRow: UIView {
    private let rank = UILabel()
    private let song = UILabel()
    private let change = UILabel()

    override init(frame: CGRect) {
        super.init(frame: frame)
        rank.font = .systemFont(ofSize: 13)
        rank.textAlignment = .center
        rank.adjustsFontSizeToFitWidth = true
        song.font = .systemFont(ofSize: 13)
        song.lineBreakMode = .byTruncatingTail
        change.font = .systemFont(ofSize: 11)
        change.textAlignment = .center
        addSubview(rank)
        addSubview(song)
        addSubview(change)
        isAccessibilityElement = true
        rank.isAccessibilityElement = false
        song.isAccessibilityElement = false
        change.isAccessibilityElement = false
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(_ entry: MusicPublicBrowseRanking.Entry?, fallbackRank: Int, theme: PageTheme) {
        guard let entry else {
            isHidden = true
            rank.text = nil
            song.attributedText = nil
            change.text = nil
            accessibilityIdentifier = nil
            accessibilityLabel = nil
            return
        }
        isHidden = false
        rank.text = String(entry.rank ?? fallbackRank)
        rank.textColor = UIColor(theme.ink)
        let copy = NSMutableAttributedString(string: entry.title, attributes: [
            .font: UIFont.systemFont(ofSize: 13),
            .foregroundColor: UIColor(theme.ink),
        ])
        if !entry.artist.isEmpty {
            copy.append(NSAttributedString(string: " − " + entry.artist, attributes: [
                .font: UIFont.systemFont(ofSize: 13),
                .foregroundColor: UIColor(theme.muted),
            ]))
        }
        song.attributedText = copy
        let movement = entry.rankingMovement
        let symbol = movement?.symbol ?? ""
        change.text = symbol
        change.textColor = UIColor(movement?.isRising == true ? theme.accent : theme.muted)
        let identity = entry.id.isEmpty ? "rank-\(fallbackRank)" : entry.id
        accessibilityIdentifier = "music.browse.ranking.\(identity)"
        accessibilityLabel = [rank.text ?? "", entry.title, entry.artist, movement?.label ?? ""]
            .filter { !$0.isEmpty }.joined(separator: "，")
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        rank.frame = CGRect(x: 0, y: 0, width: 22, height: bounds.height)
        change.frame = CGRect(x: max(0, bounds.width - 20), y: 0, width: 20, height: bounds.height)
        song.frame = CGRect(x: 30, y: 0, width: max(0, bounds.width - 54), height: bounds.height)
    }
}

extension MusicPublicBrowseRanking.Entry {
    var rankingMovement: (symbol: String, label: String, isRising: Bool)? {
        guard let lastRank, let rank else { return nil }
        if lastRank == 0 { return ("新", "新上榜", true) }
        if lastRank == rank { return ("−", "排名持平", false) }
        return lastRank > rank ? ("▴", "排名上升", true) : ("▾", "排名下降", false)
    }
}

@MainActor
final class MusicBrowsePreviewButton: UIButton {
    private let cover = MusicArtworkView(size: 40)
    private let spinner = UIActivityIndicatorView(style: .medium)
    private let title = UILabel(), artist = UILabel()
    private let row = UIStackView()
    private lazy var pressFeedback = PressFeedback(view: row)
    private var pressedColor = UIColor.clear
    private var action: (() -> Void)?
    private var playbackLoading = false
    private(set) var reference: MusicReference
    var onHighlightChanged: (() -> Void)?

    init(track: MusicRemoteTrack, theme: PageTheme, images: MusicArtworkImages, action: @escaping () -> Void) {
        reference = track.ref
        super.init(frame: .zero)
        title.font = .systemFont(ofSize: 14); artist.font = .systemFont(ofSize: 11)
        let copy = UIStackView(arrangedSubviews: [title, artist]); copy.axis = .vertical; copy.spacing = 3
        row.addArrangedSubview(cover); row.addArrangedSubview(copy)
        row.spacing = 10; row.alignment = .center
        row.isUserInteractionEnabled = false
        layer.cornerRadius = 8
        addSubview(row); row.pinEdges(to: self, insets: UIEdgeInsets(top: 3, left: 0, bottom: 3, right: 8))
        spinner.isUserInteractionEnabled = false
        spinner.isAccessibilityElement = false
        addSubview(spinner)
        spinner.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            spinner.centerXAnchor.constraint(equalTo: cover.centerXAnchor),
            spinner.centerYAnchor.constraint(equalTo: cover.centerYAnchor),
        ])
        addAction(UIAction { [weak self] _ in self?.action?() }, for: .touchUpInside)
        configure(track: track, theme: theme, images: images, action: action)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var isHighlighted: Bool {
        didSet {
            pressFeedback.setPressed(isHighlighted)
            backgroundColor = isHighlighted ? pressedColor : .clear
            onHighlightChanged?()
        }
    }

    override var isHidden: Bool {
        didSet { updatePlaybackLoading() }
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        updatePlaybackLoading()
        if window == nil { resetPressFeedback() }
    }

    func resetPressFeedback() {
        isHighlighted = false
        pressFeedback.reset()
    }

    func cancelPendingArtworkLoad() {
        cover.cancelPendingArtworkLoad()
    }

    func resumePendingArtworkLoad() {
        cover.resumePendingLoadIfNeeded()
    }

    func configure(track: MusicRemoteTrack, theme: PageTheme, images: MusicArtworkImages, action: @escaping () -> Void) {
        if reference != track.ref { playbackLoading = false }
        reference = track.ref; self.action = action
        pressedColor = UIColor(theme.chip)
        backgroundColor = isHighlighted ? pressedColor : .clear
        cover.load(track, images: images)
        title.text = track.title; title.textColor = UIColor(theme.ink)
        artist.text = track.artist; artist.textColor = UIColor(theme.muted)
        accessibilityLabel = "播放" + track.title
        spinner.color = UIColor(theme.ink)
        spinner.accessibilityIdentifier = "music.browse.preview.loading.\(track.songId)"
        updatePlaybackLoading()
    }

    func setPlaybackLoading(_ loading: Bool) {
        playbackLoading = loading
        updatePlaybackLoading()
    }

    private func updatePlaybackLoading() {
        let loading = playbackLoading && window != nil && !isHidden
        cover.alpha = loading ? 0 : 1
        isEnabled = !loading && !reference.id.isEmpty
        accessibilityValue = loading ? "加载中" : nil
        if loading { if !spinner.isAnimating { spinner.startAnimating() } }
        else { spinner.stopAnimating() }
    }
}
#endif
