#if os(iOS)
import UIKit
import MeowUI

/// Archived rows are display-only. Resume/delete use the service-issued
/// detail token, never a fabricated per-row playback reference.
@MainActor
final class MusicHistoryCardViewController: MusicPageViewController,
    UITableViewDelegate
{
    var isSelectedPage: (() -> Bool)?
    private let model: MusicViewModel
    private var summary: MusicPlayedPlaylist
    private let table = UITableView(frame: .zero, style: .plain)
    private let heading = UILabel(), source = UILabel()
    private let resume = UIButton(type: .system), remove = UIButton(type: .system)
    private let retry = UIButton(type: .system)
    private var detail: MusicHistoryDetail?
    private var displayed: MusicReferenceHistoryDisplayIdentity?
    private var command = UUID(), readRequest = UUID()
    private var reading = false
    private var needsRead = true
    private var error: String?
    private var dataSource: UITableViewDiffableDataSource<Int, String>!
    private var rows: [String: MusicHistoryDisplayEntry] = [:]
    private var readTask: Task<Void, Never>?
    private var shownReadError: String?
    private var missing = false

    init(model: MusicViewModel, summary: MusicPlayedPlaylist, appearance: MusicAppearancePreferencesStore) {
        self.model = model; self.summary = summary
        super.init(appearance: appearance)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.layer.cornerRadius = 20
        table.backgroundColor = .clear
        table.separatorStyle = .none
        table.delegate = self
        table.register(UITableViewCell.self, forCellReuseIdentifier: "History")
        dataSource = UITableViewDiffableDataSource(tableView: table) { [weak self] view, path, id in
            guard let self, let row = self.rows[id] else { return nil }
            let cell = view.dequeueReusableCell(withIdentifier: "History", for: path)
            self.configure(cell, row: row)
            return cell
        }
        heading.font = .systemFont(ofSize: 15, weight: .semibold)
        source.font = .systemFont(ofSize: 11)
        remove.setImage(PageIcon.image(.remove), for: .normal)
        remove.accessibilityLabel = "删除这份已播歌单"
        resume.addAction(UIAction { [weak self] _ in self?.continuePlaying() }, for: .touchUpInside)
        remove.addAction(UIAction { [weak self] _ in self?.confirmRemoval() }, for: .touchUpInside)
        retry.setTitle("重试", for: .normal)
        retry.addAction(UIAction { [weak self] _ in self?.read(force: true) }, for: .touchUpInside)
        for item in [table, source, heading, retry, resume, remove] { view.addSubview(item) }
        observe(model.playbackChanges) { [weak self] in self?.updateVisiblePlayback() }
        observe(model.historyChanges) { [weak self] in self?.reconcileSummary() }
        observe(model.accessChanges) { [weak self] in self?.reconcileSummary() }
        renderRows(); renderState()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let width = view.bounds.width
        source.frame = CGRect(x: 16, y: 12, width: width - 80, height: 18)
        heading.frame = CGRect(x: 16, y: 32, width: width - 80, height: 24)
        remove.frame = CGRect(x: width - 60, y: 16, width: 44, height: 44)
        resume.frame = CGRect(x: 16, y: 64, width: width - 32, height: 44)
        let height: CGFloat = error == nil ? 0 : 60
        retry.frame = CGRect(x: width - 92, y: 116, width: 76, height: 44)
        table.frame = CGRect(x: 0, y: 112 + height, width: width,
                             height: max(0, view.bounds.height - 112 - height))
    }

    override func pageDidBecomeActive() { reconcileSummary() }
    override func pageDidBecomeVisible() { renderState() }
    override func pageDidFinishAppearing() {
        if error != nil { renderState() }
    }

    override func pageDidBecomeInactive() {
        invalidateDetail()
        needsRead = false
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        updateVisiblePlayback(force: true)
        renderState()
    }

    func update(summary: MusicPlayedPlaylist) {
        let previous = identity
        self.summary = summary
        if previous != identity || displayed != nil && displayed != identity {
            invalidateDetail()
            needsRead = true
        }
        if isPageActive, needsRead { read() }
        if isViewLoaded { renderState() }
    }

    private func reconcileSummary() {
        guard model.isAvailable, !model.isClosed,
              let summary = model.playedPlaylists.first(where: { $0.id == self.summary.id }) else {
            invalidateDetail()
            missing = true; needsRead = false
            renderRows(); renderState()
            return
        }
        if missing { missing = false; needsRead = true }
        update(summary: summary)
    }

    private func configure(_ cell: UITableViewCell, row: MusicHistoryDisplayEntry) {
        let active = row.displayIdentity?.matches(model.current?.displayIdentity) == true
        var content = cell.defaultContentConfiguration()
        content.text = "\(row.index + 1). \(row.title)"
        content.secondaryText = row.artist
        content.textProperties.color = UIColor(active ? theme.accent : theme.ink)
        content.secondaryTextProperties.color = UIColor(theme.muted)
        cell.contentConfiguration = content
        cell.backgroundColor = active ? UIColor(theme.accent).withAlphaComponent(0.06) : .clear
        cell.selectionStyle = .none
        cell.accessibilityTraits = active ? [.staticText, .selected] : .staticText
    }

    private var identity: MusicReferenceHistoryDisplayIdentity {
        .init(summary, access: model.accountAccessGeneration)
    }

    private var permitted: Bool {
        isPageActive && isSelectedPage?() == true && model.isAvailable && !model.isClosed &&
            displayed == identity && detail != nil && !reading && !missing
    }

    private func read(force: Bool = false) {
        guard isPageActive, model.isAvailable, !model.isClosed, !missing, !reading else { return }
        guard force || error == nil && (detail == nil || displayed != identity) else { return }
        let own = UUID(), expected = identity
        reading = true; needsRead = false; readRequest = own; command = UUID()
        error = nil; shownReadError = nil
        renderState()
        let model = model
        readTask = Task { [weak self, model] in
            defer {
                if let self, self.readRequest == own {
                    self.reading = false; self.readTask = nil
                    self.renderState()
                }
            }
            do {
                let value = try await model.historyDetail(id: expected.id)
                guard !Task.isCancelled, let self, self.readRequest == own, self.isPageActive, !self.missing,
                      self.identity == expected else { return }
                self.detail = value; self.displayed = expected
                self.renderRows()
            } catch {
                if !Task.isCancelled, let self, self.readRequest == own, self.isPageActive, self.identity == expected {
                    self.error = MusicServiceError.sanitized(error).localizedDescription
                }
            }
        }
    }

    private func invalidateDetail() {
        command = UUID(); readRequest = UUID()
        readTask?.cancel(); readTask = nil; reading = false
        detail = nil; displayed = nil; error = nil; shownReadError = nil
        if isViewLoaded { renderRows() }
    }

    private func intent(_ detail: MusicHistoryDetail) -> MusicReferenceHistoryIntent {
        .init(access: model.accountAccessGeneration, request: command, token: detail.actionToken)
    }

    private func admits(_ intent: MusicReferenceHistoryIntent) -> Bool {
        intent.admits(access: model.accountAccessGeneration, request: command,
                      token: detail?.actionToken, permitted: permitted)
    }

    private func continuePlaying() {
        guard permitted, let detail else { return }
        let captured = intent(detail)
        runPageTask { [weak self] in
            guard let self, self.admits(captured) else { return }
            await self.model.restoreHistory(detail: detail)
        }
    }

    private func confirmRemoval() {
        guard permitted, let detail else { return }
        let captured = intent(detail)
        let alert = UIAlertController(title: "删除这份已播歌单？", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "确认删除", style: .destructive) { [weak self] _ in
            self?.runPageTask {
                guard let self, self.admits(captured) else { return }
                await self.model.removeHistory(detail: detail)
            }
        })
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        present(alert, animated: true)
    }

    private func renderRows() {
        guard dataSource != nil else { return }
        let previous = rows
        let entries = detail?.entries ?? []
        rows = Dictionary(uniqueKeysWithValues: entries.map { ($0.id, $0) })
        var snapshot = NSDiffableDataSourceSnapshot<Int, String>()
        snapshot.appendSections([0]); snapshot.appendItems(entries.map(\.id))
        let changed = snapshot.itemIdentifiers.filter { previous[$0] != nil && previous[$0] != rows[$0] }
        snapshot.reconfigureItems(changed)
        if dataSource.snapshot().itemIdentifiers != snapshot.itemIdentifiers || !changed.isEmpty {
            dataSource.apply(snapshot, animatingDifferences: false)
        }
    }

    private func updateVisiblePlayback(force: Bool = false) {
        guard dataSource != nil else { return }
        for path in table.indexPathsForVisibleRows ?? [] {
            guard let id = dataSource.itemIdentifier(for: path), let row = rows[id],
                  let cell = table.cellForRow(at: path) else { continue }
            let active = row.displayIdentity?.matches(model.current?.displayIdentity) == true
            if force || cell.accessibilityTraits.contains(.selected) != active { configure(cell, row: row) }
        }
    }

    private func renderState() {
        guard isViewLoaded else { return }
        heading.text = missing ? nil : "\(detail?.summary.title ?? summary.title) · \(detail?.entries.count ?? summary.count) 首"
        source.text = missing ? nil : detail?.sourceLabel ?? "已播歌单"
        let selected = detail?.selectedIndex.flatMap { index in detail?.entries.first { $0.index == index }?.title }
        resume.setTitle("继续播放：" + (selected ?? "播放列表"), for: .normal)
        heading.textColor = UIColor(theme.ink)
        source.textColor = UIColor(theme.muted)
        for button in [resume, remove, retry] { button.tintColor = UIColor(theme.ink) }
        retry.isHidden = error == nil
        resume.isEnabled = detail != nil && !reading && !missing && model.isAvailable && !model.isClosed
        remove.isEnabled = resume.isEnabled
        let label = UILabel()
        label.text = reading && detail == nil ? "正在读取已播歌单" : nil
        label.textAlignment = .center; label.textColor = UIColor(theme.muted)
        table.backgroundView = label.text == nil ? nil : label
        view.setNeedsLayout()
        guard canPresentPageAlert, let error, shownReadError != error else { return }
        let expected = identity
        let alert = UIAlertController(title: "错误", message: error, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
            guard let self, self.identity == expected else { return }
            self.read(force: true)
        })
        shownReadError = error
        present(alert, animated: true)
    }

    isolated deinit { readTask?.cancel() }
}
#endif
