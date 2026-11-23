#if os(iOS)
import UIKit
import MeowUI

/// Virtualized horizontal cast rail backed by `MeowUI.IndexedDiffableProjection`.
@MainActor
final class FilmCastRailView: UIView, UICollectionViewDelegateFlowLayout {
    private struct ProjectedMember: Equatable {
        let id: String
        let member: FilmCastMember
    }

    private let images: ArtworkImages
    private let layout = UICollectionViewFlowLayout()
    private let collectionView: UICollectionView
    private let emptyLabel = UILabel()
    private var projection = IndexedDiffableProjection<Int, String, ProjectedMember>()
    private var dataSource: UICollectionViewDiffableDataSource<Int, String>!
    private var theme: PageTheme
    private var heightConstraint: NSLayoutConstraint!

    init(images: ArtworkImages, theme: PageTheme) {
        self.images = images
        self.theme = theme
        layout.scrollDirection = .horizontal
        layout.minimumLineSpacing = 12
        layout.sectionInset = UIEdgeInsets(top: 0, left: 16, bottom: 0, right: 16)
        collectionView = UICollectionView(frame: .zero, collectionViewLayout: layout)
        super.init(frame: .zero)

        collectionView.backgroundColor = .clear
        collectionView.showsHorizontalScrollIndicator = false
        collectionView.delegate = self
        collectionView.register(FilmCastCell.self, forCellWithReuseIdentifier: FilmCastCell.reuseIdentifier)
        collectionView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(collectionView)
        collectionView.pinEdges(to: self)

        emptyLabel.text = "暂无演职人员"
        emptyLabel.font = .systemFont(ofSize: 14)
        emptyLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(emptyLabel)
        NSLayoutConstraint.activate([
            emptyLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16),
            emptyLabel.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])

        heightConstraint = heightAnchor.constraint(equalToConstant: 180)
        heightConstraint.isActive = true

        dataSource = UICollectionViewDiffableDataSource<Int, String>(collectionView: collectionView) { [weak self] cv, indexPath, id in
            let cell = cv.dequeueReusableCell(withReuseIdentifier: FilmCastCell.reuseIdentifier, for: indexPath) as! FilmCastCell
            if let self, let entry = self.projection[id] {
                cell.configure(member: entry.member, images: self.images, theme: self.theme)
            }
            return cell
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func apply(members: [FilmCastMember], theme: PageTheme) {
        self.theme = theme
        emptyLabel.isHidden = !members.isEmpty
        emptyLabel.textColor = UIColor(theme.muted)
        collectionView.isHidden = members.isEmpty
        heightConstraint.constant = members.isEmpty ? 36 : 180

        let projected = members.enumerated().map { index, member in
            ProjectedMember(id: "\(index):\(member.name):\(member.role)", member: member)
        }
        let update = projection.update(section: 0, items: projected, id: { $0.id })
        dataSource.applyIfNeeded(update, animatingDifferences: false)
    }

    func applyTheme(_ theme: PageTheme) {
        self.theme = theme
        emptyLabel.textColor = UIColor(theme.muted)
        for cell in collectionView.visibleCells {
            (cell as? FilmCastCell)?.applyTheme(theme)
        }
    }

    func cancelVisibleArtworks() {
        for cell in collectionView.visibleCells {
            (cell as? FilmCastCell)?.cancelPendingLoad()
        }
    }

    func resumeVisibleArtworks() {
        for cell in collectionView.visibleCells {
            (cell as? FilmCastCell)?.resumePendingLoad()
        }
    }

    func collectionView(_ collectionView: UICollectionView, willDisplay cell: UICollectionViewCell,
                        forItemAt indexPath: IndexPath) {
        (cell as? FilmCastCell)?.resumePendingLoad()
    }

    func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                        forItemAt indexPath: IndexPath) {
        (cell as? FilmCastCell)?.cancelPendingLoad()
    }

    func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
                        sizeForItemAt indexPath: IndexPath) -> CGSize {
        CGSize(width: 86, height: 176)
    }
}

@MainActor
private final class FilmCastCell: UICollectionViewCell {
    static let reuseIdentifier = "film.detail.cast.cell"

    private var portrait: ArtworkView?
    private let name = UILabel()
    private let role = UILabel()
    private let stack = UIStackView()
    private var currentURL: URL?

    override init(frame: CGRect) {
        super.init(frame: frame)
        name.font = .systemFont(ofSize: 13)
        name.numberOfLines = 2
        name.textAlignment = .center
        role.font = .systemFont(ofSize: 12)
        role.numberOfLines = 2
        role.textAlignment = .center
        stack.axis = .vertical
        stack.spacing = 6
        stack.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(stack)
        stack.pinEdges(to: contentView)
        isAccessibilityElement = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        currentURL = nil
        portrait?.load(nil)
    }

    func cancelPendingLoad() {
        portrait?.cancelPendingLoad()
    }

    func resumePendingLoad() {
        guard let currentURL else { return }
        portrait?.load(currentURL)
    }

    func configure(member: FilmCastMember, images: ArtworkImages, theme: PageTheme) {
        let art: ArtworkView
        if let existing = portrait {
            art = existing
        } else {
            let created = ArtworkView(size: 0, images: images)
            created.layer.cornerRadius = 10
            created.heightAnchor.constraint(equalTo: created.widthAnchor, multiplier: 1.25).isActive = true
            stack.addArrangedSubview(created)
            stack.addArrangedSubview(name)
            stack.addArrangedSubview(role)
            portrait = created
            art = created
        }
        if currentURL != member.portraitURL {
            currentURL = member.portraitURL
            art.load(member.portraitURL)
        }
        name.text = member.name
        role.text = member.role
        accessibilityLabel = [member.name, member.role].filter { !$0.isEmpty }.joined(separator: "，")
        applyTheme(theme)
    }

    func applyTheme(_ theme: PageTheme) {
        portrait?.backgroundColor = UIColor(theme.chip)
        name.textColor = UIColor(theme.ink)
        role.textColor = UIColor(theme.muted)
    }
}
#endif
