#if os(iOS)
import UIKit
import MeowDebug
import MeowUI

@MainActor
extension FilmDetailViewController {
    func showDirectory(animated: Bool = true) {
        guard let model, let snapshot = model.presentation, snapshot.directoryError == nil,
              let sourceID = snapshot.sourceSelectedID, !snapshot.directoryRawItems.isEmpty else { return }
        let page = FilmDirectoryViewController(
            model: model,
            sourceID: sourceID,
            lineID: snapshot.lineSelectedID,
            themeProvider: themeProvider
        )
        page.onPresentPlayback = onPresentPlayback
        navigationController?.pushViewController(page, animated: animated)
    }

    func renderPlayback() {
        guard isViewLoaded, let model else { return }
        DebugUIMetrics.shared.increment("film.detail.renderPlayback")
        guard let snapshot = model.presentation else {
            if !renderedEmptySourceMenus { renderSourceMenus(nil) }
            play.isEnabled = false
            play.configuration?.title = model.opening ? "准备中" : "播放"
            playbackState.text = model.opening ? "加载中" : nil
            if model.opening { loadingIndicator.startAnimating() } else { loadingIndicator.stopAnimating() }
            showEpisodeState(model.opening ? "加载中" : nil)
            presentPlaybackError()
            return
        }
        renderedEmptySourceMenus = false
        play.isEnabled = !snapshot.noSources && model.canStartPlayback
        play.configuration?.title = snapshot.noSources ? "暂无可用视频源" : "播放"
        play.accessibilityLabel = snapshot.noSources ? "播放，暂无可用视频源" : "播放"
        let busy = model.opening || model.playbackStatus.busy
        if busy { loadingIndicator.startAnimating() } else { loadingIndicator.stopAnimating() }
        playbackState.text = model.playbackStatus == .failed ? nil : model.playbackStatus.title

        let metadataChanged = displayedSnapshot == nil || displayedSnapshot?.work != snapshot.work
        if metadataChanged {
            let next = FilmPlaybackPresentation.detail(snapshot, original: originalDetail)
            let relatedChanged = detail.cast != next.cast || detail.similarItems != next.similarItems
            if detail != next { detail = next; renderMetadata() }
            if relatedChanged { renderRelated() }
        }

        let sourceMenuChanged = displayedSnapshot == nil ||
            displayedSnapshot?.noSources != snapshot.noSources ||
            displayedSnapshot?.sourceSelectedID != snapshot.sourceSelectedID ||
            displayedSnapshot?.sourcePlaceholder != snapshot.sourcePlaceholder ||
            displayedSnapshot?.sourceDisabled != snapshot.sourceDisabled ||
            displayedSnapshot?.sourceCanLoadMore != snapshot.sourceCanLoadMore ||
            displayedSnapshot?.sourceLoadingMore != snapshot.sourceLoadingMore ||
            displayedSnapshot?.sourceOptions != snapshot.sourceOptions ||
            displayedSnapshot?.lineVisible != snapshot.lineVisible ||
            displayedSnapshot?.lineSelectedID != snapshot.lineSelectedID ||
            displayedSnapshot?.lineOptions != snapshot.lineOptions ||
            displayedCanRetryMedia != model.canRetryMedia
        if sourceMenuChanged {
            renderSourceMenus(snapshot)
            displayedCanRetryMedia = model.canRetryMedia
        }

        let directoryChanged = displayedSnapshot == nil ||
            displayedSnapshot?.noSources != snapshot.noSources ||
            displayedSnapshot?.directoryMode != snapshot.directoryMode ||
            displayedSnapshot?.directoryLoading != snapshot.directoryLoading ||
            displayedSnapshot?.directoryError != snapshot.directoryError ||
            displayedSnapshot?.directorySelectedSeason != snapshot.directorySelectedSeason ||
            displayedSnapshot?.directoryCanLoadMore != snapshot.directoryCanLoadMore ||
            displayedSnapshot?.directoryLoadingMore != snapshot.directoryLoadingMore ||
            displayedSnapshot?.directorySeasons != snapshot.directorySeasons ||
            displayedSnapshot?.directoryItems != snapshot.directoryItems ||
            displayedSnapshot?.sourceOptions != snapshot.sourceOptions ||
            displayedSnapshot?.sourceCanLoadMore != snapshot.sourceCanLoadMore ||
            displayedSnapshot?.sourceLoadingMore != snapshot.sourceLoadingMore
        if directoryChanged {
            renderRuntimeEpisodes()
        }
        updateEpisodeAvailability()

        let previewState = (preview: snapshot.previewEpisodes, canToggle: snapshot.canTogglePreview)
        if displayedPreviewState == nil || displayedPreviewState! != previewState {
            displayedPreviewState = previewState
            let enabled = snapshot.canTogglePreview || snapshot.previewEpisodes
            more.isEnabled = enabled
            more.menu = enabled ? UIMenu(children: [
                UIAction(title: "点映", identifier: .init("film.detail.preview"),
                         state: snapshot.previewEpisodes ? .on : .off) { [weak model] _ in
                    guard let model, let current = model.presentation else { return }
                    model.setPreview(!current.previewEpisodes)
                }
            ]) : nil
        }

        renderEpisodeMenu()
        seasonButton.setTitleColor(UIColor(seasonButton.isUserInteractionEnabled ? theme.accent : theme.ink), for: .normal)
        displayedSnapshot = snapshot
        presentPlaybackError()
    }

