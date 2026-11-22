#if os(iOS)
import UIKit
import MeowUI

@MainActor
extension MusicSearchResultsViewController {
    public func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
        if case .heading? = rowID(in: tableView, at: indexPath) { return 44 }
        if case .status(let section)? = rowID(in: tableView, at: indexPath) {
            if footerState(kind: Kind.music[tableView.tag], section: section)?.showsSkeleton == true {
                return MusicLoadingSkeletonView.preferredHeight + 72
            }
            guard let footer = statusView(in: tableView, section: section) else { return .leastNormalMagnitude }
            return max(52, footer.systemLayoutSizeFitting(CGSize(width: tableView.bounds.width, height: 0),
                withHorizontalFittingPriority: .required, verticalFittingPriority: .fittingSizeLevel).height)
        }
        if case .song? = rowID(in: tableView, at: indexPath) { return 72 }
        let columns = playlistColumns(tableView)
        return columns == 1 ? 80 : (tableView.bounds.width - 32 - CGFloat(columns - 1) * 12) /
            CGFloat(columns) + 60
    }
    public func tableView(_ tableView: UITableView, estimatedHeightForRowAt indexPath: IndexPath) -> CGFloat {
        self.tableView(tableView, heightForRowAt: indexPath)
    }
    func makeCell(in tableView: UITableView, at indexPath: IndexPath,
                          id: MusicSearchResultRowID) -> UITableViewCell? {
        if case .heading(let section) = id {
            let cell = tableView.dequeueReusableCell(
                withIdentifier: MusicSearchSectionHeaderCell.reuseIdentifier,
                for: indexPath
            ) as! MusicSearchSectionHeaderCell
            configureHeaderCell(cell, in: tableView, section: section)
            return cell
        }
        if case .song(let id) = id, let row = songRows[id] {
            let cell = tableView.dequeueReusableCell(withIdentifier: MusicSearchSongCell.reuseIdentifier,
                                                     for: indexPath) as! MusicSearchSongCell
            configureSongCell(cell, row: row)
            return cell
        }
        if case .status(let section) = id {
            let cell = tableView.dequeueReusableCell(withIdentifier: MusicSearchStatusCell.reuseIdentifier, for: indexPath) as! MusicSearchStatusCell
            configureStatusCell(cell, in: tableView, section: section)
            return cell
        }
        let cell = tableView.dequeueReusableCell(withIdentifier: "Playlists", for: indexPath) as! MusicSearchPlaylistCell
        if let page = pages[Kind.music[tableView.tag]] { configurePlaylistCell(cell, id: id, page: page) }
        return cell
    }

    func configureStatusCell(_ cell: MusicSearchStatusCell, in tableView: UITableView, section: Int) {
        cell.setFooter(statusView(in: tableView, section: section))
    }
    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        if let song = song(in: tableView, at: indexPath) { actions.playSingle(song, session: results) }
    }
    public func tableView(_ tableView: UITableView, trailingSwipeActionsConfigurationForRowAt indexPath: IndexPath)
        -> UISwipeActionsConfiguration? {
        guard let song = song(in: tableView, at: indexPath) else { return nil }
        let request = results.requestID
        let favorite = UIContextualAction(style: .normal,
            title: actions.isFavorite(song) ? "取消收藏" : "收藏") { [weak self] _, _, finish in
            guard let self, self.results.requestID == request else { finish(false); return }
            self.actions.toggleFavorite(song) { try await self.results.refreshTrack(song) }
            finish(true)
        }
        favorite.backgroundColor = UIColor(theme.accent)
        return UISwipeActionsConfiguration(actions: [favorite])
    }
    public func tableView(_ tableView: UITableView, contextMenuConfigurationForRowAt indexPath: IndexPath,
                   point: CGPoint) -> UIContextMenuConfiguration? {
        guard let song = song(in: tableView, at: indexPath) else { return nil }
        let request = results.requestID
        return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
            guard let self, self.results.requestID == request else { return nil }
            return self.songMenu(song, request: request)
        }
    }
    public func tableView(_ tableView: UITableView, willDisplay cell: UITableViewCell, forRowAt indexPath: IndexPath) {
        guard isPageVisible, !paging, Kind.music[tableView.tag] == selected, selected != .all else { return }
        if case .song? = rowID(in: tableView, at: indexPath), indexPath.row >= results.songs.count - 3 {
            results.more(.songs, automatic: true)
        }
        if case .playlists? = rowID(in: tableView, at: indexPath),
           indexPath.row * playlistColumns(tableView) >= results.playlists.count - 3 {
            results.more(.playlists, automatic: true)
        }
    }
    public func tableView(_ tableView: UITableView, didEndDisplaying cell: UITableViewCell, forRowAt indexPath: IndexPath) {
        (cell as? MusicSearchPlaylistCell)?.resetPressFeedback()
    }
    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        if let table = scrollView as? UITableView {
            for cell in table.visibleCells.compactMap({ $0 as? MusicSearchPlaylistCell }) { cell.resetPressFeedback() }
        }
        guard isPageVisible, !paging, scrollView === pages[selected]?.table else { return }
        results.resumePagination()
    }

    func resetPlaylistPressFeedback() {
        for page in pages.values {
            for cell in page.table.visibleCells.compactMap({ $0 as? MusicSearchPlaylistCell }) {
                cell.resetPressFeedback()
            }
        }
    }

    private func rowID(in table: UITableView, at path: IndexPath) -> MusicSearchResultRowID? {
        pages[Kind.music[table.tag]]?.dataSource.itemIdentifier(for: path)
    }

    private func song(in table: UITableView, at path: IndexPath) -> MusicSearchSong? {
        guard case .song(let id)? = rowID(in: table, at: path) else { return nil }
        return songRows[id]?.song
    }

    private func configureSongCell(_ cell: MusicSearchSongCell, row: (song: MusicSearchSong, index: Int)) {
        let active = model.current.map { MusicSearchFusion.sameRecording($0.track, row.song.track) } == true
        let request = results.requestID
        cell.configure(
            song: row.song,
            active: active,
            favorite: actions.isFavorite(row.song),
            enabled: !actions.busy,
            theme: theme,
            onPlay: { [weak self] in
                guard let self, self.results.requestID == request else { return }
                self.actions.playSingle(row.song, session: self.results)
            },
            onFavorite: { [weak self] in
                guard let self, self.results.requestID == request else { return }
                self.actions.toggleFavorite(row.song) { try await self.results.refreshTrack(row.song) }
            },
            onAppend: { [weak self] in
                guard let self, self.results.requestID == request else { return }
                self.actions.perform(song: row.song, session: self.results) { [weak model = self.model] in
                    await model?.append($0)
                }
            },
            menu: songMenu(row.song, request: request)
        )
    }

    func updateVisibleSongs() {
        for page in pages.values {
            for path in page.table.indexPathsForVisibleRows ?? [] {
                guard case .song(let id)? = page.dataSource.itemIdentifier(for: path), let row = songRows[id],
                      let cell = page.table.cellForRow(at: path) as? MusicSearchSongCell else { continue }
                configureSongCell(cell, row: row)
            }
        }
    }

    func configurePlaylistCell(_ cell: UITableViewCell, id: MusicSearchResultRowID,
                                       page: MusicSearchTableController) {
        guard case .playlists(let id) = id, let items = page.playlistRows[id],
              let cell = cell as? MusicSearchPlaylistCell else { return }
        let request = results.requestID
        cell.configure(items, columns: page.columns, theme: theme, images: model.artworkImages, open: { [weak self] in
            guard let self, self.results.requestID == request else { return }
            self.router?.showPublicCollection($0)
        })
    }

    func headingTitle(kind: Kind, section: Int) -> String? {
        if section == 0, kind != .playlists { return "单曲" }
        if section == 1, kind == .all { return "歌单" }
        return nil
    }

    func configureHeaderCell(_ cell: MusicSearchSectionHeaderCell,
                                     in tableView: UITableView, section: Int) {
        let kind = Kind.music[tableView.tag]
        guard let title = headingTitle(kind: kind, section: section) else { return }
        let request = results.requestID
        cell.configure(title: title, showsPlay: section == 0 && !results.songs.isEmpty,
                       theme: theme) { [weak self] in
            guard let self, self.results.requestID == request else { return }
            self.actions.playAll(session: self.results)
        }
    }

    private func songMenu(_ song: MusicSearchSong, request: UUID) -> UIMenu {
        var menu: [UIMenuElement] = [
            UIAction(title: "下一首播放") { [weak self] _ in
                guard let self, self.results.requestID == request else { return }
                self.actions.perform(song: song, session: self.results) { [weak model = self.model] in
                    await model?.insertNext($0)
                }
            },
            UIAction(title: "添加到播放队列") { [weak self] _ in
                guard let self, self.results.requestID == request else { return }
                self.actions.perform(song: song, session: self.results) { [weak model = self.model] in
                    await model?.append($0)
                }
            },
            UIAction(title: "音源") { [weak self] _ in
                guard let self, self.results.requestID == request else { return }
                self.showSources(song)
            },
        ]
        let collections = model.collections.filter { $0.canEdit && $0.kind == "playlist" }.map { target in
            UIAction(title: target.title) { [weak self] _ in
                guard let self, self.results.requestID == request else { return }
                self.actions.perform(song: song, session: self.results) { [weak model = self.model] in
                    await model?.add($0, to: target)
                }
            }
        }
        if !collections.isEmpty { menu.append(UIMenu(title: "添加到歌单", children: collections)) }
        return UIMenu(children: menu)
    }

    func showActionError() {
        let message = actions.actionError ?? actions.error?.localizedDescription
        guard let message, !message.isEmpty else {
            shownActionError = nil
            return
        }
        guard shownActionError != message else { return }
        shownActionError = message
        enqueueError(message)
    }

    func showSearchFailure(section requested: Int? = nil, force: Bool = false) {
        if results.songFailures.isEmpty { shownFailures[0] = nil }
        if results.playlistFailures.isEmpty { shownFailures[1] = nil }
        let sections = requested.map { [$0] } ?? (selected == .songs ? [0] : selected == .playlists ? [1] : [0, 1])
        guard canPresentPageAlert, !selected.isAdditional else { return }
        for section in sections {
            let failures = section == 0 ? results.songFailures : results.playlistFailures
            guard !failures.isEmpty else { continue }
            let message = failures.map { $0.error.message(operation: section == 0 ? "单曲搜索" : "歌单搜索",
                platform: $0.platform.title, diagnosticID: $0.diagnosticID) }.joined(separator: "；")
            guard force || shownFailures[section] != message else { continue }
            let request = results.requestID
            shownFailures[section] = message
            showRetryableError(message) { [weak self] in
                guard let self, self.results.requestID == request else { return }
                self.shownFailures[section] = nil
                self.results.retry(section == 0 ? .songs : .playlists)
            }
            return
        }
    }
    func playlistCount(_ kind: Kind) -> Int {
        kind == .songs ? 0 : kind == .all ? min(5, results.playlists.count) : results.playlists.count
    }
    func playlistColumns(_ table: UITableView) -> Int {
        guard Kind.music[table.tag] == .playlists, appearance.preferences.playlistView == .grid else { return 1 }
        return table.bounds.width - 32 >= 327 ? 3 : 2
    }
    private func showSources(_ song: MusicSearchSong) {
        guard canPresentPageAlert else { return }
        let request = results.requestID
        results.holdPagination()
        let sources = MusicSourcesViewController(model: model, tracks: song.variants,
            appearance: appearance, originIsCurrent: { [weak results] in results?.requestID == request })
        presentPageSheet(sources)
    }
    private func statusView(in tableView: UITableView, section: Int) -> UIView? {
        let kind = Kind.music[tableView.tag]
        guard let state = footerState(kind: kind, section: section) else { return nil }
        let footer = UIStackView()
        footer.axis = .vertical; footer.spacing = 8
        if state.showsSkeleton, let page = pages[kind] {
            let skeleton = page.skeletons[section] ?? MusicLoadingSkeletonView()
            if page.skeletons[section] == nil {
                page.skeletons[section] = skeleton
                skeleton.heightAnchor.constraint(equalToConstant: MusicLoadingSkeletonView.preferredHeight).isActive = true
            }
            skeleton.configure(loading: true, visible: isPageVisible && selected == kind, theme: theme)
            footer.addArrangedSubview(skeleton)
        }
        if let message = state.message {
            let text = UILabel(); text.text = message; text.numberOfLines = 0
            text.textColor = UIColor(theme.muted); text.font = .systemFont(ofSize: 13)
            text.textAlignment = .center
            footer.addArrangedSubview(text)
        }
        if let label = state.label, let action = state.action {
            let button = UIButton(type: .system)
            button.setTitle(label, for: .normal); button.tintColor = UIColor(theme.ink)
            button.titleLabel?.font = .systemFont(ofSize: 14)
            if state.showsSkeleton { button.heightAnchor.constraint(equalToConstant: 40).isActive = true }
            button.accessibilityIdentifier = "music.search.results.action.\(tableView.tag).\(section)"
            if action == .category {
                button.backgroundColor = UIColor(theme.chip)
                button.layer.cornerRadius = 10
                button.contentEdgeInsets = UIEdgeInsets(top: 9, left: 16, bottom: 9, right: 16)
                button.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
            }
            button.addAction(UIAction { [weak self] _ in
                guard let self, self.isPageActive, self.results.requestID == state.request else { return }
                let type: MusicSearchSection = section == 0 ? .songs : .playlists
                switch action {
                case .cancel: self.results.cancel(type)
                case .more: self.results.more(type)
                case .category: self.select(section == 0 ? .songs : .playlists, animated: true)
                }
            }, for: .touchUpInside)
            footer.addArrangedSubview(button)
        }
        footer.isLayoutMarginsRelativeArrangement = true
        footer.layoutMargins = UIEdgeInsets(top: 8, left: 16, bottom: 16, right: 16)
        return footer
    }

    func footerState(kind: Kind, section: Int) -> MusicSearchFooterState? {
        guard section == 0 ? kind != .playlists : kind != .songs else { return nil }
        let loading = section == 0 ? results.songsLoading : results.playlistsLoading
        let failures = section == 0 ? results.songFailures : results.playlistFailures
        let cancelled = section == 0 ? results.songsCancelled : results.playlistsCancelled
        let noPlatforms = section == 0 ? results.songPlatformsEmpty : results.playlistPlatformsEmpty
        let loaded = section == 0 ? results.songsLoaded : results.playlistsLoaded
        let empty = section == 0 ? results.songs.isEmpty : results.playlists.isEmpty
        let showsSkeleton = loading && empty && !loaded
        var message: String?
        var label: String?
        var action: MusicSearchFooterState.Action?
        if noPlatforms { message = "尚未启用搜索平台" }
        else if !failures.isEmpty {
            // Failures and retry are surfaced via unified PageViewController alert overlays.
        } else if cancelled {
            message = "已取消"
        } else if loading {
            if !showsSkeleton { message = section == 0 ? "单曲加载中" : "歌单加载中" }
            label = "取消"; action = .cancel
        } else if loaded && empty { message = section == 0 ? "未找到歌曲" : "未找到歌单" }
        else if kind == .all, (section == 0 ? results.songs.count : results.playlists.count) > 5 {
            label = "查看更多"; action = .category
        } else if section == 0 ? results.moreSongs : results.morePlaylists {
            label = "加载更多"; action = .more
        }
        guard message != nil || label != nil else { return nil }
        return .init(message: message, label: label, action: action, showsSkeleton: showsSkeleton, request: results.requestID)
    }
    public func tableView(_ tableView: UITableView, heightForFooterInSection section: Int) -> CGFloat { .leastNormalMagnitude }
}
#endif
