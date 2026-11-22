#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicQueueCurrentPage: UIViewController, UITableViewDelegate {
    private let layout = MusicQueueListLayout()
    lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: layout)
    let mode = UIButton(type: .system)
    let retry = UIButton(type: .system), returnToQueue = UIButton(type: .system)
    let recommendations = UITableView(frame: .zero, style: .plain)
    private var recommendationDataSource: UITableViewDiffableDataSource<Int, String>!
    private let recommendationStatus = UILabel()
    private var candidates: [MusicSimilarModeCandidate] = []
    private var candidatesByID: [String: (candidate: MusicSimilarModeCandidate, index: Int)] = [:]
    private var snapshotID: UUID?
    private var theme = PageTheme()
    var onCandidate: ((UUID, String) -> Void)?
    private let controls = UIStackView()
    private let empty = UILabel()

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear
        layout.minimumLineSpacing = 0
        layout.minimumInteritemSpacing = 0
        collection.backgroundColor = .clear
        collection.contentInsetAdjustmentBehavior = .never
        collection.alwaysBounceVertical = true
        collection.isDirectionalLockEnabled = true
        collection.accessibilityIdentifier = "music.queue.current"
        controls.distribution = .fillEqually
        controls.spacing = 8
        for (button, id) in [(mode, "mode"), (retry, "retry"), (returnToQueue, "return")] {
            controls.addArrangedSubview(button)
            button.accessibilityIdentifier = "music.queue." + id
        }
        retry.setTitle("重试", for: .normal)
        returnToQueue.setTitle("返回原队列", for: .normal)
        controls.isHidden = true
        empty.textAlignment = .center
        view.addSubview(collection)
        view.addSubview(controls)
        recommendations.backgroundColor = .clear
        recommendations.separatorStyle = .none
        recommendations.rowHeight = 64
        recommendations.contentInsetAdjustmentBehavior = .never
        recommendations.isDirectionalLockEnabled = true
        recommendations.delegate = self
        recommendations.accessibilityIdentifier = "music.queue.recommendations"
        recommendations.register(MusicQueueSongCell.self, forCellReuseIdentifier: MusicQueueSongCell.reuseIdentifier)
        recommendationDataSource = UITableViewDiffableDataSource<Int, String>(tableView: recommendations) { [weak self] tableView, indexPath, id in
            let cell = tableView.dequeueReusableCell(withIdentifier: MusicQueueSongCell.reuseIdentifier, for: indexPath)
            if let self, let entry = self.candidatesByID[id] {
                self.configure(cell, candidate: entry.candidate, index: entry.index)
            }
            return cell
        }
        recommendationDataSource.defaultRowAnimation = .none
        recommendationStatus.font = .systemFont(ofSize: 13)
        recommendationStatus.accessibilityIdentifier = "music.queue.recommendation.status"
        view.addSubview(recommendationStatus)
        view.addSubview(recommendations)
    }

    func configure(snapshot: MusicSimilarModeSnapshot?, recommendations: MusicQueueRecommendationSnapshot?,
                   changing: Bool, returning: Bool, count: Int, modeTitle: String, theme: PageTheme, fm: MusicFMPlaybackSnapshot? = nil) {
        loadViewIfNeeded()
        self.theme = theme
        let active = snapshot?.active == true
        let needsReadback = snapshot?.error == .commitUnconfirmed ||
            recommendations?.error == .commitUnconfirmed || fm?.error == .commitUnconfirmed
        controls.isHidden = snapshot == nil && recommendations == nil && fm == nil && !returning
        mode.isHidden = returning && !active
        retry.isHidden = !needsReadback && snapshot?.canRetry != true && recommendations?.canRetry != true
        retry.setTitle(needsReadback ? "核对结果" : "重试", for: .normal)
        returnToQueue.isHidden = snapshot?.canRestoreQueue != true
        mode.setTitle(modeTitle, for: .normal)
        for button in [mode, retry, returnToQueue] {
            button.tintColor = UIColor(theme.ink)
            button.backgroundColor = UIColor(theme.chip)
            button.layer.cornerRadius = 12
            button.titleLabel?.font = .systemFont(ofSize: 13)
            button.isEnabled = button === returnToQueue || !changing
        }
        let old = candidates
        let oldByID = candidatesByID
        candidates = recommendations?.candidates ?? []
        snapshotID = recommendations?.id
        var nextByID: [String: (candidate: MusicSimilarModeCandidate, index: Int)] = [:]
        var ids: [String] = []
        var toReconfigure: [String] = []
        var seen: Set<String> = []
        for (index, candidate) in candidates.enumerated() {
            guard seen.insert(candidate.id).inserted else { continue }
            nextByID[candidate.id] = (candidate, index)
            ids.append(candidate.id)
            if let prev = oldByID[candidate.id], prev.candidate.song != candidate.song || prev.index != index {
                toReconfigure.append(candidate.id)
            }
        }
        candidatesByID = nextByID
        var snapshot = NSDiffableDataSourceSnapshot<Int, String>()
        snapshot.appendSections([0])
        snapshot.appendItems(ids, toSection: 0)
        if !toReconfigure.isEmpty { snapshot.reconfigureItems(toReconfigure) }
        recommendationDataSource.apply(snapshot, animatingDifferences: false)
        if old.map(\.id) == candidates.map(\.id) {
            for indexPath in self.recommendations.indexPathsForVisibleRows ?? [] {
                guard let id = recommendationDataSource.itemIdentifier(for: indexPath),
                      let entry = candidatesByID[id],
                      let cell = self.recommendations.cellForRow(at: indexPath) else { continue }
                configure(cell, candidate: entry.candidate, index: entry.index)
            }
        }
        self.recommendations.isHidden = candidates.isEmpty
        self.recommendations.isUserInteractionEnabled = !changing
        recommendationStatus.isHidden = candidates.isEmpty
        recommendationStatus.textColor = UIColor(theme.muted)
        recommendationStatus.text = "更多推荐"
        empty.text = count == 0 ? "没有正在播放歌曲" : nil
        empty.textColor = UIColor(theme.muted)
        collection.backgroundView = empty.text == nil ? nil : empty
        view.setNeedsLayout()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let width = view.bounds.width, height = view.bounds.height
        let footer: CGFloat = controls.isHidden ? 0 : 52
        let candidateHeight: CGFloat = recommendations.isHidden ? 0 : min(192, max(64, (height - footer) * 0.4))
        let heading: CGFloat = recommendationStatus.isHidden ? 0 : 28
        let queueHeight = max(0, height - footer - candidateHeight - heading)
        collection.frame = CGRect(x: 0, y: 0, width: width, height: queueHeight)
        recommendationStatus.frame = CGRect(x: 8, y: queueHeight, width: max(0, width - 16), height: heading)
        recommendations.frame = CGRect(x: 0, y: queueHeight + heading, width: width, height: candidateHeight)
        controls.frame = CGRect(x: 0, y: height - footer + 8, width: width, height: 44)
    }

    private func configure(_ cell: UITableViewCell, candidate: MusicSimilarModeCandidate, index: Int) {
        guard let cell = cell as? MusicQueueSongCell else { return }
        cell.row.configure(title: candidate.song.title, artist: candidate.song.artist, index: index,
                           active: false, favorite: nil, removable: false, reorderable: false, theme: theme)
        cell.accessibilityIdentifier = "music.queue.recommendation." + candidate.id
        cell.accessibilityLabel = [candidate.song.title, candidate.song.artist].filter { !$0.isEmpty }.joined(separator: "，")
        cell.accessibilityTraits = .button
    }
    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: false)
        guard let snapshotID, let id = recommendationDataSource.itemIdentifier(for: indexPath) else { return }
        onCandidate?(snapshotID, id)
    }
}

