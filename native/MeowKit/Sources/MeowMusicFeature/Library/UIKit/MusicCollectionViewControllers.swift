#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicCollectionViewController: MusicPageViewController,
    UITableViewDelegate, UISearchResultsUpdating
{
    private let model: MusicViewModel
    private let page: MusicCollectionViewModel
    private weak var router: (any MusicUIRouting)?
    private let table = UITableView(frame: .zero, style: .plain)
    private let search = UISearchController(searchResultsController: nil)
    private weak var activeEditor: MusicCollectionEditorViewController?
    private var dataSource: UITableViewDiffableDataSource<Int, MusicReference>!
    private var rows: [MusicReference: (track: MusicRemoteTrack, index: Int)] = [:]
    private var shownReadError: String?

    public init(
        model: MusicViewModel,
        collectionID: String,
        appearance: MusicAppearancePreferencesStore,
        router: any MusicUIRouting
    ) {
        self.model = model
        page = MusicCollectionViewModel(
            model: model,
            collectionID: collectionID,
            currentQueueCollectionID: { model.state.sourceCollectionID }
        )
        self.router = router
        super.init(appearance: appearance)
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.separatorStyle = .none
        table.delegate = self
        table.register(MusicTrackCell.self, forCellReuseIdentifier: MusicTrackCell.reuseIdentifier)
        dataSource = UITableViewDiffableDataSource(tableView: table) { [weak self] view, path, id in
            guard let self, let row = self.rows[id] else { return nil }
            let cell = view.dequeueReusableCell(withIdentifier: MusicTrackCell.reuseIdentifier,
                                                for: path) as! MusicTrackCell
            self.configure(cell, row: row)
            return cell
        }
        table.refreshControl = UIRefreshControl()
        table.refreshControl?.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            let task = self.page.reload(preservingContent: true)
            self.runPageTask {
                await task?.value
                if !Task.isCancelled { self.table.refreshControl?.endRefreshing() }
            }
        }, for: .valueChanged)
        view.addSubview(table)
        table.pinEdges(to: view)

        search.searchResultsUpdater = self
        search.obscuresBackgroundDuringPresentation = false
        search.searchBar.placeholder = "歌单内搜索"
        navigationItem.searchController = search
        navigationItem.hidesSearchBarWhenScrolling = true
        navigationItem.rightBarButtonItems = [
            UIBarButtonItem(image: UIImage(systemName: "ellipsis.circle"), menu: actionsMenu()),
            UIBarButtonItem(
                image: UIImage(systemName: "arrow.clockwise"),
                style: .plain,
                target: self,
                action: #selector(refresh)
            ),
        ]
        observe(page) { [weak self] in self?.render() }
        observe(model.playbackChanges) { [weak self] in self?.updatePlayback() }
        observe(model.libraryChanges) { [weak self] in
            self?.navigationItem.rightBarButtonItems?.first?.menu = self?.actionsMenu()
        }
        render()
    }

    public override func pageDidBecomeActive() {
        page.appear()
        render()
    }
    public override func pageDidBecomeVisible() { renderState() }
    public override func pageDidFinishAppearing() {
        if page.isMissing || page.error != nil || page.actionError != nil { renderState() }
    }

    public override func pageDidBecomeInactive() {
        if let editor = activeEditor,
           navigationController?.viewControllers.contains(where: { $0 === editor }) == true {
            // Detail and editor share one admission until both leave the stack.
            return
        }
        page.disappear()
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        updateVisibleCells(force: true)
        search.searchBar.tintColor = UIColor(theme.accent)
    }

    public func updateSearchResults(for searchController: UISearchController) {
        page.searchQuery = searchController.searchBar.text ?? ""
    }

    private func configure(_ cell: MusicTrackCell, row: (track: MusicRemoteTrack, index: Int)) {
        let track = row.track
        let active = model.current?.track.songId.utf8.elementsEqual(track.songId.utf8) == true
        cell.configure(
            track: track,
            active: active,
            theme: theme,
            index: row.index
        )
        cell.accessibilityTraits = active ? [.button, .selected] : .button
        cell.accessibilityIdentifier = "music.collection.track.\(track.songId)"
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard let track = track(at: indexPath),
              let index = page.filteredTracks.firstIndex(where: { $0.ref == track.ref }) else { return }
        page.playFiltered(
            at: index,
            presentationID: page.presentationID,
            filterID: page.filterID
        )
    }

    public func tableView(
        _ tableView: UITableView,
        trailingSwipeActionsConfigurationForRowAt indexPath: IndexPath
    ) -> UISwipeActionsConfiguration? {
        guard let track = track(at: indexPath) else { return nil }
        let presentationID = page.presentationID
        let favorite = UIContextualAction(
            style: .normal,
            title: page.isFavorite(track.ref) ? "取消收藏" : "收藏"
        ) { [weak self] _, _, finish in
            guard let self else { finish(false); return }
            self.page.toggleFavorite(track.ref, presentationID: presentationID)
            finish(true)
        }
        favorite.backgroundColor = UIColor(theme.accent)
        var actions = [favorite]
        if page.canEdit, let original = page.tracks.firstIndex(where: { $0.ref == track.ref }) {
            let remove = UIContextualAction(style: .destructive, title: "移除") {
                [weak self] _, _, finish in
                guard let self else { finish(false); return }
                self.confirmRemoval(track: track, index: original, presentationID: presentationID)
                finish(true)
            }
            actions.append(remove)
        }
        return UISwipeActionsConfiguration(actions: actions)
    }

    public func tableView(
        _ tableView: UITableView,
        contextMenuConfigurationForRowAt indexPath: IndexPath,
        point: CGPoint
    ) -> UIContextMenuConfiguration? {
        guard let track = track(at: indexPath) else { return nil }
        let presentationID = page.presentationID
        guard let originalIndex = page.tracks.firstIndex(where: { $0.ref == track.ref }) else { return nil }
        return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
            guard let self else { return nil }
            var elements: [UIMenuElement] = [
                UIAction(
                    title: "下一首播放",
                    image: UIImage(systemName: "text.line.first.and.arrowtriangle.forward")
                ) { [weak self] _ in
                    guard let self else { return }
                    self.page.enqueue(at: originalIndex, next: true, presentationID: presentationID)
                },
                UIAction(
                    title: "添加到播放队列",
                    image: UIImage(systemName: "text.badge.plus")
                ) { [weak self] _ in
                    guard let self else { return }
                    self.page.enqueue(at: originalIndex, next: false, presentationID: presentationID)
                },
            ]
            let targets = self.model.collections.filter {
                ["playlist", "favorites"].contains($0.kind) && $0.id != self.page.collectionID
            }
            if !targets.isEmpty {
                elements.append(UIMenu(title: "添加到歌单", children: targets.map { target in
                    UIAction(title: target.title) { [weak self] _ in
                        guard let self else { return }
                        self.page.add(
                            [track.ref],
                            to: target,
                            presentationID: presentationID
                        )
                    }
                }))
            }
            return UIMenu(children: elements)
        }
    }

    private func render() {
        guard dataSource != nil else { return }
        let previous = rows
        let tracks = page.filteredTracks
        rows = Dictionary(uniqueKeysWithValues: tracks.enumerated().map {
            ($0.element.ref, (track: $0.element, index: $0.offset))
        })
        var snapshot = NSDiffableDataSourceSnapshot<Int, MusicReference>()
        snapshot.appendSections([0])
        snapshot.appendItems(tracks.map(\.ref))
        let changed = tracks.map(\.ref).filter {
            guard let old = previous[$0], let row = rows[$0] else { return false }
            return old.track != row.track || old.index != row.index
        }
        snapshot.reconfigureItems(changed)
        if dataSource.snapshot().itemIdentifiers != snapshot.itemIdentifiers || !changed.isEmpty {
            dataSource.apply(snapshot, animatingDifferences: false)
        }
        renderState()
    }

    private func renderState() {
        guard isViewLoaded else { return }
        title = page.collection?.title ?? ""
        navigationItem.rightBarButtonItems?.first?.menu = actionsMenu()
        if page.isMissing {
            if canPresentPageAlert, navigationController?.topViewController === self {
                navigationController?.popViewController(animated: true)
            }
            return
        }
        if page.isLoading, rows.isEmpty {
            table.backgroundView = statusLabel("加载中")
        } else if page.collection != nil, rows.isEmpty {
            table.backgroundView = statusLabel(page.searchQuery.isEmpty ? "暂无歌曲" : "无匹配歌曲")
        } else {
            table.backgroundView = nil
        }
        showError(page.actionError)
        guard let error = page.error else { shownReadError = nil; return }
        let message = error.localizedDescription
        guard canPresentPageAlert, shownReadError != message else { return }
        let presentation = page.presentationID
        let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
            guard let self, self.page.presentationID == presentation else { return }
            self.shownReadError = nil
            self.page.reload(preservingContent: true)
        })
        shownReadError = message
        present(alert, animated: true)
    }

    private func track(at path: IndexPath) -> MusicRemoteTrack? {
        dataSource.itemIdentifier(for: path).flatMap { rows[$0]?.track }
    }

    private func updateVisibleCells(force: Bool = false) {
        guard dataSource != nil else { return }
        for path in table.indexPathsForVisibleRows ?? [] {
            guard let id = dataSource.itemIdentifier(for: path), let row = rows[id],
                  let cell = table.cellForRow(at: path) as? MusicTrackCell else { continue }
            let active = model.current?.track.songId.utf8.elementsEqual(row.track.songId.utf8) == true
            if force || cell.accessibilityTraits.contains(.selected) != active { configure(cell, row: row) }
        }
    }

    private func updatePlayback() {
        updateVisibleCells()
        navigationItem.rightBarButtonItems?.first?.menu = actionsMenu()
    }

    private func actionsMenu() -> UIMenu {
        let presentationID = page.presentationID, filterID = page.filterID
        var actions: [UIMenuElement] = [
            UIAction(
                title: page.isPlayingCollection ? "暂停" : "播放全部",
                image: UIImage(systemName: page.isPlayingCollection ? "pause.fill" : "play.fill")
            ) { [weak self] _ in
                guard let self, !self.page.filteredTracks.isEmpty else { return }
                self.page.playFiltered(
                    presentationID: presentationID,
                    filterID: filterID,
                    toggleCurrent: true
                )
            },
        ]
        if page.canEdit {
            actions.append(UIAction(title: "编辑歌单", image: UIImage(systemName: "list.bullet")) {
                [weak self] _ in self?.openEditor()
            })
        }
        if page.canRename {
            actions.append(UIAction(title: "重命名", image: UIImage(systemName: "pencil")) {
                [weak self] _ in self?.rename()
            })
            actions.append(UIAction(
                title: "删除歌单",
                image: UIImage(systemName: "trash"),
                attributes: .destructive
            ) { [weak self] _ in self?.removeCollection() })
        }
        return UIMenu(children: actions)
    }

    private func openEditor() {
        guard let draft = page.beginEditing(presentationID: page.presentationID) else { return }
        let editor = MusicCollectionEditorViewController(page: page, draft: draft, owner: model,
                                                          origin: self, appearance: appearance)
        activeEditor = editor
        navigationController?.pushViewController(editor, animated: true)
    }

    private func rename() {
        guard let ticket = page.requestRename(presentationID: page.presentationID) else { return }
        let alert = UIAlertController(title: "重命名歌单", message: nil, preferredStyle: .alert)
        alert.addTextField { [weak self] field in field.text = self?.page.collection?.title }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel) { [weak self] _ in
            self?.page.cancelRename(ticket)
        })
        alert.addAction(UIAlertAction(title: "保存", style: .default) { [weak self, weak alert] _ in
            let value = alert?.textFields?.first?.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard let self else { return }
            guard !value.isEmpty else { self.page.cancelRename(ticket); return }
            self.page.rename(value, ticket: ticket)
        })
        present(alert, animated: true)
    }

    private func removeCollection() {
        let presentationID = page.presentationID
        let alert = UIAlertController(title: "删除歌单？", message: page.collection?.title, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "删除", style: .destructive) { [weak self] _ in
            guard let self else { return }
            self.page.removeCollection(presentationID: presentationID)
        })
        present(alert, animated: true)
    }

    private func confirmRemoval(track: MusicRemoteTrack, index: Int, presentationID: UUID) {
        guard page.presentationID == presentationID else { return }
        let alert = UIAlertController(title: "从歌单移除这首歌？", message: track.title, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "移除", style: .destructive) { [weak self] _ in
            guard let self, self.page.tracks.indices.contains(index),
                  self.page.tracks[index].ref == track.ref else { return }
            self.page.deleteMembers(
                at: IndexSet(integer: index),
                presentationID: presentationID
            )
        })
        present(alert, animated: true)
    }

    private func statusLabel(_ text: String) -> UILabel {
        let label = UILabel()
        label.text = text
        label.textAlignment = .center
        label.numberOfLines = 0
        label.textColor = UIColor(theme.muted)
        return label
    }

    @objc private func refresh() {
        page.reload(preservingContent: true)
    }
}