    func updateEpisodeAvailability() {
        let available = model.map { $0.canSelectFromPresentation && !$0.directoryHasNoVideoSource } ?? false
        episodeCollection.updateAvailability(available)
    }

    func renderSourceMenus(_ snapshot: FilmPlaybackSnapshot?) {
        guard let snapshot else {
            renderedEmptySourceMenus = true
            displayedCanRetryMedia = nil
            sourceButton.setTitle("加载中...", for: .normal)
            sourceButton.accessibilityValue = "加载中..."
            sourceButton.isEnabled = false
            sourceButton.menu = nil
            lineButton.isHidden = true
            lineButton.menu = nil
            return
        }

        if snapshot.noSources {
            sourceButton.setTitle("暂无可用视频源", for: .normal)
            sourceButton.accessibilityValue = "暂无可用视频源"
            sourceButton.isEnabled = false
            sourceButton.menu = nil
            lineButton.isHidden = true
            lineButton.menu = nil
            return
        }

        var actions: [UIMenuElement] = snapshot.sourceOptions.map { opt in
            UIAction(
                title: opt.displayTitle,
                state: opt.id == snapshot.sourceSelectedID ? .on : .off
            ) { [weak model] _ in
                model?.browseSource(opt.id)
            }
        }
        if snapshot.sourceCanLoadMore || snapshot.sourceLoadingMore {
            actions.append(UIAction(
                title: snapshot.sourceLoadingMore ? "正在搜索站点源…" : "加载更多",
                attributes: snapshot.sourceLoadingMore ? .disabled : []
            ) { [weak model] _ in
                model?.loadMoreSources()
            })
        }
        actions.append(UIMenu(options: .displayInline, children: [
            UIAction(title: "刷新目录", identifier: .init("film.detail.refresh")) { [weak model] _ in
                model?.refreshBrowsing()
            },
            UIAction(title: "重试地址", identifier: .init("film.detail.retry-media"),
                     attributes: model?.canRetryMedia == true ? [] : .disabled) { [weak model] _ in
                model?.retryMedia()
            }
        ]))
        sourceButton.menu = UIMenu(title: "来源", children: actions)

        let selectedTitle = snapshot.sourceOptions.first(where: { $0.id == snapshot.sourceSelectedID })?.displayTitle ??
            snapshot.sourcePlaceholder
        sourceButton.setTitle(selectedTitle, for: .normal)
        sourceButton.accessibilityValue = selectedTitle
        sourceButton.isEnabled = !snapshot.sourceDisabled

        lineButton.isHidden = !snapshot.lineVisible
        if snapshot.lineVisible {
            let lineTitle = snapshot.lineOptions.first(where: { $0.id == snapshot.lineSelectedID })?.label ??
                snapshot.lineOptions.first?.label ?? "线路"
            lineButton.setTitle(lineTitle, for: .normal)
            lineButton.isEnabled = !snapshot.lineOptions.isEmpty
            lineButton.menu = snapshot.lineOptions.isEmpty ? nil : UIMenu(title: "线路", children: snapshot.lineOptions.map { line in
                UIAction(title: line.label, state: line.id == snapshot.lineSelectedID ? .on : .off) { [weak model] _ in
                    model?.browseLine(line.id)
                }
            })
        } else {
            lineButton.menu = nil
        }
    }

