#if os(iOS)
import UIKit
import MeowUI

/// Pure UIKit channel picker sheet backed by `UICollectionViewDiffableDataSource`.
@MainActor
final class MusicChannelsPickerViewController: PageViewController, UICollectionViewDelegate, UICollectionViewDelegateFlowLayout {
    private static let rowHeight: CGFloat = 54
    private static let gridSpacing: CGFloat = 12
    private static let bottomPadding: CGFloat = 24
    private static let bottomContainerInset: CGFloat = 12
    private static let fadeHeight: CGFloat = 20
    private static let headerHeight: CGFloat = 64

    private struct ChannelItem: Equatable {
        let channel: MusicPublicBrowseChannel
        let selected: Bool
    }

    var channels: [MusicPublicBrowseChannel]
    private var projection = IndexedDiffableProjection<Int, String, ChannelItem>()
    var selectedID: String?
    private var emptyTitle: String
    private let onSelect: (MusicPublicBrowseChannel) -> Void
    private var lastLayoutWidth: CGFloat = 0
    private var lastFadeState: (size: CGSize, showsFade: Bool)?

    private let closeButton = UIButton(type: .system)
    private let titleLabel = UILabel()
    private let emptyLabel = UILabel()
    private let fadeMask = CAGradientLayer()
    private let collectionContainer = UIView()
    private let layout = UICollectionViewFlowLayout()
    private lazy var collectionView = UICollectionView(frame: .zero, collectionViewLayout: layout)
    private var dataSource: UICollectionViewDiffableDataSource<Int, String>!

    var preferredHeight: CGFloat {
        let bodyHeight: CGFloat
        if channels.isEmpty {
            bodyHeight = 96
        } else {
            let rows = (channels.count + 2) / 3
            bodyHeight = CGFloat(rows) * Self.rowHeight
                + CGFloat(max(0, rows - 1)) * Self.gridSpacing
                + Self.bottomPadding
        }
        return Self.headerHeight + bodyHeight + Self.bottomContainerInset
    }

    override var usesPageHeader: Bool { false }