@MainActor
final class MusicCollectionEditorViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    private let page: MusicCollectionViewModel
    private let draft: MusicCollectionEditorDraft
    private let owner: MusicViewModel
    private weak var origin: MusicCollectionViewController?
    private let table = UITableView(frame: .zero, style: .plain)
    private let selectAll = UIButton(type: .system)
    private let enqueue = UIButton(type: .system)
    private let addToCollection = UIButton(type: .system)
    private let deleteSelected = UIButton(type: .system)
    private var backLease: UIKitEditorBackGestureLease?
    private var closing = false
    private var displayedTracks: [MusicRemoteTrack] = []
    private var displayedTrackIndices: [MusicReference: Int] = [:]

    public init(
        page: MusicCollectionViewModel,
        draft: MusicCollectionEditorDraft,
        owner: MusicViewModel,
        origin: MusicCollectionViewController,
        appearance: MusicAppearancePreferencesStore
    ) {
        self.page = page
        self.draft = draft
        self.owner = owner
        self.origin = origin
        super.init(appearance: appearance)
        title = draft.title
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.separatorStyle = .none
        displayedTracks = page.editor === draft ? draft.tracks : []
        displayedTrackIndices = Dictionary(uniqueKeysWithValues: displayedTracks.enumerated().map { ($0.element.ref, $0.offset) })
        table.dataSource = self
        table.delegate = self
        table.register(MusicTrackCell.self, forCellReuseIdentifier: MusicTrackCell.reuseIdentifier)
        table.isEditing = true
        table.allowsSelectionDuringEditing = true
        for (button, title, symbol) in [
            (selectAll, "全选", "checklist"),
            (enqueue, "下一首", "text.line.first.and.arrowtriangle.forward"),
            (addToCollection, "收藏", "folder"),
            (deleteSelected, "删除", "trash"),
        ] {
            var configuration = UIButton.Configuration.plain()
            configuration.title = title
            configuration.image = UIImage(systemName: symbol)
            configuration.imagePlacement = .top
            configuration.imagePadding = 2
            button.configuration = configuration
        }
        selectAll.addAction(UIAction { [weak self] _ in self?.draft.toggleAll() }, for: .touchUpInside)
        enqueue.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.page.enqueueSelected(self.draft)
        }, for: .touchUpInside)
        addToCollection.addAction(UIAction { [weak self] _ in self?.chooseCollection() }, for: .touchUpInside)
        deleteSelected.addAction(UIAction { [weak self] _ in self?.draft.deleteSelected() }, for: .touchUpInside)
        let actions = UIStackView(arrangedSubviews: [
            selectAll, enqueue, addToCollection, deleteSelected,
        ])
        actions.axis = .horizontal
        actions.distribution = .fillEqually
        actions.translatesAutoresizingMaskIntoConstraints = false
        table.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(table)
        view.addSubview(actions)
        NSLayoutConstraint.activate([
            table.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            table.topAnchor.constraint(equalTo: view.topAnchor),
            table.bottomAnchor.constraint(equalTo: actions.topAnchor),
            actions.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 8),
            actions.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -8),
            actions.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
            actions.heightAnchor.constraint(equalToConstant: 58),
        ])

        navigationItem.hidesBackButton = true
        navigationItem.leftBarButtonItem = UIBarButtonItem(
            title: "返回",
            style: .plain,
            target: self,
            action: #selector(requestClose)
        )
        navigationItem.rightBarButtonItem = UIBarButtonItem(
            title: "完成",
            style: .done,
            target: self,
            action: #selector(save)
        )
        observe(draft) { [weak self] in
            self?.renderTracks()
            self?.updateSelection()
            self?.renderActions()
        }
        observe(page) { [weak self] in self?.renderActions() }
        observe(owner.libraryChanges) { [weak self] in self?.renderActions() }
        renderActions()
    }

    public override func pageDidBecomeVisible() {
        if page.editor == nil {
            if canPresentPageAlert { navigationController?.popViewController(animated: false) }
            return
        }
        if let navigationController {
            backLease = UIKitEditorBackGestureLease(navigationController: navigationController,
                hostView: view, requestConfirmation: { [weak self] in self?.requestClose() })
        }
        renderActions()
    }

    public override func pageDidFinishAppearing() {
        if page.editor !== draft || page.error != nil || page.actionError != nil { renderActions() }
    }

    public override func pageDidBecomeInactive() {
        backLease?.invalidate(); backLease = nil
        let stack = navigationController?.viewControllers ?? []
        if !stack.contains(where: { $0 === origin }) {
            page.disappear()
        }
    }

    public override func pageDidBecomeHidden() { backLease?.invalidate(); backLease = nil }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        updateVisibleTracks()
        for button in [selectAll, enqueue, addToCollection, deleteSelected] { button.tintColor = UIColor(theme.ink) }
    }

    private func updateVisibleTracks(in range: ClosedRange<Int>? = nil) {
        for case let cell as MusicTrackCell in table.visibleCells {
            guard let reference = cell.representedReference,
                  let index = displayedTrackIndices[reference],
                  range?.contains(index) != false else { continue }
            cell.configure(track: displayedTracks[index], active: false, theme: theme, index: index)
        }
    }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        displayedTracks.count
    }

    public func tableView(
        _ tableView: UITableView,
        cellForRowAt indexPath: IndexPath
    ) -> UITableViewCell {
        let track = displayedTracks[indexPath.row]
        let cell = tableView.dequeueReusableCell(
            withIdentifier: MusicTrackCell.reuseIdentifier,
            for: indexPath
        ) as! MusicTrackCell
        cell.configure(track: track, active: false, theme: theme, index: indexPath.row)
        cell.accessoryType = draft.selected.contains(track.ref) ? .checkmark : .none
        return cell
    }

    public func tableView(
        _ tableView: UITableView,
        canMoveRowAt indexPath: IndexPath
    ) -> Bool { draft.canEdit }

    public func tableView(
        _ tableView: UITableView,
        moveRowAt sourceIndexPath: IndexPath,
        to destinationIndexPath: IndexPath
    ) {
        guard displayedTracks.indices.contains(sourceIndexPath.row),
              displayedTracks.indices.contains(destinationIndexPath.row) else { return }
        let moved = displayedTracks.remove(at: sourceIndexPath.row)
        displayedTracks.insert(moved, at: destinationIndexPath.row)
        for index in min(sourceIndexPath.row, destinationIndexPath.row)...max(sourceIndexPath.row, destinationIndexPath.row) {
            displayedTrackIndices[displayedTracks[index].ref] = index
        }
        draft.move(
            from: IndexSet(integer: sourceIndexPath.row),
            to: destinationIndexPath.row > sourceIndexPath.row ? destinationIndexPath.row + 1 : destinationIndexPath.row
        )
        renderTracks()
        updateVisibleTracks(in: min(sourceIndexPath.row, destinationIndexPath.row)...max(sourceIndexPath.row, destinationIndexPath.row))
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard displayedTracks.indices.contains(indexPath.row) else { return }
        draft.toggle(displayedTracks[indexPath.row].ref)
        updateSelection()
    }

    public func tableView(
        _ tableView: UITableView,
        editingStyleForRowAt indexPath: IndexPath
    ) -> UITableViewCell.EditingStyle { .delete }

    public func tableView(
        _ tableView: UITableView,
        commit editingStyle: UITableViewCell.EditingStyle,
        forRowAt indexPath: IndexPath
    ) {
        guard editingStyle == .delete else { return }
        guard displayedTracks.indices.contains(indexPath.row), draft.tracks.indices.contains(indexPath.row),
              displayedTracks[indexPath.row].ref == draft.tracks[indexPath.row].ref else { return }
        draft.remove(at: IndexSet(integer: indexPath.row))
        renderTracks()
    }

    @objc private func requestClose() {
        guard !closing else { return }
        if !draft.hasChanges {
            page.discardEditing(draft)
            navigationController?.popViewController(animated: true)
            return
        }
        let alert = UIAlertController(title: "放弃未保存的修改？", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "继续编辑", style: .cancel))
        alert.addAction(UIAlertAction(title: "放弃", style: .destructive) { [weak self] _ in
            guard let self else { return }
            self.closing = true
            self.page.discardEditing(self.draft)
            self.navigationController?.popViewController(animated: true)
        })
        present(alert, animated: true)
    }

    @objc private func save() {
        guard !closing else { return }
        runPageTask { [weak self] in
            guard let self, !Task.isCancelled, self.isPageActive, !self.closing else { return }
            self.closing = true
            defer { self.closing = false }
            guard let task = self.page.saveEditing(self.draft) else {
                if !self.draft.hasChanges, self.navigationController?.topViewController === self {
                    self.navigationController?.popViewController(animated: true)
                }
                return
            }
            await task.value
            guard !Task.isCancelled else { return }
            if self.page.editor == nil, self.navigationController?.topViewController === self {
                self.navigationController?.popViewController(animated: true)
            }
        }
    }

    private func renderActions() {
        if page.editor !== draft {
            title = nil
            renderTracks()
            backLease?.invalidate()
            if canPresentPageAlert, !closing, navigationController?.topViewController === self {
                navigationController?.popViewController(animated: true)
            }
        }
        selectAll.isEnabled = draft.canEdit
        deleteSelected.isEnabled = draft.canEdit && !draft.selected.isEmpty
        enqueue.isEnabled = page.canAct && !draft.selected.isEmpty
        addToCollection.isEnabled = enqueue.isEnabled
        navigationItem.rightBarButtonItem?.isEnabled = page.canAct
        selectAll.configuration?.title = draft.allSelected ? "取消全选" : "全选"
        if draft.hasChanges, page.editor === draft {
            if isPageVisible { backLease?.activate() }
            navigationItem.hidesBackButton = true
            navigationItem.leftBarButtonItem = UIBarButtonItem(title: "返回", style: .plain,
                target: self, action: #selector(requestClose))
        } else {
            backLease?.invalidate()
            navigationItem.hidesBackButton = false
            navigationItem.leftBarButtonItem = nil
        }
        _ = showError(page.actionError ?? page.error?.localizedDescription)
    }

    private func renderTracks() {
        let next = page.editor === draft ? draft.tracks : []
        let difference = next.map(\.ref).difference(from: displayedTracks.map(\.ref)).inferringMoves()
        displayedTracks = next
        guard !difference.isEmpty else { return }
        displayedTrackIndices = Dictionary(uniqueKeysWithValues: displayedTracks.enumerated().map { ($0.element.ref, $0.offset) })
        UIView.performWithoutAnimation {
            table.performBatchUpdates {
                for change in difference {
                    switch change {
                    case .remove(let offset, _, let destination):
                        if let destination {
                            table.moveRow(at: IndexPath(row: offset, section: 0),
                                          to: IndexPath(row: destination, section: 0))
                        } else { table.deleteRows(at: [IndexPath(row: offset, section: 0)], with: .none) }
                    case .insert(let offset, _, let source):
                        if source == nil { table.insertRows(at: [IndexPath(row: offset, section: 0)], with: .none) }
                    }
                }
            }
        }
        applyTheme()
        updateSelection()
    }

    private func updateSelection() {
        for case let cell as MusicTrackCell in table.visibleCells {
            guard let reference = cell.representedReference else { continue }
            cell.accessoryType = draft.selected.contains(reference) ? .checkmark : .none
        }
    }

    private func chooseCollection() {
        guard !draft.selectedTracks.isEmpty else { return }
        let alert = UIAlertController(title: "收藏到歌单", message: nil, preferredStyle: .actionSheet)
        for target in pageCollectionTargets {
            alert.addAction(UIAlertAction(title: target.title, style: .default) { [weak self] _ in
                guard let self else { return }
                self.page.addSelected(self.draft, to: target)
            })
        }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        present(alert, animated: true)
    }

    private var pageCollectionTargets: [MusicCollection] {
        owner.collections.filter { ["playlist", "favorites"].contains($0.kind) }
    }

}

#endif