    func renderRuntimeEpisodes() {
        guard let snapshot = model?.presentation else { return }
        let seasons = snapshot.directorySeasons
        let selectedSeason = snapshot.directorySelectedSeason
        let currentSeasonLabel = seasons.first(where: { $0.season == selectedSeason })?.label
        let baseTitle = currentSeasonLabel ??
            (snapshot.directoryMode == "movies" ? "正片" : snapshot.directoryMode == "files" ? "文件" : "集数")
        let selectable = seasons.count > 1
        seasonButton.setTitle(baseTitle + (selectable ? "  ﹀" : ""), for: .normal)
        seasonButton.isUserInteractionEnabled = selectable
        seasonButton.accessibilityTraits = selectable ? [.header, .button] : .header
        seasonButton.menu = selectable ? UIMenu(title: "选择季度", children: seasons.compactMap { s in
            guard let num = s.season else { return nil }
            return UIAction(title: s.label, state: num == selectedSeason ? .on : .off) { [weak model] _ in
                model?.browseSeason(num)
            }
        }) : nil

        if snapshot.directoryLoading && snapshot.directoryItems.isEmpty {
            episodesMoreContainer.isHidden = true
            showEpisodeState("加载中")
            return
        }
        if snapshot.directoryError != nil && snapshot.directoryItems.isEmpty {
            episodesMoreContainer.isHidden = true
            showEpisodeState(nil)
            return
        }

        let key = EpisodeDirectoryKey(
            source: snapshot.sourceSelectedID,
            line: snapshot.lineSelectedID,
            season: selectedSeason
        )
        if episodeDirectory != key {
            if let previous = episodeDirectory { episodeLayouts[previous] = episodeListVisible }
            episodeListVisible = episodeLayouts[key] ?? false
        }

        let available = model.map { $0.canSelectFromPresentation && !$0.directoryHasNoVideoSource } ?? false
        let fallbackArt = detail.item.posterURL ?? detail.item.largePosterURL
        var items: [FilmEpisodeListItem] = []
        items.reserveCapacity(snapshot.directoryItems.count)
        for row in snapshot.directoryItems {
            let ep = FilmPlaybackPresentation.episode(
                from: row,
                mode: snapshot.directoryMode,
                fallbackArtworkURL: fallbackArt
            )
            let itemID = row.id
            var handler: (@MainActor () -> Void)? = nil
            if row.isPlayable {
                handler = { [weak self] in
                    self?.model?.selectItem(itemID)
                    self?.onPresentPlayback?()
                }
            }
            items.append(FilmEpisodeListItem(
                id: itemID,
                episode: ep,
                accessibilityID: "film.item.\(itemID)",
                enabled: available && handler != nil,
                onSelect: handler
            ))
        }

        if items.isEmpty {
            episodesMoreContainer.isHidden = true
            if snapshot.noSources {
                showEpisodeState("暂无可用视频源")
                searchButton.isHidden = true
            } else {
                showEpisodeState("暂无集数")
                let canSearchMore = snapshot.sourceOptions.isEmpty && (snapshot.sourceCanLoadMore || snapshot.sourceLoadingMore)
                searchButton.isHidden = !canSearchMore
                if canSearchMore {
                    searchButton.configuration?.title = snapshot.sourceLoadingMore ? "搜索中..." : "加载更多"
                    searchButton.isEnabled = !snapshot.sourceLoadingMore
                }
            }
            return
        }

        applyEpisodeItems(items, directory: key)
        let showMoreEpisodes = !snapshot.noSources && (snapshot.directoryCanLoadMore || snapshot.directoryLoadingMore)
        episodesMoreContainer.isHidden = !showMoreEpisodes
        if showMoreEpisodes {
            episodesMoreButton.configuration?.title = snapshot.directoryLoadingMore ? "加载中..." : "加载更多"
            episodesMoreButton.isEnabled = !snapshot.directoryLoadingMore
        }
    }

