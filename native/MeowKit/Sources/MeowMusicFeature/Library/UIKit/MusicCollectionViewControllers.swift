#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
final class MusicCollectionEditorViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    private enum Content {
        case local(MusicCollectionViewModel, MusicCollectionEditorDraft)
        case remote(MusicPublicBrowseViewModel)
    }

    private let content: Content
    private let owner: MusicViewModel
    private weak var origin: MusicCollectionViewController?
    private let table = UITableView(frame: .zero, style: .plain)
    private let toolbar = MusicCollectionEditorToolbarView()
    private let actions = MusicCollectionEditorActionsView()
    private var closing = false
    private var displayedTracks: [MusicRemoteTrack] = []
    private var displayedTrackIndices: [MusicReference: Int] = [:]
    private var remoteSelected: Set<MusicReference> = []
    private var shownRemoteActionError: String?

    private var localContent: (page: MusicCollectionViewModel, draft: MusicCollectionEditorDraft)? {
        guard case .local(let page, let draft) = content else { return nil }
        return (page, draft)
    }

    private var remotePage: MusicPublicBrowseViewModel? {
        guard case .remote(let page) = content else { return nil }
        return page
    }

    private var selectedReferences: Set<MusicReference> {
        switch content {
        case .local(_, let draft): draft.selected
        case .remote: remoteSelected
        }
    }

    private var selectedTracks: [MusicRemoteTrack] {
        switch content {
        case .local(_, let draft): draft.selectedTracks
        case .remote: displayedTracks.filter { remoteSelected.contains($0.ref) }
        }
    }

    private var allSelected: Bool {
        !displayedTracks.isEmpty && displayedTracks.allSatisfy { selectedReferences.contains($0.ref) }
    }

    private var canSelect: Bool {
        switch content {
        case .local(_, let draft): draft.canEdit
        case .remote(let page): canActRemotely(page)
        }
    }

    private var canAct: Bool {
        switch content {
        case .local(let page, _): page.canAct
        case .remote(let page): canActRemotely(page)
        }
    }

    private var canReorder: Bool { localContent?.draft.canEdit == true }

    init(
        page: MusicCollectionViewModel,
        draft: MusicCollectionEditorDraft,
        owner: MusicViewModel,
        origin: MusicCollectionViewController,
        appearance: MusicAppearancePreferencesStore
    ) {
        content = .local(page, draft)
        self.owner = owner
        self.origin = origin
        super.init(appearance: appearance)
        pageTitle = draft.title
    }

    init(
        page: MusicPublicBrowseViewModel,
        title: String,
        owner: MusicViewModel,
        origin: MusicCollectionViewController,
        appearance: MusicAppearancePreferencesStore
    ) {
        content = .remote(page)
        self.owner = owner
        self.origin = origin
        super.init(appearance: appearance)
        pageTitle = title
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.accessibilityIdentifier = "music.collection.editor.tracks"
        table.separatorStyle = .none
        table.contentInsetAdjustmentBehavior = .never
        table.rowHeight = 72
        table.estimatedRowHeight = 72
        table.contentInset.bottom = 18
        displayedTracks = currentTracks()
        rebuildDisplayedIndices()
        table.dataSource = self
        table.delegate = self
        table.register(MusicCollectionEditorTrackCell.self,
                       forCellReuseIdentifier: MusicCollectionEditorTrackCell.reuseIdentifier)
        table.isEditing = localContent != nil
        table.allowsSelectionDuringEditing = true
        toolbar.onSelectAll = { [weak self] in self?.toggleAll() }
        toolbar.onDone = { [weak self] in self?.save() }
        actions.showsDelete = localContent != nil
        actions.enqueue.addAction(UIAction { [weak self] _ in
            self?.enqueueSelected()
        }, for: .touchUpInside)
        actions.addToCollection.addAction(
            UIAction { [weak self] _ in self?.chooseCollection() },
            for: .touchUpInside
        )
        actions.deleteSelected.addAction(
            UIAction { [weak self] _ in self?.localContent?.draft.deleteSelected() },
            for: .touchUpInside
        )
        toolbar.translatesAutoresizingMaskIntoConstraints = false
        table.translatesAutoresizingMaskIntoConstraints = false
        actions.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(toolbar)
        view.addSubview(table)
        view.addSubview(actions)
        NSLayoutConstraint.activate([
            toolbar.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            toolbar.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            toolbar.topAnchor.constraint(equalTo: contentTopAnchor),
            toolbar.heightAnchor.constraint(equalToConstant: 58),
            table.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            table.topAnchor.constraint(equalTo: toolbar.bottomAnchor),
            table.bottomAnchor.constraint(equalTo: actions.topAnchor),
            actions.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            actions.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            actions.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
            actions.heightAnchor.constraint(equalToConstant: 72),
        ])

        switch content {
        case .local(let page, let draft):
            observe(draft) { [weak self] in
                self?.renderTracks()
                self?.updateSelection()
                self?.renderActions()
            }
            observe(page) { [weak self] in self?.renderActions() }
        case .remote(let page):
            observe(page) { [weak self] in
                self?.renderTracks()
                self?.renderActions()
            }
            observe(owner.accessChanges) { [weak self] in self?.renderActions() }
        }
        observe(owner.libraryChanges) { [weak self] in self?.renderActions() }
        renderActions()
    }

    public override func pageDidBecomeVisible() {
        if let local = localContent, local.page.editor == nil {
            if canPresentPageAlert { navigationController?.popViewController(animated: false) }
            return
        }
        renderActions()
    }

    public override func pageDidFinishAppearing() {
        if let local = localContent,
           local.page.editor !== local.draft || local.page.error != nil || local.page.actionError != nil {
            renderActions()
        } else if remotePage?.actionError != nil {
            renderActions()
        }
    }

    public override func pageDidBecomeInactive() {
        // A popped editor has no navigation controller; ask its detail owner instead.
        guard origin?.navigationController == nil else {
            if case .local(let page, let draft) = content, page.editor === draft {
                closing = true
                page.discardEditing(draft)
            }
            return
        }
        switch content {
        case .local(let page, _): page.disappear()
        case .remote(let page): page.disappear()
        }
    }

    public override func requestsBackConfirmationForNavigationPop() -> Bool {
        guard case .local(let page, let draft) = content else { return false }
        return page.editor === draft && draft.hasChanges
    }

    public override func requestBackConfirmationForNavigationPop() { requestClose() }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        toolbar.applyTheme(theme)
        actions.applyTheme(theme)
        updateVisibleTracks()
    }

    private func currentTracks() -> [MusicRemoteTrack] {
        switch content {
        case .local(let page, let draft): page.editor === draft ? draft.tracks : []
        case .remote(let page): page.contentIsCurrent ? page.tracks : []
        }
    }

    private func rebuildDisplayedIndices() {
        displayedTrackIndices = Dictionary(
            displayedTracks.enumerated().map { ($0.element.ref, $0.offset) },
            uniquingKeysWith: { first, _ in first }
        )
    }

    private func updateVisibleTracks(in range: ClosedRange<Int>? = nil) {
        for path in table.indexPathsForVisibleRows ?? [] {
            guard displayedTracks.indices.contains(path.row), range?.contains(path.row) != false,
                  let cell = table.cellForRow(at: path) as? MusicCollectionEditorTrackCell else { continue }
            configure(cell, track: displayedTracks[path.row])
        }
    }

    private func configure(_ cell: MusicCollectionEditorTrackCell, track: MusicRemoteTrack) {
        cell.configure(track: track, selected: selectedReferences.contains(track.ref),
                       selectable: canSelect, showsReorder: canReorder, theme: theme)
        cell.onToggle = { [weak self, reference = track.ref] in
            self?.toggleSelection(reference)
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
            withIdentifier: MusicCollectionEditorTrackCell.reuseIdentifier,
            for: indexPath
        ) as! MusicCollectionEditorTrackCell
        configure(cell, track: track)
        return cell
    }

    public func tableView(
        _ tableView: UITableView,
        canEditRowAt indexPath: IndexPath
    ) -> Bool { canReorder }

    public func tableView(
        _ tableView: UITableView,
        canMoveRowAt indexPath: IndexPath
    ) -> Bool { canReorder }

    public func tableView(
        _ tableView: UITableView,
        moveRowAt sourceIndexPath: IndexPath,
        to destinationIndexPath: IndexPath
    ) {
        guard let draft = localContent?.draft,
              displayedTracks.indices.contains(sourceIndexPath.row),
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
        toggleSelection(displayedTracks[indexPath.row].ref)
    }

    public func tableView(
        _ tableView: UITableView,
        editingStyleForRowAt indexPath: IndexPath
    ) -> UITableViewCell.EditingStyle { .none }

    public func tableView(
        _ tableView: UITableView,
        shouldIndentWhileEditingRowAt indexPath: IndexPath
    ) -> Bool { false }

    private func requestClose() {
        guard !closing else { return }
        guard case .local(let page, let draft) = content else {
            closing = true
            navigationController?.popViewController(animated: true)
            return
        }
        if !draft.hasChanges {
            closing = true
            page.discardEditing(draft)
            navigationController?.popViewController(animated: true)
            return
        }
        let alert = UIAlertController(title: "放弃未保存的修改？", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "继续编辑", style: .cancel))
        alert.addAction(UIAlertAction(title: "放弃", style: .destructive) { [weak self] _ in
            guard let self else { return }
            self.closing = true
            page.discardEditing(draft)
            self.navigationController?.popViewController(animated: true)
        })
        present(alert, animated: true)
    }

    private func save() {
        guard !closing else { return }
        guard case .local(let page, let draft) = content else {
            requestClose()
            return
        }
        runPageTask { [weak self] in
            guard let self, !Task.isCancelled, self.isPageActive, !self.closing else { return }
            self.closing = true
            defer { self.closing = false }
            guard let task = page.saveEditing(draft) else {
                if !draft.hasChanges, self.navigationController?.topViewController === self {
                    self.navigationController?.popViewController(animated: true)
                }
                return
            }
            await task.value
            guard !Task.isCancelled else { return }
            if page.editor == nil, self.navigationController?.topViewController === self {
                self.navigationController?.popViewController(animated: true)
            }
        }
    }

    private func renderActions() {
        if case .local(let page, let draft) = content, page.editor !== draft {
            pageHeader.title = nil
            renderTracks()
            if canPresentPageAlert, !closing, navigationController?.topViewController === self {
                navigationController?.popViewController(animated: true)
            }
        }
        let hasSelection = !selectedReferences.isEmpty
        actions.deleteSelected.isEnabled = canReorder && hasSelection
        actions.enqueue.isEnabled = canAct && hasSelection
        actions.addToCollection.isEnabled = actions.enqueue.isEnabled
        toolbar.configure(allSelected: allSelected, canSelect: canSelect && !displayedTracks.isEmpty,
                          canSave: localContent == nil || canAct, theme: theme)
        actions.applyTheme(theme)
        table.isUserInteractionEnabled = canSelect
        updateVisibleTracks()
        updateEditorTitle()
        switch content {
        case .local(let page, _):
            _ = showError(page.actionError ?? page.error?.localizedDescription)
        case .remote(let page):
            showRemoteActionError(page)
        }
    }

    private func renderTracks() {
        let next = currentTracks()
        if remotePage != nil {
            let refs = Set(next.map(\.ref))
            remoteSelected.formIntersection(refs)
        }
        let difference = next.map(\.ref).difference(from: displayedTracks.map(\.ref)).inferringMoves()
        let contentChanged = remotePage != nil && next != displayedTracks
        let previousCount = displayedTracks.count
        displayedTracks = next
        guard !difference.isEmpty || contentChanged else { return }
        rebuildDisplayedIndices()
        if remotePage != nil || table.numberOfRows(inSection: 0) != previousCount {
            table.reloadData()
        } else {
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
        }
        applyTheme()
        updateSelection()
    }

    private func updateSelection() {
        let selected = selectedReferences
        for case let cell as MusicCollectionEditorTrackCell in table.visibleCells {
            guard let reference = cell.representedReference else { continue }
            cell.setTrackSelected(selected.contains(reference), theme: theme)
        }
    }

    private func toggleSelection(_ reference: MusicReference) {
        switch content {
        case .local(_, let draft):
            draft.toggle(reference)
        case .remote:
            guard canSelect, displayedTrackIndices[reference] != nil else { return }
            if !remoteSelected.insert(reference).inserted { remoteSelected.remove(reference) }
        }
        renderActions()
    }

    private func toggleAll() {
        switch content {
        case .local(_, let draft):
            draft.toggleAll()
        case .remote:
            guard canSelect else { return }
            remoteSelected = allSelected ? [] : Set(displayedTracks.map(\.ref))
        }
        renderActions()
    }

    private func enqueueSelected() {
        switch content {
        case .local(let page, let draft):
            page.enqueueSelected(draft)
        case .remote(let page):
            let tracks = selectedTracks
            guard !tracks.isEmpty else { return }
            performRemote(page: page, operation: "下一首播放", kind: .queue) { [weak self, weak page] in
                guard let self, let page else { return }
                await self.owner.insertNext(try await page.refreshedActionTracks(tracks))
            }
        }
    }

    private func updateEditorTitle() {
        let next: String?
        switch content {
        case .local(let page, let draft): next = page.editor === draft ? draft.title : nil
        case .remote: next = pageTitle
        }
        if pageHeader.title != next { pageHeader.title = next }
    }

    private func chooseCollection() {
        let tracks = selectedTracks
        guard !tracks.isEmpty else { return }
        let alert = UIAlertController(title: "收藏到歌单", message: nil, preferredStyle: .actionSheet)
        for target in pageCollectionTargets {
            alert.addAction(UIAlertAction(title: target.title, style: .default) { [weak self] _ in
                guard let self else { return }
                switch self.content {
                case .local(let page, let draft):
                    page.addSelected(draft, to: target)
                case .remote(let page):
                    self.performRemote(page: page, operation: "收藏到歌单", kind: .library) {
                        [weak self, weak page] in
                        guard let self, let page else { return }
                        await self.owner.add(try await page.refreshedActionTracks(tracks), to: target)
                    }
                }
            })
        }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        if let popover = alert.popoverPresentationController {
            popover.sourceView = actions.addToCollection
            popover.sourceRect = actions.addToCollection.bounds
        }
        present(alert, animated: true)
    }

    private var pageCollectionTargets: [MusicCollection] {
        let collectionID = localContent?.page.collectionID
        return owner.collections.filter {
            ["playlist", "favorites"].contains($0.kind) && $0.id != collectionID
        }
    }

    private func canActRemotely(_ page: MusicPublicBrowseViewModel) -> Bool {
        page.loaded && page.contentIsCurrent && !page.isLoading && !page.isActing &&
            owner.isAvailable && !owner.isClosed
    }

    private func performRemote(
        page: MusicPublicBrowseViewModel,
        operation: String,
        kind: MusicRequestTrace.Operation,
        _ action: @escaping @MainActor () async throws -> Void
    ) {
        guard let intent = page.makeActionIntent() else { return }
        runPageTask { [weak page] in
            guard let page else { return }
            await page.perform(intent: intent, operation: operation, kind: kind, action)
        }
    }

    private func showRemoteActionError(_ page: MusicPublicBrowseViewModel) {
        guard let error = page.actionError else {
            shownRemoteActionError = nil
            return
        }
        let message = error.message(operation: page.actionOperation, diagnosticID: page.actionDiagnosticID)
        guard shownRemoteActionError != message else { return }
        if showError(message) { shownRemoteActionError = message }
    }
}

#endif
