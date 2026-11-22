#if os(iOS)
import UIKit
import MeowUI

@MainActor
extension MusicCollectionViewController {
    func applyRows(_ next: [(id: RowID, row: Row)]) {
        let previous = rows
        var deduped: [(id: RowID, row: Row)] = []
        deduped.reserveCapacity(next.count)
        var nextRows: [RowID: Row] = [:]
        nextRows.reserveCapacity(next.count)
        for entry in next {
            guard nextRows[entry.id] == nil else { continue }
            nextRows[entry.id] = entry.row
            deduped.append(entry)
        }
        orderedRows = deduped
        rows = nextRows
        var snapshot = NSDiffableDataSourceSnapshot<Int, RowID>()
        snapshot.appendSections([0])
        snapshot.appendItems(deduped.map(\.id))
        let changed = deduped.compactMap { value -> RowID? in
            guard let old = previous[value.id] else { return nil }
            return old.track != value.row.track ||
                old.filteredIndex != value.row.filteredIndex ||
                old.originalIndex != value.row.originalIndex ? value.id : nil
        }
        if !changed.isEmpty {
            snapshot.reconfigureItems(changed)
        }
        if dataSource.snapshot().itemIdentifiers != snapshot.itemIdentifiers || !changed.isEmpty {
            dataSource.apply(snapshot, animatingDifferences: false)
        }
    }

    func configure(_ cell: MusicCollectionTrackCell, row: Row) {
        updateRowActionsRevision()
        switch content {
        case .local(let page):
            configureLocal(cell, row: row, page: page)
        case .remote(_, let page):
            configureRemote(cell, row: row, page: page)
        }
    }

    func configureLocal(
        _ cell: MusicCollectionTrackCell,
        row: Row,
        page: MusicCollectionViewModel
    ) {
        let track = row.track
        let active = model.current?.track.songId.utf8.elementsEqual(track.songId.utf8) == true
        let presentationID = page.presentationID
        let filterID = page.filterID
        cell.configure(
            track: track,
            active: active,
            isFavorite: page.isFavorite(track.ref),
            favoriteEnabled: page.canAct && page.favoritesLoaded,
            actionsEnabled: page.canAct,
            theme: theme,
            images: model.artworkImages,
            actionRevision: rowActionsRevision,
            menu: row.originalIndex.map {
                localTrackMenu(
                    track: track,
                    originalIndex: $0,
                    presentationID: presentationID,
                    page: page
                )
            },
            filteredIndex: row.filteredIndex,
            originalIndex: row.originalIndex,
            onPlay: { [weak page] in
                page?.playFiltered(
                    at: row.filteredIndex,
                    presentationID: presentationID,
                    filterID: filterID
                )
            },
            onFavorite: { [weak page] in
                page?.toggleFavorite(track.ref, presentationID: presentationID)
            },
            onAppend: { [weak page] in
                guard let originalIndex = row.originalIndex else { return }
                page?.enqueue(at: originalIndex, next: false, presentationID: presentationID)
            }
        )
    }

    func configureRemote(
        _ cell: MusicCollectionTrackCell,
        row: Row,
        page: MusicPublicBrowseViewModel
    ) {
        let track = row.track
        let active = model.current.map { MusicMiniPlayerFavorites.sameSong($0.track, track) } == true
        let enabled = canActRemotely(page)
        cell.configure(
            track: track,
            active: active,
            isFavorite: favorites?.isFavorite(track) == true,
            favoriteEnabled: enabled && favorites?.loaded == true &&
                favorites?.loading == false && favorites?.busy == false,
            actionsEnabled: enabled,
            theme: theme,
            images: model.artworkImages,
            actionRevision: rowActionsRevision,
            menu: remoteTrackMenu(track, page: page),
            filteredIndex: row.filteredIndex,
            originalIndex: nil,
            onPlay: { [weak self] in self?.playRemote(track, page: page) },
            onFavorite: { [weak self] in self?.toggleRemoteFavorite(track, page: page) },
            onAppend: { [weak self] in self?.enqueueRemote(track, next: false, page: page) }
        )
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard let row = row(at: indexPath) else { return }
        switch content {
        case .local(let page):
            page.playFiltered(
                at: row.filteredIndex,
                presentationID: page.presentationID,
                filterID: page.filterID
            )
        case .remote(_, let page):
            playRemote(row.track, page: page)
        }
    }

    public func tableView(
        _ tableView: UITableView,
        trailingSwipeActionsConfigurationForRowAt indexPath: IndexPath
    ) -> UISwipeActionsConfiguration? {
        guard let page = localPage, let row = row(at: indexPath) else { return nil }
        let track = row.track
        let presentationID = page.presentationID
        let favorite = UIContextualAction(
            style: .normal,
            title: page.isFavorite(track.ref) ? "取消收藏" : "收藏"
        ) { [weak page] _, _, finish in
            guard let page else {
                finish(false)
                return
            }
            page.toggleFavorite(track.ref, presentationID: presentationID)
            finish(true)
        }
        favorite.backgroundColor = UIColor(theme.accent)
        var actions = [favorite]
        if page.canEdit, let originalIndex = row.originalIndex {
            let remove = UIContextualAction(style: .destructive, title: "移除") {
                [weak self] _, _, finish in
                self?.confirmLocalRemoval(
                    track: track,
                    index: originalIndex,
                    presentationID: presentationID,
                    page: page
                )
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
        guard let row = row(at: indexPath) else { return nil }
        switch content {
        case .local(let page):
            guard let originalIndex = row.originalIndex else { return nil }
            let presentationID = page.presentationID
            return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
                self?.localTrackMenu(
                    track: row.track,
                    originalIndex: originalIndex,
                    presentationID: presentationID,
                    page: page
                )
            }
        case .remote(_, let page):
            guard canActRemotely(page) else { return nil }
            return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
                self?.remoteTrackMenu(row.track, page: page)
            }
        }
    }

    public func tableView(
        _ tableView: UITableView,
        willDisplay cell: UITableViewCell,
        forRowAt indexPath: IndexPath
    ) {
        guard remotePage != nil, remoteFilter.isEmpty,
              indexPath.row >= max(0, orderedRows.count - 3) else { return }
        loadAutomaticRemoteNextPage()
    }

    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        if remotePage != nil { automaticPaginationHeld = false }
    }

    public func scrollViewDidScroll(_ scrollView: UIScrollView) {
        updateScrollChrome()
    }

}
#endif