    func applyEpisodeItems(_ items: [FilmEpisodeListItem], directory: EpisodeDirectoryKey) {
        let changed = episodeDirectory != directory
        let layoutChanged = shownEpisodeList != episodeListVisible
        if let previous = episodeDirectory, changed || layoutChanged {
            if shownEpisodeList {
                episodeListOffsets[previous] = episodeCollection.contentOffset
            } else {
                episodeOffsets[previous] = episodeCollection.contentOffset
            }
        }
        episodeCollection.isHidden = false
        episodeStateContainer.isHidden = true
        episodeState.isHidden = true
        searchButton.isHidden = true

        let width = max(240, view.safeAreaLayoutGuide.layoutFrame.width)
        episodeCollection.apply(items: items, isList: episodeListVisible, viewportWidth: width, theme: theme)
        if changed || layoutChanged {
            episodeCollection.layoutIfNeeded()
            let targetOffset = episodeListVisible
                ? (episodeListOffsets[directory] ?? .zero)
                : (episodeOffsets[directory] ?? .zero)
            episodeCollection.setContentOffset(targetOffset, animated: false)
        }
        episodeDirectory = directory
        shownEpisodeList = episodeListVisible
    }

    func renderSeason() {
        if model != nil {
            renderRuntimeEpisodes()
            updateEpisodeAvailability()
            renderEpisodeMenu()
            seasonButton.setTitleColor(UIColor(seasonButton.isUserInteractionEnabled ? theme.accent : theme.ink), for: .normal)
            return
        }
        let season = detail.seasons.first { $0.id == selectedSeasonID } ?? detail.seasons.first
        selectedSeasonID = season?.id
        let selectable = detail.seasons.count > 1
        play.isEnabled = false
        play.configuration?.title = "暂无可用视频源"
        play.accessibilityLabel = "播放，暂无可用视频源"
        seasonButton.setTitle((season?.title ?? "集数") + (selectable ? "  ﹀" : ""), for: .normal)
        seasonButton.menu = detail.seasons.isEmpty ? nil : UIMenu(title: "选择季度", children: detail.seasons.map { season in
            UIAction(title: season.title, state: season.id == selectedSeasonID ? .on : .off) { [weak self] _ in
                self?.selectedSeasonID = season.id
                self?.renderSeason()
            }
        })
        seasonButton.isUserInteractionEnabled = selectable
        seasonButton.accessibilityTraits = selectable ? [.header, .button] : .header
        seasonButton.setTitleColor(UIColor(selectable ? theme.accent : theme.ink), for: .normal)

        if let season, !season.episodes.isEmpty {
            let items = season.episodes.map { episode in
                FilmEpisodeListItem(
                    id: episode.id,
                    episode: episode,
                    accessibilityID: "film.episode.\(episode.id)",
                    enabled: false,
                    onSelect: nil
                )
            }
            let key = EpisodeDirectoryKey(source: nil, line: nil, season: season.number)
            applyEpisodeItems(items, directory: key)
        } else {
            showEpisodeState("暂无集数")
        }
        renderEpisodeMenu()
    }

