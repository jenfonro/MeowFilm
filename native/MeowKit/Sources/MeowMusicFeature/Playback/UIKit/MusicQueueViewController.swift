#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicQueueViewController: MusicPageViewController,
    UIPageViewControllerDataSource, UIPageViewControllerDelegate,
    UITableViewDataSource, UITableViewDelegate
{
    public var onDismiss: (() -> Void)?
    private let model: MusicViewModel
    private let favorites: MusicMiniPlayerFavorites
    private let categories = UISegmentedControl(items: MusicReferenceQueuePage.allCases.map(\.title))
    private let pager = UIPageViewController(transitionStyle: .scroll, navigationOrientation: .horizontal)
    private let current = MusicQueueTablePage()
    private let songs = MusicQueueTablePage()
    private var histories: [String: MusicHistoryCardViewController] = [:]
    private var selected = "current"
    private var paging = false
    private var pendingSelection: String?
    private var entries: [MusicQueueEntry] = []
    private var played: [[MusicPlayedSong]] = []
    private var displayed: MusicReferenceQueueIntent?
    private var similar: MusicSimilarModeSnapshot?
    private var ids: [String] { MusicReferenceQueueStrip.ids(history: model.playedPlaylists.map(\.id)) }

    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        favorites = MusicMiniPlayerFavorites(model: model)
        super.init(appearance: appearance)
        title = "播放队列"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        categories.selectedSegmentIndex = 0
        categories.addAction(UIAction { [weak self] _ in
            guard let self, let category = MusicReferenceQueuePage(rawValue: self.categories.selectedSegmentIndex)
            else { return }
            self.select(MusicReferenceQueueStrip.tab(category, selected: self.selected,
                                                     history: self.model.playedPlaylists.map(\.id)))
        }, for: .valueChanged)
        view.addSubview(categories)
        categories.translatesAutoresizingMaskIntoConstraints = false
        addChild(pager)
        pager.dataSource = self
        pager.delegate = self
        view.addSubview(pager.view)
        pager.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            categories.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 12),
            categories.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -12),
            categories.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8),
            categories.heightAnchor.constraint(equalToConstant: 36),
            pager.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            pager.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            pager.view.topAnchor.constraint(equalTo: categories.bottomAnchor, constant: 8),
            pager.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        pager.didMove(toParent: self)
        for (index, page) in [current, songs].enumerated() {
            page.table.tag = index
            page.table.dataSource = self
            page.table.delegate = self
            page.table.register(MusicTrackCell.self, forCellReuseIdentifier: MusicTrackCell.reuseIdentifier)
            page.table.register(UITableViewCell.self, forCellReuseIdentifier: "Status")
        }
        current.candidates.tag = 2
        current.candidates.dataSource = self
        current.candidates.delegate = self
        current.candidates.register(UITableViewCell.self, forCellReuseIdentifier: "Candidate")
        current.mode.addAction(UIAction { [weak self] _ in self?.cycleMode() }, for: .touchUpInside)
        current.continuePlaying.addAction(UIAction { [weak self] _ in
            guard let self, let snapshot = self.similar else { return }
            self.runSimilar(snapshot) {
                await self.model.setSimilarContinue(!snapshot.continueEnabled, snapshotID: snapshot.id)
            }
        }, for: .touchUpInside)
        current.retry.addAction(UIAction { [weak self] _ in
            guard let self, let snapshot = self.similar, snapshot.canRetry else { return }
            self.runSimilar(snapshot) { await self.model.retrySimilarMode(snapshotID: snapshot.id) }
        }, for: .touchUpInside)
        navigationItem.rightBarButtonItem = UIBarButtonItem(
            title: "清空", style: .plain, target: self, action: #selector(clearQueue))
        navigationItem.leftBarButtonItem = editButtonItem
        observe(model) { [weak self] in self?.render() }
        observe(favorites) { [weak self] in self?.render() }
        pager.setViewControllers([current], direction: .forward, animated: false)
    }

    public override func pageDidBecomeActive() {
        favorites.setActive(true)
        render()
        runPageTask { [weak self] in
            guard let self else { return }
            await self.model.loadHistory()
            if !Task.isCancelled { await self.model.loadSimilarMode(preview: true) }
        }
    }

    public override func pageDidBecomeInactive() { favorites.suspend() }

    public override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        if navigationController?.isBeingDismissed == true { onDismiss?() }
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        categories.setTitleTextAttributes([.foregroundColor: UIColor(theme.ink)], for: .normal)
        render()
    }

    public func numberOfSections(in tableView: UITableView) -> Int {
        tableView.tag == 1 ? played.count : 1
    }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        if tableView.tag == 0 { return entries.count }
        if tableView.tag == 1 { return played[section].count }
        return similar?.active == false ? similar?.candidates.count ?? 0 : 0
    }

    public func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        tableView.tag == 1 ? played[section].first?.date : nil
    }

    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        if tableView.tag == 0 || tableView.tag == 1 {
            let track = tableView.tag == 0 ? entries[indexPath.row].track : played[indexPath.section][indexPath.row].track
            let active = tableView.tag == 0 ? entries[indexPath.row].id == model.state.currentId :
                played[indexPath.section][indexPath.row].displayIdentity?.matches(model.current?.displayIdentity) == true
            let cell = tableView.dequeueReusableCell(
                withIdentifier: MusicTrackCell.reuseIdentifier, for: indexPath) as! MusicTrackCell
            cell.configure(track: track, active: active, theme: theme, index: indexPath.row)
            cell.accessoryType = favorites.isFavorite(track) ? .checkmark : .none
            cell.accessibilityTraits = active ? [.button, .selected] : .button
            return cell
        }
        let row = similar!.candidates[indexPath.row]
        let cell = tableView.dequeueReusableCell(withIdentifier: "Candidate", for: indexPath)
        var content = cell.defaultContentConfiguration()
        content.text = "\(indexPath.row + 1). \(row.song.title)"
        content.secondaryText = row.song.artist
        content.textProperties.color = UIColor(theme.ink)
        content.secondaryTextProperties.color = UIColor(theme.muted)
        cell.contentConfiguration = content
        cell.backgroundColor = .clear
        return cell
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        if tableView.tag == 0, let intent = displayed {
            let id = entries[indexPath.row].id
            runQueue(.select(id), intent: intent, startPlayback: true)
        } else if tableView.tag == 1 {
            let row = played[indexPath.section][indexPath.row], access = model.accountAccessGeneration
            runPageTask { [weak self] in
                guard let self, self.selected == "songs", self.model.accountAccessGeneration == access else { return }
                await self.model.playHistorySong(row)
            }
        } else if let snapshot = similar {
            let id = snapshot.candidates[indexPath.row].id
            runSimilar(snapshot) { await self.model.playSimilarCandidate(snapshotID: snapshot.id, candidateID: id) }
        }
    }

    public func tableView(_ tableView: UITableView,
                          trailingSwipeActionsConfigurationForRowAt indexPath: IndexPath)
        -> UISwipeActionsConfiguration? {
        guard tableView.tag != 2 else { return nil }
        let track = tableView.tag == 0 ? entries[indexPath.row].track : played[indexPath.section][indexPath.row].track
        let like = UIContextualAction(style: .normal, title: favorites.isFavorite(track) ? "取消收藏" : "收藏") {
            [weak self] _, _, finish in
            self?.favorites.toggle(track)
            finish(true)
        }
        var actions = [like]
        if tableView.tag == 0, let intent = displayed {
            let id = entries[indexPath.row].id
            actions.insert(UIContextualAction(style: .destructive, title: "移除") { [weak self] _, _, finish in
                self?.runQueue(.remove(id), intent: intent)
                finish(true)
            }, at: 0)
        }
        return UISwipeActionsConfiguration(actions: actions)
    }

    public func tableView(_ tableView: UITableView, canMoveRowAt indexPath: IndexPath) -> Bool {
        tableView.tag == 0
    }

    public func tableView(_ tableView: UITableView, editingStyleForRowAt indexPath: IndexPath)
        -> UITableViewCell.EditingStyle { .none }

    public func tableView(_ tableView: UITableView, shouldIndentWhileEditingRowAt indexPath: IndexPath) -> Bool {
        false
    }

    public override func setEditing(_ editing: Bool, animated: Bool) {
        super.setEditing(editing, animated: animated)
        current.table.setEditing(editing, animated: animated)
        if !editing { render(forceQueue: true) }
    }

    public func tableView(_ tableView: UITableView, moveRowAt from: IndexPath, to: IndexPath) {
        guard let intent = displayed, intent.ids.indices.contains(from.row),
              intent.ids.indices.contains(to.row) else { render(forceQueue: true); return }
        let destination = to.row > from.row ? to.row + 1 : to.row
        guard let order = intent.moving(IndexSet(integer: from.row), to: destination) else { return }
        runQueue(.reorder(order), intent: intent)
    }

    public func pageViewController(_ pageViewController: UIPageViewController,
                                   viewControllerBefore page: UIViewController) -> UIViewController? {
        neighbor(page, step: -1)
    }

    public func pageViewController(_ pageViewController: UIPageViewController,
                                   viewControllerAfter page: UIViewController) -> UIViewController? {
        neighbor(page, step: 1)
    }

    public func pageViewController(_ pageViewController: UIPageViewController,
                                   willTransitionTo pendingViewControllers: [UIViewController]) { paging = true }

    public func pageViewController(_ pageViewController: UIPageViewController, didFinishAnimating finished: Bool,
                                   previousViewControllers: [UIViewController], transitionCompleted completed: Bool) {
        paging = false
        if completed, let shown = pager.viewControllers?.first, let id = id(of: shown) { selected = id }
        render()
        if let pendingSelection { self.pendingSelection = nil; select(pendingSelection) }
    }

    private func render(forceQueue: Bool = false) {
        guard isViewLoaded else { return }
        favorites.setActive(isPageActive && model.isAvailable && !model.isClosed)
        if forceQueue || !current.table.isEditing {
            entries = model.queue
            displayed = .init(access: model.accountAccessGeneration, revision: model.state.revision,
                              ids: entries.map(\.id))
        }
        var dates: [String] = [], groups: [String: [MusicPlayedSong]] = [:]
        for row in model.playedSongs {
            if groups[row.date] == nil { dates.append(row.date) }
            groups[row.date, default: []].append(row)
        }
        played = dates.map { groups[$0]! }
        similar = model.similarMode
        current.configure(snapshot: similar, queueCount: entries.count, modeTitle: model.state.mode.title,
                          notice: model.queueNotice, theme: theme)
        current.table.reloadData()
        current.candidates.reloadData()
        songs.table.reloadData()
        songs.setEmpty(played.isEmpty ? "还没有播放记录" : nil, theme: theme)
        if !paging {
            let next = MusicReferenceQueueStrip.reconciled(selected, history: model.playedPlaylists.map(\.id))
            if next != selected { select(next, animated: false) }
        }
        categories.selectedSegmentIndex = MusicReferenceQueueStrip.category(selected).rawValue
        navigationItem.rightBarButtonItem = selected == "current" ? UIBarButtonItem(
            title: "清空", style: .plain, target: self, action: #selector(clearQueue)) : nil
        navigationItem.leftBarButtonItem = selected == "current" ? editButtonItem : nil
        let live = Set(model.playedPlaylists.map(\.id))
        histories = histories.filter { live.contains($0.key) }
        for summary in model.playedPlaylists { histories[summary.id]?.update(summary: summary) }
    }

    @objc private func clearQueue() {
        guard let intent = displayed, !intent.ids.isEmpty else { return }
        let alert = UIAlertController(title: "停止播放并清空队列？", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "清空", style: .destructive) { [weak self] _ in
            self?.runQueue(.clear, intent: intent)
        })
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        present(alert, animated: true)
    }

    private func runQueue(_ change: MusicQueueChange, intent: MusicReferenceQueueIntent, startPlayback: Bool = false) {
        runPageTask { [weak self] in
            guard let self, self.selected == "current" else { return }
            await self.model.changeDisplayedQueue(change, intent: intent, startPlayback: startPlayback)
            if !Task.isCancelled { self.render(forceQueue: true) }
        }
    }

    private func runSimilar(_ snapshot: MusicSimilarModeSnapshot, operation: @escaping @MainActor () async -> Void) {
        guard let intent = displayed else { return }
        runPageTask { [weak self] in
            guard let self, self.selected == "current", self.model.similarMode?.id == snapshot.id,
                  !self.model.isChangingSimilarMode,
                  intent.admits(access: self.model.accountAccessGeneration, revision: self.model.state.revision,
                                ids: self.model.queue.map(\.id), permitted: self.isPageActive) else { return }
            await operation()
        }
    }

    private func cycleMode() {
        guard let snapshot = similar, !model.isFMActive,
              let index = MusicPlaybackMode.allCases.firstIndex(of: model.state.mode) else { return }
        let mode = MusicPlaybackMode.allCases[(index + 1) % MusicPlaybackMode.allCases.count]
        let revision = model.state.settingsRevision
        runSimilar(snapshot) {
            if self.model.state.settingsRevision == revision { await self.model.setMode(mode) }
        }
    }

    private func select(_ id: String, animated: Bool = true) {
        if paging { pendingSelection = id; return }
        guard let page = page(id) else { return }
        let forward = (ids.firstIndex(of: id) ?? 0) >= (ids.firstIndex(of: selected) ?? 0)
        selected = id
        paging = animated
        pager.setViewControllers([page], direction: forward ? .forward : .reverse, animated: animated) {
            [weak self] _ in
            guard let self else { return }
            self.paging = false
            self.render()
            if let pending = self.pendingSelection { self.pendingSelection = nil; self.select(pending) }
        }
        categories.selectedSegmentIndex = MusicReferenceQueueStrip.category(id).rawValue
    }

    private func page(_ id: String) -> UIViewController? {
        if id == "current" { return current }
        if id == "songs" { return songs }
        guard let summary = model.playedPlaylists.first(where: { "history:" + $0.id == id }) else {
            if id == "playlists" {
                let empty = MusicQueueTablePage()
                empty.setEmpty("还没有已播歌单", theme: theme)
                return empty
            }
            return nil
        }
        if let existing = histories[summary.id] { return existing }
        let card = MusicHistoryCardViewController(model: model, summary: summary, appearance: appearance)
        card.isSelectedPage = { [weak self] in self?.selected == id }
        histories[summary.id] = card
        return card
    }

    private func id(of page: UIViewController) -> String? {
        if page === current { return "current" }
        if page === songs { return "songs" }
        return histories.first { $0.value === page }.map { "history:" + $0.key } ?? "playlists"
    }

    private func neighbor(_ page: UIViewController, step: Int) -> UIViewController? {
        guard let id = id(of: page), let index = ids.firstIndex(of: id), ids.indices.contains(index + step)
        else { return nil }
        return self.page(ids[index + step])
    }
}

