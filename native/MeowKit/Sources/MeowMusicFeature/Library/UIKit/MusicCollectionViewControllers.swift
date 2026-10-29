#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicCollectionViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate, UISearchResultsUpdating
{
    private let model: MusicViewModel
    private let page: MusicCollectionViewModel
    private weak var router: (any MusicUIRouting)?
    private let table = UITableView(frame: .zero, style: .plain)
    private let search = UISearchController(searchResultsController: nil)
    private weak var activeEditor: MusicCollectionEditorViewController?

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
        table.dataSource = self
        table.delegate = self
        table.register(MusicTrackCell.self, forCellReuseIdentifier: MusicTrackCell.reuseIdentifier)
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
        observe(model) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeActive() {
        page.appear()
        render()
    }

    public override func pageDidBecomeInactive() {
        if let editor = activeEditor,
           navigationController?.viewControllers.contains(where: { $0 === editor }) == true {
            // The editor now owns this same detail/draft scope; only UIKit
            // subscriptions of the covered detail page have been stopped.
            return
        }
        page.disappear()
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        table.reloadData()
        search.searchBar.tintColor = UIColor(theme.accent)
    }

    public func updateSearchResults(for searchController: UISearchController) {
        page.searchQuery = searchController.searchBar.text ?? ""
    }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        page.filteredTracks.count
    }

    public func tableView(
        _ tableView: UITableView,
        cellForRowAt indexPath: IndexPath
    ) -> UITableViewCell {
        let track = page.filteredTracks[indexPath.row]
        let cell = tableView.dequeueReusableCell(
            withIdentifier: MusicTrackCell.reuseIdentifier,
            for: indexPath
        ) as! MusicTrackCell
        cell.configure(
            track: track,
            active: model.current?.track.songId == track.songId,
            theme: theme,
            index: indexPath.row
        )
        cell.accessibilityIdentifier = "music.collection.track.\(track.songId)"
        return cell
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        page.playFiltered(
            at: indexPath.row,
            presentationID: page.presentationID,
            filterID: page.filterID
        )
    }

    public func tableView(
        _ tableView: UITableView,
        trailingSwipeActionsConfigurationForRowAt indexPath: IndexPath
    ) -> UISwipeActionsConfiguration? {
        let track = page.filteredTracks[indexPath.row]
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
                self.confirmRemoval(track: track, index: original)
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
        let track = page.filteredTracks[indexPath.row]
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
        guard isViewLoaded else { return }
        title = page.collection?.title ?? ""
        table.reloadData()
        navigationItem.rightBarButtonItems?.first?.menu = actionsMenu()
        if page.isMissing {
            navigationController?.popViewController(animated: true)
            return
        }
        if let error = page.error {
            table.backgroundView = statusLabel(error.localizedDescription)
        } else if page.collection != nil, page.filteredTracks.isEmpty {
            table.backgroundView = statusLabel(page.searchQuery.isEmpty ? "暂无歌曲" : "无匹配歌曲")
        } else {
            table.backgroundView = nil
        }
        showError(page.actionError)
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

    private func confirmRemoval(track: MusicRemoteTrack, index: Int) {
        let presentationID = page.presentationID
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
private final class MusicCollectionEditorViewController: MusicPageViewController,
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
            self?.table.reloadData()
            self?.renderActions()
        }
        observe(page) { [weak self] in self?.renderActions() }
        renderActions()
    }

    public override func pageDidBecomeActive() {
        if page.editor == nil {
            navigationController?.popViewController(animated: false)
            return
        }
        if let navigationController {
            backLease = UIKitEditorBackGestureLease(navigationController: navigationController,
                hostView: view, requestConfirmation: { [weak self] in self?.requestClose() })
        }
        renderActions()
    }

    public override func pageDidBecomeInactive() {
        backLease?.invalidate(); backLease = nil
        let stack = navigationController?.viewControllers ?? []
        if stack.contains(where: { $0 === self }) {
            page.pauseCommands()
        } else if !stack.contains(where: { $0 === origin }) {
            page.disappear()
        }
    }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        draft.tracks.count
    }

    public func tableView(
        _ tableView: UITableView,
        cellForRowAt indexPath: IndexPath
    ) -> UITableViewCell {
        let track = draft.tracks[indexPath.row]
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
    ) -> Bool { true }

    public func tableView(
        _ tableView: UITableView,
        moveRowAt sourceIndexPath: IndexPath,
        to destinationIndexPath: IndexPath
    ) {
        draft.move(
            from: IndexSet(integer: sourceIndexPath.row),
            to: destinationIndexPath.row > sourceIndexPath.row ? destinationIndexPath.row + 1 : destinationIndexPath.row
        )
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        draft.toggle(draft.tracks[indexPath.row].ref)
        table.reloadRows(at: [indexPath], with: .none)
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
        draft.remove(at: IndexSet(integer: indexPath.row))
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
        selectAll.configuration?.title = draft.allSelected ? "取消全选" : "全选"
        if draft.hasChanges {
            backLease?.activate()
            navigationItem.hidesBackButton = true
            navigationItem.leftBarButtonItem = UIBarButtonItem(title: "返回", style: .plain,
                target: self, action: #selector(requestClose))
        } else {
            backLease?.invalidate()
            navigationItem.hidesBackButton = false
            navigationItem.leftBarButtonItem = nil
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
