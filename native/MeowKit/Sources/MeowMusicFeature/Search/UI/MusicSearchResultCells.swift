#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicSearchPlaylistCell: UITableViewCell {
    private var tiles: [MusicBrowseCell] = []
    private var columns = 1
    private var items: [MusicPublicBrowseCollection] = []
    private var open: ((MusicPublicBrowseCollection) -> Void)?
    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear; selectionStyle = .none
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func prepareForReuse() {
        super.prepareForReuse()
        for tile in tiles { tile.setDisplayVisible(false) }
        resetPressFeedback()
    }
    func resetPressFeedback() {
        for tile in tiles { tile.resetPressFeedback() }
    }
    func configure(_ items: [MusicPublicBrowseCollection], columns: Int, theme: PageTheme, images: MusicArtworkImages,
                   open: @escaping (MusicPublicBrowseCollection) -> Void) {
        self.columns = columns
        self.open = open
        let changed = self.items.map(\.id) != items.map(\.id)
        self.items = items
        if changed {
            for tile in tiles { tile.removeFromSuperview() }
            tiles = items.enumerated().map { index, _ in
                let tile = MusicBrowseCell(frame: .zero)
                tile.setOpenAction { [weak self] in
                    guard let self, self.items.indices.contains(index) else { return }
                    self.open?(self.items[index])
                }
                contentView.addSubview(tile)
                return tile
            }
        }
        for (tile, item) in zip(tiles, items) {
            tile.configure(item: .init(id: item.id, title: item.title,
                subtitle: [item.count.map { "\($0)首" }, item.creator].compactMap { $0 }.joined(separator: " · "),
                artwork: item.artwork, content: .collection(item)),
                layout: columns == 1 ? .list : .grid, theme: theme, images: images,
                showsPlayButton: false, onPlay: {})
            tile.setDisplayVisible(true)
        }
        setNeedsLayout()
    }
    override func layoutSubviews() {
        super.layoutSubviews()
        let width = (bounds.width - 32 - CGFloat(columns - 1) * 12) / CGFloat(columns)
        for (index, tile) in tiles.enumerated() {
            tile.frame = CGRect(x: 16 + CGFloat(index) * (width + 12), y: 0,
                                width: width, height: bounds.height)
        }
    }
}

@MainActor
final class MusicSearchTableController: UIViewController {
    let table = MusicSearchResultsTableView(frame: .zero, style: .plain)
    var dataSource: UITableViewDiffableDataSource<Int, MusicSearchResultRowID>!
    var playlistRows: [Data: [MusicPublicBrowseCollection]] = [:]
    var headerStates: [Int: MusicSearchHeaderState] = [:]
    var footerStates: [Int: MusicSearchFooterState] = [:]
    var skeletons: [Int: MusicLoadingSkeletonView] = [:]
    var columns = 1
    var onLayout: (() -> Void)?
    override func loadView() { view = table }
    override func viewDidLayoutSubviews() { super.viewDidLayoutSubviews(); onLayout?() }
}

enum MusicSearchResultRowID: Hashable { case heading(Int), song(Data), playlists(Data), status(Int) }

struct MusicSearchHeaderState: Equatable {
    let title: String
    let showsPlay: Bool
    let request: UUID
}

struct MusicSearchFooterState: Equatable {
    enum Action: Equatable { case cancel, more, category }
    let message: String?
    let label: String?
    let action: Action?
    let showsSkeleton: Bool
    let request: UUID
}


@MainActor
final class MusicSearchStatusCell: UITableViewCell {
    static let reuseIdentifier = "MusicSearchStatusCell"
    private var hostedFooter: UIView?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        selectionStyle = .none
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        setFooter(nil)
    }

    func setFooter(_ footer: UIView?) {
        if let hostedFooter, hostedFooter !== footer {
            hostedFooter.removeFromSuperview()
        }
        hostedFooter = footer
        guard let footer else { return }
        if footer.superview !== contentView {
            contentView.addSubview(footer)
            footer.pinEdges(to: contentView)
        }
    }
}

#endif
