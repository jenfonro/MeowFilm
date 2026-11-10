#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicQueueCurrentPage: UIViewController, UITableViewDataSource, UITableViewDelegate {
    private let layout = MusicQueueListLayout()
    lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: layout)
    let mode = UIButton(type: .system), continuePlaying = UIButton(type: .system)
    let retry = UIButton(type: .system), returnToQueue = UIButton(type: .system)
    let recommendations = UITableView(frame: .zero, style: .plain)
    private let recommendationStatus = UILabel()
    private var candidates: [MusicSimilarModeCandidate] = []
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
        for (button, id) in [(mode, "mode"), (continuePlaying, "continue"), (retry, "retry"), (returnToQueue, "return")] {
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
        recommendations.dataSource = self; recommendations.delegate = self
        recommendations.accessibilityIdentifier = "music.queue.recommendations"
        recommendations.register(MusicQueueSongCell.self, forCellReuseIdentifier: MusicQueueSongCell.reuseIdentifier)
        recommendationStatus.font = .systemFont(ofSize: 13, weight: .semibold)
        recommendationStatus.accessibilityIdentifier = "music.queue.recommendation.status"
        view.addSubview(recommendationStatus)
        view.addSubview(recommendations)
    }

    func configure(snapshot: MusicSimilarModeSnapshot?, recommendations: MusicQueueRecommendationSnapshot?,
                   changing: Bool, returning: Bool, count: Int, modeTitle: String, theme: PageTheme, fm: MusicFMPlaybackSnapshot? = nil) {
        loadViewIfNeeded()
        self.theme = theme
        let active = snapshot?.active == true
        controls.isHidden = !active && recommendations == nil && fm == nil && !returning
        mode.isHidden = returning && !active
        continuePlaying.isHidden = active ? snapshot?.supportsContinuation != true : recommendations == nil && fm == nil
        retry.isHidden = !(active ? snapshot?.canRetry == true : recommendations?.canRetry == true)
        returnToQueue.isHidden = snapshot?.canRestoreQueue != true
        mode.setTitle(modeTitle, for: .normal)
        let enabled = fm?.continueEnabled ?? (active ? snapshot?.continueEnabled == true : recommendations?.continueEnabled == true)
        continuePlaying.setTitle(enabled ? "续播已开启" : "续播已关闭", for: .normal)
        continuePlaying.accessibilityValue = enabled ? "开启" : "关闭"
        for button in [mode, continuePlaying, retry, returnToQueue] {
            button.tintColor = UIColor(theme.ink)
            button.backgroundColor = UIColor(theme.chip)
            button.layer.cornerRadius = 12
            button.titleLabel?.font = .systemFont(ofSize: 13)
            button.isEnabled = button === returnToQueue || !changing
        }
        let old = candidates
        candidates = recommendations?.candidates ?? []
        snapshotID = recommendations?.id
        if old.map(\.id) != candidates.map(\.id) || old.map(\.song) != candidates.map(\.song) {
            self.recommendations.reloadData()
        } else {
            for cell in self.recommendations.visibleCells {
                if let path = self.recommendations.indexPath(for: cell) { configure(cell, at: path) }
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

    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { candidates.count }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let cell = tableView.dequeueReusableCell(withIdentifier: MusicQueueSongCell.reuseIdentifier, for: indexPath)
        configure(cell, at: indexPath)
        return cell
    }
    private func configure(_ cell: UITableViewCell, at indexPath: IndexPath) {
        guard candidates.indices.contains(indexPath.row), let cell = cell as? MusicQueueSongCell else { return }
        let candidate = candidates[indexPath.row]
        cell.row.configure(title: candidate.song.title, artist: candidate.song.artist, index: indexPath.row,
                           active: false, favorite: nil, removable: false, reorderable: false, theme: theme)
        cell.accessibilityIdentifier = "music.queue.recommendation." + candidate.id
        cell.accessibilityLabel = [candidate.song.title, candidate.song.artist].filter { !$0.isEmpty }.joined(separator: "，")
        cell.accessibilityTraits = .button
    }
    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: false)
        guard let snapshotID, candidates.indices.contains(indexPath.row) else { return }
        onCandidate?(snapshotID, candidates[indexPath.row].id)
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