@MainActor
private final class MusicQueueTablePage: UIViewController {
    let table = UITableView(frame: .zero, style: .plain)
    let candidates = UITableView(frame: .zero, style: .plain)
    let mode = UIButton(type: .system), continuePlaying = UIButton(type: .system)
    let retry = UIButton(type: .system)
    private let heading = UILabel(), status = UILabel()
    private var snapshot: MusicSimilarModeSnapshot?
    private var queueCount = 0

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear
        for item in [table, candidates, mode, continuePlaying, retry, heading, status] { view.addSubview(item) }
        for list in [table, candidates] { list.backgroundColor = .clear; list.separatorStyle = .none; list.rowHeight = 64 }
        heading.font = .systemFont(ofSize: 12)
        status.font = .systemFont(ofSize: 11)
        status.numberOfLines = 0
        retry.setTitle("重试", for: .normal)
        configure(snapshot: nil, queueCount: 0, modeTitle: "", notice: nil, theme: PageTheme())
    }

    func configure(snapshot: MusicSimilarModeSnapshot?, queueCount: Int, modeTitle: String,
                   notice: String?, theme: PageTheme) {
        loadViewIfNeeded()
        self.snapshot = snapshot; self.queueCount = queueCount
        let presentation = snapshot.map { MusicReferenceSimilarPresentation(snapshot: $0) }
        heading.text = presentation?.showsSection == true ? presentation?.title : nil
        status.text = presentation?.status ?? notice
        retry.isHidden = presentation?.showsRetry != true
        mode.isHidden = snapshot?.active != true
        continuePlaying.isHidden = mode.isHidden
        mode.setTitle(modeTitle, for: .normal)
        continuePlaying.setTitle(snapshot?.continueEnabled == true ? "续播已开启" : "续播已关闭", for: .normal)
        for button in [mode, continuePlaying, retry] { button.tintColor = UIColor(theme.ink) }
        heading.textColor = UIColor(theme.muted); status.textColor = UIColor(theme.muted)
        setEmpty(queueCount == 0 ? "没有正在播放歌曲" : nil, theme: theme)
        view.setNeedsLayout()
    }

    func setEmpty(_ text: String?, theme: PageTheme) {
        loadViewIfNeeded()
        let label = UILabel()
        label.text = text; label.textColor = UIColor(theme.muted); label.textAlignment = .center
        table.backgroundView = text == nil ? nil : label
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let width = view.bounds.width, height = view.bounds.height
        let hasCandidates = snapshot?.active == false && snapshot?.candidates.isEmpty == false
        let footer: CGFloat = snapshot?.active == true ? 52 : 0
        let information: CGFloat = heading.text == nil && status.text == nil ? 0 : 76
        let available = max(0, height - footer - information)
        let queueHeight = hasCandidates ? CGFloat(MusicReferenceQueueMetrics.previewQueueHeight(
            count: queueCount, availableHeight: Double(available))) : available
        table.frame = CGRect(x: 0, y: 0, width: width, height: queueHeight)
        heading.frame = CGRect(x: 16, y: queueHeight + 8, width: width - 32, height: 22)
        status.frame = CGRect(x: 16, y: queueHeight + 30, width: width - (retry.isHidden ? 32 : 108), height: 42)
        retry.frame = CGRect(x: width - 92, y: queueHeight + 28, width: 76, height: 44)
        candidates.frame = CGRect(x: 0, y: queueHeight + information, width: width,
                                  height: max(0, available - queueHeight))
        mode.frame = CGRect(x: 12, y: height - footer, width: (width - 32) / 2, height: 44)
        continuePlaying.frame = mode.frame.offsetBy(dx: (width - 8) / 2, dy: 0)
    }
}

#endif
