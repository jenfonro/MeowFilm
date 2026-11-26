#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
final class MusicCollectionEditorViewController: MusicPageViewController,
    UITableViewDelegate
{
    private final class DataSource: UITableViewDiffableDataSource<Int, MusicReference> {
        weak var owner: MusicCollectionEditorViewController?

        override func tableView(_ tableView: UITableView, canEditRowAt indexPath: IndexPath) -> Bool {
            owner?.canReorder ?? false
        }

        override func tableView(_ tableView: UITableView, canMoveRowAt indexPath: IndexPath) -> Bool {
            owner?.canReorder ?? false
        }

        override func tableView(_ tableView: UITableView, moveRowAt sourceIndexPath: IndexPath,
                                to destinationIndexPath: IndexPath) {
            owner?.moveTrack(from: sourceIndexPath, to: destinationIndexPath)
        }
    }

    private enum Content {
        case local(MusicCollectionViewModel, MusicCollectionEditorDraft)
        case remote(MusicPublicBrowseViewModel)
    }

    private let content: Content
    private let owner: MusicViewModel
    private weak var origin: MusicCollectionViewController?
    private let table = UITableView(frame: .zero, style: .plain)
    private var dataSource: DataSource!
    private let toolbar = MusicCollectionEditorToolbarView()
    private let actions = MusicCollectionEditorActionsView()
    private var closing = false
    private var projection = IndexedDiffableProjection<Int, MusicReference, MusicRemoteTrack>()
    private var displayedTracks: [MusicRemoteTrack] = []
    private var displayedTrackIndices: [MusicReference: Int] = [:]
    private var remoteSelected: Set<MusicReference> = []

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
        table.delegate = self
        table.register(MusicCollectionEditorTrackCell.self,
                       forCellReuseIdentifier: MusicCollectionEditorTrackCell.reuseIdentifier)
        dataSource = DataSource(tableView: table) { [weak self] tableView, indexPath, ref in
            let cell = tableView.dequeueReusableCell(
                withIdentifier: MusicCollectionEditorTrackCell.reuseIdentifier,
                for: indexPath
            ) as! MusicCollectionEditorTrackCell
            if let self, let track = self.projection[ref] {
                self.configure(cell, track: track)
            }
            return cell
        }
        dataSource.owner = self
        dataSource.defaultRowAnimation = .none
        renderTracks()
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
            observe(draft, whenVisible: true) { [weak self] in
                self?.renderTracks()
                self?.updateSelection()
                self?.renderActions()
            }
            let localChanges = Publishers.MergeMany([
                page.$isLoading.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$isActing.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$editor.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$error.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$actionError.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$presentationID.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            ])
            observe(localChanges.eraseToAnyPublisher(), whenVisible: true) { [weak self] in self?.renderActions() }
        case .remote(let page):
            let remoteChanges = Publishers.MergeMany([
                page.$sections.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$loaded.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$contentIsCurrent.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$isLoading.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$isActing.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$actionError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            ])
            observe(remoteChanges.eraseToAnyPublisher(), whenVisible: true) { [weak self] in
                self?.renderTracks()
                self?.renderActions()
            }
        }
        observe(owner.libraryChanges, whenVisible: true) { [weak self] in self?.renderActions() }
        renderActions()
    }

    public override func pageDidBecomeVisible() {
        owner.artworkImages.retryFailedVisible()
        if let local = localContent, local.page.editor == nil {
            if canPresentPageAlert { navigationController?.popViewController(animated: false) }
            return
        }
        for cell in table.visibleCells {
            (cell as? MusicCollectionEditorTrackCell)?.resumePendingArtworkLoad()
        }
        renderActions()
    }

    public override func pageDidBecomeHidden() {
        for cell in table.visibleCells {
            (cell as? MusicCollectionEditorTrackCell)?.cancelPendingArtworkLoad()
        }
    }

    public override func pageDidFinishAppearing() {
        if let local = localContent, local.page.editor !== local.draft {
            renderActions()
        }
    }

    public override func pageDidBecomeInactive() {
        for cell in table.visibleCells {
            (cell as? MusicCollectionEditorTrackCell)?.cancelPendingArtworkLoad()
        }
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
                       selectable: canSelect, showsReorder: canReorder, theme: theme, images: owner.artworkImages)
        cell.onToggle = { [weak self, reference = track.ref] in
            self?.toggleSelection(reference)
        }
    }

    private func moveTrack(from sourceIndexPath: IndexPath, to destinationIndexPath: IndexPath) {
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

    public func tableView(_ tableView: UITableView, willDisplay cell: UITableViewCell, forRowAt indexPath: IndexPath) {
        if isPageVisible {
            (cell as? MusicCollectionEditorTrackCell)?.resumePendingArtworkLoad()
        }
    }

    public func tableView(_ tableView: UITableView, didEndDisplaying cell: UITableViewCell, forRowAt indexPath: IndexPath) {
        (cell as? MusicCollectionEditorTrackCell)?.cancelPendingArtworkLoad()
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
        presentPageConfirmation(title: "放弃未保存的修改？", confirmTitle: "放弃", cancelTitle: "继续编辑", destructive: true, onConfirm: { [weak self] in
            guard let self else { return }
            self.closing = true
            page.discardEditing(draft)
            self.navigationController?.popViewController(animated: true)
        })
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
        actions.addToCollection.isEnabled = actions.enqueue.isEnabled && !pageCollectionTargets.isEmpty
        toolbar.configure(allSelected: allSelected, canSelect: canSelect && !displayedTracks.isEmpty,
                          canSave: localContent == nil || canAct, theme: theme)
        actions.applyTheme(theme)
        table.isUserInteractionEnabled = canSelect
        updateVisibleTracks()
        updateEditorTitle()
        switch content {
        case .local(let page, _):
            if let actionError = page.actionError, !actionError.isEmpty {
                enqueueError(actionError, acknowledged: { [weak page] in page?.clearActionError() })
            }
            syncErrorSource("read", message: page.error?.localizedDescription)
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
        let update = projection.update(section: 0, items: next, id: { $0.ref })
        displayedTracks = projection.items
        guard update.hasChanges else { return }
        rebuildDisplayedIndices()
        dataSource.applyIfNeeded(update, animatingDifferences: false)
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
                guard let self, page != nil else { return }
                await self.owner.insertNext(tracks)
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
        let sheetActions = pageCollectionTargets.map { target in
            PageAlertAction(title: target.title) { [weak self] in
                guard let self else { return }
                switch self.content {
                case .local(let page, let draft):
                    page.addSelected(draft, to: target)
                case .remote(let page):
                    self.performRemote(page: page, operation: "收藏到歌单", kind: .library) {
                        [weak self, weak page] in
                        guard let self, page != nil else { return }
                        await self.owner.add(tracks, to: target)
                    }
                }
            }
        }
        guard !sheetActions.isEmpty else { return }
        presentPageActionSheet(title: "收藏到歌单", sourceView: actions.addToCollection, actions: sheetActions)
    }

    private var pageCollectionTargets: [MusicCollection] {
        let collectionID = localContent?.page.collectionID
        return owner.collections.filter {
            ["playlist", "favorites"].contains($0.kind) && $0.id != collectionID
        }
    }

    private func canActRemotely(_ page: MusicPublicBrowseViewModel) -> Bool {
        page.loaded && page.contentIsCurrent && !page.isLoading && !page.isActing &&
            !owner.isClosed
    }

    private func performRemote(
        page: MusicPublicBrowseViewModel,
        operation: String,
        kind: MusicRequestTrace.Operation,
        _ action: @escaping @MainActor () async throws -> Void
    ) {
        runPageTask { [weak page] in
            await page?.perform(operation: operation, kind: kind, action)
        }
    }

    private func showRemoteActionError(_ page: MusicPublicBrowseViewModel) {
        guard let error = page.actionError, let id = page.actionErrorID else { return }
        let message = error.message(operation: page.actionOperation, diagnosticID: page.actionDiagnosticID)
        enqueueError(message, acknowledged: { [weak page] in page?.dismissActionError(id: id) })
    }
}

#endif