@MainActor
private final class MusicQueueListLayout: UICollectionViewFlowLayout {
    override func prepare() {
        if let collectionView, collectionView.bounds.width > 0 {
            let insets = collectionView.adjustedContentInset
            let size = CGSize(width: max(0, collectionView.bounds.width - insets.left - insets.right), height: 64)
            if itemSize != size { itemSize = size }
        }
        super.prepare()
    }

    override func shouldInvalidateLayout(forBoundsChange newBounds: CGRect) -> Bool {
        newBounds.width != collectionView?.bounds.width || super.shouldInvalidateLayout(forBoundsChange: newBounds)
    }
}

@MainActor
final class MusicQueueTablePage: UIViewController {
    let table = UITableView(frame: .zero, style: .plain)
    private let empty = UILabel()
    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear
        table.backgroundColor = .clear
        table.separatorStyle = .none
        table.rowHeight = 64
        table.contentInsetAdjustmentBehavior = .never
        table.isDirectionalLockEnabled = true
        empty.textAlignment = .center
        view.addSubview(table)
    }
    func setEmpty(_ text: String?, theme: PageTheme) {
        loadViewIfNeeded()
        empty.text = text; empty.textColor = UIColor(theme.muted)
        table.backgroundView = text == nil ? nil : empty
    }
    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        table.frame = view.bounds
    }
}
#endif