    func playSelection() {
        guard let model, model.canStartPlayback else { return }
        model.send("play")
        onPresentPlayback?()
    }

    func renderEpisodeMenu() {
        let showsDirectory: Bool
        if let snapshot = model?.presentation, snapshot.directoryError == nil,
           snapshot.sourceSelectedID != nil, !snapshot.directoryRawItems.isEmpty {
            showsDirectory = true
        } else {
            showsDirectory = false
        }
        let nextState = EpisodeMenuState(listVisible: episodeListVisible, showsDirectory: showsDirectory)
        guard displayedEpisodeMenuState != nextState else { return }
        displayedEpisodeMenuState = nextState
        var actions: [UIMenuElement] = [
            UIAction(title: "卡片视图", identifier: .init("film.detail.cards"),
                     state: episodeListVisible ? .off : .on) { [weak self] _ in
                self?.episodeListVisible = false; self?.renderSeason()
            },
            UIAction(title: "列表视图", identifier: .init("film.detail.list"),
                     state: episodeListVisible ? .on : .off) { [weak self] _ in
                self?.episodeListVisible = true; self?.renderSeason()
            },
        ]
        if showsDirectory {
            actions.append(UIAction(title: "查看原始列表", identifier: .init("film.detail.directory")) { [weak self] _ in
                self?.showDirectory()
            })
        }
        episodeMore.menu = UIMenu(children: actions)
    }

    func showEpisodeState(_ text: String?) {
        episodeCollection.isHidden = true
        episodesMoreContainer.isHidden = true
        episodeStateContainer.isHidden = text == nil
        episodeState.text = text
        episodeState.isHidden = text == nil
        searchButton.isHidden = true
    }

    func presentLibraryError() {
        syncErrorSource(
            "library",
            message: library.loadError?.localizedDescription,
            token: library.loadError.map { AnyHashable(String(describing: $0)) }
        )
    }

    func presentPlaybackError() {
        let active = model?.executionStage == nil ? nil : model?.snapshot
        let suppress = { (err: FilmPlaybackError?) -> FilmPlaybackError? in
            guard let err else { return nil }
            if err.code == "NO_VIDEO_SOURCES" { return nil }
            if err.code == "NOT_CONFIGURED", active?.noSources == true { return nil }
            return err
        }
        let reported = suppress(active?.reportError.map { FilmPlaybackError(code: $0) })
        let browsing = suppress(active?.directoryError.map { FilmPlaybackError(code: $0) })
        let session = suppress(active?.error.map { FilmPlaybackError(code: $0) })
        let actionErr = suppress(model?.actionError)

        guard let model,
              let error = actionErr ?? model.observationError ?? reported ?? browsing ?? session else {
            clearErrorSource("playback")
            return
        }
        let token = AnyHashable(String(describing: error))
        if actionErr == nil, model.observationError != nil {
            syncErrorSource("playback", message: error.localizedDescription, token: token, retry: { [weak model] in
                model?.reconnect()
            })
        } else if model.presentation == nil {
            syncErrorSource("playback", message: error.localizedDescription, token: token, retry: { [weak model] in
                model?.open()
            })
        } else if model.restoringRead {
            syncErrorSource("playback", message: error.localizedDescription, token: token, retry: { [weak model] in
                model?.refreshBrowsing()
            })
        } else if actionErr == nil, model.observationError == nil, reported == nil, browsing != nil {
            syncErrorSource("playback", message: error.localizedDescription, token: token, retry: { [weak model] in
                model?.refreshBrowsing()
            })
        } else {
            syncErrorSource("playback", message: error.localizedDescription, token: token, acknowledged: { [weak self, weak model] in
                self?.clearErrorSource("playback")
                model?.acknowledgeError()
            })
        }
    }
}
#endif