    init(channels: [MusicPublicBrowseChannel], selectedID: String?, emptyTitle: String,
         appearance: MusicAppearancePreferencesStore, onSelect: @escaping (MusicPublicBrowseChannel) -> Void) {
        self.channels = channels
        self.selectedID = selectedID
        self.emptyTitle = emptyTitle
        self.onSelect = onSelect
        super.init(themeProvider: appearance)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.layer.cornerRadius = 24
        view.layer.maskedCorners = [.layerMinXMaxYCorner, .layerMaxXMaxYCorner]
        view.clipsToBounds = true
        view.accessibilityIdentifier = "music.channels.picker"

        closeButton.setImage(UIImage(
            systemName: "xmark",
            withConfiguration: UIImage.SymbolConfiguration(pointSize: 17, weight: .regular)
        ), for: .normal)
        closeButton.accessibilityLabel = "关闭"
        closeButton.accessibilityIdentifier = "music.channels.picker.close"
        closeButton.addAction(UIAction { [weak self] _ in self?.dismiss(animated: true) }, for: .touchUpInside)
        closeButton.translatesAutoresizingMaskIntoConstraints = false

        titleLabel.text = "选择频道"
        titleLabel.font = .systemFont(ofSize: 20)
        titleLabel.textAlignment = .center
        titleLabel.translatesAutoresizingMaskIntoConstraints = false

        let header = UIView()
        header.translatesAutoresizingMaskIntoConstraints = false
        header.addSubview(closeButton)
        header.addSubview(titleLabel)
        view.addSubview(header)

        emptyLabel.font = .systemFont(ofSize: 14)
        emptyLabel.textAlignment = .center
        emptyLabel.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(emptyLabel)

        layout.minimumInteritemSpacing = Self.gridSpacing
        layout.minimumLineSpacing = Self.gridSpacing
        layout.sectionInset = UIEdgeInsets(top: 0, left: 16, bottom: Self.bottomPadding, right: 16)
        collectionView.backgroundColor = .clear
        collectionView.showsVerticalScrollIndicator = false
        collectionView.alwaysBounceVertical = true
        collectionView.contentInsetAdjustmentBehavior = .never
        collectionView.delegate = self
        collectionView.register(MusicChannelPickerCell.self, forCellWithReuseIdentifier: MusicChannelPickerCell.reuseIdentifier)
        collectionView.translatesAutoresizingMaskIntoConstraints = false
        collectionContainer.translatesAutoresizingMaskIntoConstraints = false
        collectionContainer.addSubview(collectionView)
        collectionView.pinEdges(to: collectionContainer)
        fadeMask.colors = [UIColor.black.cgColor, UIColor.black.cgColor, UIColor.clear.cgColor]
        collectionContainer.layer.mask = fadeMask
        view.addSubview(collectionContainer)

        NSLayoutConstraint.activate([
            header.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8),
            header.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 16),
            header.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -16),
            header.heightAnchor.constraint(equalToConstant: 44),
            closeButton.leadingAnchor.constraint(equalTo: header.leadingAnchor),
            closeButton.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            closeButton.widthAnchor.constraint(equalToConstant: 44),
            closeButton.heightAnchor.constraint(equalToConstant: 44),
            titleLabel.centerXAnchor.constraint(equalTo: header.centerXAnchor),
            titleLabel.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            titleLabel.leadingAnchor.constraint(greaterThanOrEqualTo: closeButton.trailingAnchor, constant: 8),
            collectionContainer.topAnchor.constraint(equalTo: header.bottomAnchor, constant: 12),
            collectionContainer.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            collectionContainer.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            collectionContainer.bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -Self.bottomContainerInset),
            emptyLabel.centerXAnchor.constraint(equalTo: collectionContainer.centerXAnchor),
            emptyLabel.centerYAnchor.constraint(equalTo: collectionContainer.centerYAnchor),
        ])

        dataSource = UICollectionViewDiffableDataSource<Int, String>(collectionView: collectionView) { [weak self] cv, indexPath, id in
            let cell = cv.dequeueReusableCell(withReuseIdentifier: MusicChannelPickerCell.reuseIdentifier, for: indexPath) as! MusicChannelPickerCell
            if let self, let entry = self.projection[id] {
                cell.configure(channel: entry.channel, selected: entry.selected, theme: self.theme)
            }
            return cell
        }
        applyTheme()
        applyChannelsSnapshot()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        if abs(lastLayoutWidth - collectionView.bounds.width) > 0.5 {
            lastLayoutWidth = collectionView.bounds.width
            layout.invalidateLayout()
        }
        updateFadeMask()
    }

    func scrollViewDidScroll(_ scrollView: UIScrollView) {
        updateFadeMask()
    }

    private func updateFadeMask() {
        let height = collectionContainer.bounds.height
        guard height > 0 else { return }
        let remaining = collectionView.contentSize.height - (collectionView.contentOffset.y + collectionView.bounds.height)
        let showsFade = remaining > 1
        if let lastFadeState,
           lastFadeState.size == collectionContainer.bounds.size,
           lastFadeState.showsFade == showsFade {
            return
        }
        lastFadeState = (collectionContainer.bounds.size, showsFade)
        let solid = height > Self.fadeHeight ? (height - Self.fadeHeight) / height : 0
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        fadeMask.frame = collectionContainer.bounds
        if showsFade {
            fadeMask.colors = [UIColor.black.cgColor, UIColor.black.cgColor, UIColor.clear.cgColor]
            fadeMask.locations = [0, NSNumber(value: Double(solid)), 1]
        } else {
            fadeMask.colors = [UIColor.black.cgColor, UIColor.black.cgColor, UIColor.black.cgColor]
            fadeMask.locations = [0, 0.5, 1]
        }
        CATransaction.commit()
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        closeButton.tintColor = UIColor(theme.muted)
        titleLabel.textColor = UIColor(theme.ink)
        emptyLabel.textColor = UIColor(theme.muted)
        guard dataSource != nil else { return }
        for indexPath in collectionView.indexPathsForVisibleItems {
            guard let id = dataSource.itemIdentifier(for: indexPath),
                  let entry = projection[id],
                  let cell = collectionView.cellForItem(at: indexPath) as? MusicChannelPickerCell else { continue }
            cell.configure(channel: entry.channel, selected: entry.selected, theme: theme)
        }
    }

    func update(channels: [MusicPublicBrowseChannel], selectedID: String?, emptyTitle: String) {
        self.channels = channels
        self.selectedID = selectedID
        self.emptyTitle = emptyTitle
        applyChannelsSnapshot()
    }

    private func applyChannelsSnapshot() {
        guard isViewLoaded, dataSource != nil else { return }
        emptyLabel.text = emptyTitle
        emptyLabel.isHidden = !channels.isEmpty
        collectionContainer.isHidden = channels.isEmpty

        let projected = channels.map { ChannelItem(channel: $0, selected: $0.identity == selectedID) }
        let update = projection.update(section: 0, items: projected, id: { $0.channel.identity })
        lastFadeState = nil
        dataSource.applyIfNeeded(update, animatingDifferences: false) { [weak self] in
            self?.updateFadeMask()
        }
        view.setNeedsLayout()
    }

    func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
                        sizeForItemAt indexPath: IndexPath) -> CGSize {
        let available = max(0, collectionView.bounds.width - 32 - Self.gridSpacing * 2)
        return CGSize(width: floor(available / 3), height: Self.rowHeight)
    }

    func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        guard let id = dataSource.itemIdentifier(for: indexPath), let entry = projection[id] else { return }
        let channel = entry.channel
        dismiss(animated: true) { [onSelect = self.onSelect] in onSelect(channel) }
    }
}

@MainActor
private final class MusicChannelPickerCell: UICollectionViewCell {
    static let reuseIdentifier = "music.channel.picker.cell"

    private let label = UILabel()
    private lazy var feedback = PressFeedback(view: contentView)

    override var isHighlighted: Bool {
        didSet { feedback.setPressed(isHighlighted) }
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        contentView.layer.cornerRadius = 13
        contentView.layer.borderWidth = 1
        contentView.clipsToBounds = true
        label.font = .systemFont(ofSize: 15)
        label.textAlignment = .center
        label.numberOfLines = 1
        label.lineBreakMode = .byTruncatingTail
        label.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(label)
        NSLayoutConstraint.activate([
            label.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 8),
            label.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -8),
            label.centerYAnchor.constraint(equalTo: contentView.centerYAnchor),
        ])
        isAccessibilityElement = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        feedback.reset()
    }

    func configure(channel: MusicPublicBrowseChannel, selected: Bool, theme: PageTheme) {
        label.text = channel.title
        label.textColor = UIColor(selected ? theme.accent : theme.ink)
        contentView.backgroundColor = UIColor(selected ? theme.buttonSelected : theme.buttonSurface)
        contentView.layer.borderColor = UIColor(selected ? theme.accent : theme.border).cgColor
        accessibilityIdentifier = "music.channel.\(channel.identity)"
        accessibilityLabel = channel.title
        accessibilityTraits = selected ? [.button, .selected] : .button
    }
}
#endif
