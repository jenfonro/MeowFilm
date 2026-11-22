#if os(iOS)
import UIKit
import MeowDebug
import MeowUI

@MainActor
extension FilmDetailViewController {
    func showDirectory(animated: Bool = true) {
        guard let model, let browsing = model.presentation?.browsing, browsing.error == nil,
              let sourceID = browsing.sourceID, !browsing.originalFiles.isEmpty else { return }
        let page = FilmDirectoryViewController(model: model, sourceID: sourceID, lineID: browsing.lineID,
                                               metadataSourceID: model.presentation?.metadataSourceID, season: browsing.season,
                                               themeProvider: themeProvider)
        page.onPresentPlayback = onPresentPlayback
        navigationController?.pushViewController(page, animated: animated)
    }

    func renderPlayback() {
        guard isViewLoaded, let model else { return }
        debugRenderPlaybackCount += 1
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
        play.isEnabled = canPlay(snapshot)
        play.configuration?.title = model.directoryHasNoVideoSource ? "无可用视频源" : "播放"
        play.accessibilityLabel = model.directoryHasNoVideoSource ? "播放，无可用视频源" : "播放"
        let busy = model.opening || model.playbackStatus.busy
        if busy { loadingIndicator.startAnimating() } else { loadingIndicator.stopAnimating() }
        playbackState.text = model.playbackStatus == .failed ? nil : model.playbackStatus.title
        let metadataChanged = displayedSnapshot == nil || displayedSnapshot?.workInfo != snapshot.workInfo
        let directoryMetadataChanged = displayedSnapshot?.metadata != snapshot.metadata ||
            displayedSnapshot?.metadataCache != snapshot.metadataCache ||
            displayedSnapshot?.metadataSupplement != snapshot.metadataSupplement ||
            displayedSnapshot?.metadataSourceID != snapshot.metadataSourceID ||
            displayedSnapshot?.metadataSources != snapshot.metadataSources
        if metadataChanged {
            let next = FilmPlaybackPresentation.detail(snapshot, original: originalDetail)
            let relatedChanged = detail.cast != next.cast || detail.similarItems != next.similarItems
            if detail != next { detail = next; renderMetadata() }
            if relatedChanged { renderRelated() }
        }
        if displayedSnapshot?.sources != snapshot.sources || displayedSnapshot?.browsing != snapshot.browsing ||
            displayedSnapshot?.searchComplete != snapshot.searchComplete ||
            displayedSnapshot?.entryResolution != snapshot.entryResolution ||
            displayedAggregateProvider != model.aggregateSourceProvider ||
            displayedCanRetryMedia != model.canRetryMedia || directoryMetadataChanged {
            renderSourceMenus(snapshot)
            displayedCanRetryMedia = model.canRetryMedia
        }
        if displayedSnapshot?.episodes != snapshot.episodes || displayedSnapshot?.browsing != snapshot.browsing ||
            displayedSnapshot?.sources != snapshot.sources || displayedSnapshot?.entryResolution != snapshot.entryResolution ||
            displayedSnapshot?.candidates != snapshot.candidates || directoryMetadataChanged {
            renderRuntimeEpisodes()
        }
        updateEpisodeAvailability()
        if displayedPreviewEpisodes != snapshot.previewEpisodes {
            displayedPreviewEpisodes = snapshot.previewEpisodes
            more.menu = UIMenu(children: [
                UIAction(title: "点映", identifier: .init("film.detail.preview"),
                         state: snapshot.previewEpisodes ? .on : .off) { [weak model] _ in
                    guard let model, let current = model.presentation else { return }
                    model.setPreview(!current.previewEpisodes)
                }
            ])
        }
        renderEpisodeMenu()
        seasonButton.setTitleColor(UIColor(seasonButton.isUserInteractionEnabled ? theme.accent : theme.ink), for: .normal)
        displayedSnapshot = snapshot
        displayedAggregateProvider = model.aggregateSourceProvider
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
            lineButton.isHidden = false
            lineButton.setTitle("加载中...", for: .normal)
            lineButton.isEnabled = false; lineButton.menu = nil
            return
        }
        var actions: [UIMenuElement] = snapshot.metadataSources.map { row in
            UIAction(title: "\(row.provider.title) · \(row.title)",
                     state: model?.aggregateSourceProvider == row.provider ? .on : .off) { [weak model] _ in
                model?.browseAggregate(row.provider)
            }
        }
        actions += snapshot.sources.map { row in
            UIAction(title: "\(row.source.name) · \(row.title)",
                     state: row.id == snapshot.browsing.sourceID ? .on : .off) { [weak model] _ in
                model?.browse(sourceID: row.id)
            }
        }
        let waitingForExplicitSiteSearch = snapshot.entryResolution == "unmatched" &&
            snapshot.error != "SOURCE_NOT_FOUND" && snapshot.sources.isEmpty
        if !waitingForExplicitSiteSearch && snapshot.error != "SOURCE_NOT_FOUND" && !snapshot.searchComplete &&
            snapshot.entryResolution != "loading" && snapshot.entryResolution != "error" {
            actions.append(UIAction(title: "加载更多来源") { [weak model] _ in model?.send("search.more") })
        }
        actions.append(UIMenu(options: .displayInline, children: [
            UIAction(title: snapshot.browsing.sourceID == nil ? "刷新资料" : "刷新目录",
                     identifier: .init("film.detail.refresh")) { [weak model] _ in model?.refreshBrowsing() },
            UIAction(title: "重试地址", identifier: .init("film.detail.retry-media"),
                     attributes: model?.canRetryMedia == true ? [] : .disabled) { [weak model] _ in model?.retryMedia() }
        ]))
        sourceButton.menu = UIMenu(title: "来源", children: actions)
        let selectedTitle: String?
        if let sourceID = snapshot.browsing.sourceID {
            selectedTitle = snapshot.sources.first { $0.id == sourceID }.map { "\($0.source.name) · \($0.title)" }
        } else {
            selectedTitle = snapshot.metadataSources.first { $0.provider == model?.aggregateSourceProvider }
                .map { "\($0.provider.title) · \($0.title)" }
        }
        let waiting = snapshot.browsing.loading || snapshot.entryResolution == "loading"
        let emptyTitle = waiting ? "加载中..." : "无可用视频源"
        sourceButton.setTitle(selectedTitle ?? emptyTitle, for: .normal)
        sourceButton.accessibilityValue = selectedTitle ?? emptyTitle
        sourceButton.isEnabled = !snapshot.metadataSources.isEmpty || !snapshot.sources.isEmpty
        lineButton.isHidden = snapshot.browsing.sourceID == nil && snapshot.entryResolution != "loading" &&
            !snapshot.browsing.loading
        lineButton.setTitle(snapshot.browsing.lines.first { $0.id == snapshot.browsing.lineID }?.label ??
            emptyTitle, for: .normal)
        lineButton.isEnabled = !snapshot.browsing.lines.isEmpty
        lineButton.menu = UIMenu(title: "网盘源", children: snapshot.browsing.lines.map { line in
            UIAction(title: line.label, state: line.id == snapshot.browsing.lineID ? .on : .off) { [weak model] _ in
                model?.browse(sourceID: snapshot.browsing.sourceID, lineID: line.id)
            }
        })
    }

    func renderRuntimeEpisodes() {
        guard let snapshot = model?.presentation else { return }
        let browsing = snapshot.browsing
        let manualSearch = snapshot.entryResolution == "unmatched" && snapshot.error != "SOURCE_NOT_FOUND" &&
            browsing.sourceID == nil && snapshot.sources.isEmpty
        let metadataKind = snapshot.metadataSources.first { $0.id == snapshot.metadataSourceID }?.kind
        let seasons = FilmPlaybackPresentation.seasons(snapshot)
        let number = browsing.season ?? snapshot.target.position.season ?? snapshot.episodes.first?.position.season
        let season = seasons.first { $0.number == number }
        let seasonTitle = season?.title ??
            (metadataKind == .tv ? "第一季" : metadataKind == .movie ? "正片" : "文件")
        let metadataActions: [UIMenuElement] = snapshot.metadataSources.map { row in
            UIAction(title: "\(row.provider.title) · \(row.title)",
                     state: row.id == snapshot.metadataSourceID ? .on : .off) { [weak model] _ in
                model?.browseMetadata(row.id)
            }
        }
        seasonButton.setTitle(seasonTitle, for: .normal)
        seasonButton.isUserInteractionEnabled = seasons.count > 1 || snapshot.metadataSources.count > 1
        seasonButton.accessibilityTraits = seasonButton.isUserInteractionEnabled ? [.header, .button] : .header
        let seasonActions: [UIMenuElement] = seasons.compactMap { season in
            guard let number = season.number else { return nil }
            return UIAction(title: season.title, state: number == (browsing.season ?? snapshot.target.position.season) ? .on : .off) { [weak model] _ in
                model?.browseSeason(number)
            }
        }
        var choices = seasonActions
        if !metadataActions.isEmpty {
            choices.insert(UIMenu(title: "资料视图", children: metadataActions), at: 0)
        }
        seasonButton.menu = UIMenu(title: "季度", children: choices)
        if snapshot.entryResolution == "loading" || browsing.loading { showEpisodeState("加载中"); return }
        if snapshot.entryResolution == "error" || browsing.error != nil { showEpisodeState(nil); return }
        if model?.directoryHasNoVideoSource == true {
            showEpisodeState("无可用视频源")
            searchButton.isHidden = !manualSearch
            return
        }

        let key = EpisodeDirectoryKey(
            source: snapshot.sources.first { $0.id == browsing.sourceID }?.businessID ?? browsing.sourceID,
            line: browsing.lineID,
            metadata: snapshot.metadataSources.first { $0.id == snapshot.metadataSourceID }?.businessID ?? snapshot.metadataSourceID,
            season: number
        )
        if episodeDirectory != key {
            if let previous = episodeDirectory { episodeLayouts[previous] = episodeListVisible }
            episodeListVisible = episodeLayouts[key] ?? false
        }

        let available = model.map { $0.canSelectFromPresentation && !$0.directoryHasNoVideoSource } ?? false
        var items: [FilmEpisodeListItem] = []
        if browsing.sourceID != nil {
            var mappedEpisodes: [FilmEpisodePosition: FilmPlaybackEpisode] = [:]
            for episode in snapshot.episodes {
                let coordinate = FilmEpisodePosition(season: episode.position.season, episode: episode.position.episode)
                if mappedEpisodes[coordinate] == nil { mappedEpisodes[coordinate] = episode }
            }
            for file in browsing.files {
                let episode = displayedEpisode(
                    position: file.position, id: file.businessID, name: file.name, target: file.playbackTarget,
                    metadataEpisode: mappedEpisodes[.init(season: file.position.season, episode: file.position.episode)]
                )
                let handler: (@MainActor () -> Void)? = file.candidateID.map { _ in
                    { [weak self] in
                        self?.model?.selectTarget(file.playbackTarget, metadataSourceID: snapshot.metadataSourceID)
                        self?.onPresentPlayback?()
                    }
                }
                items.append(FilmEpisodeListItem(
                    id: episode.id,
                    episode: episode,
                    accessibilityID: "film.file.\(file.id)",
                    enabled: available && handler != nil,
                    onSelect: handler
                ))
            }
        } else if metadataKind == .movie {
            for candidate in snapshot.candidates {
                let episode = FilmEpisode(
                    id: candidate.businessID, title: candidate.name,
                    summary: [candidate.quality, candidate.source.name, candidate.lineLabel]
                        .compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · "),
                    artworkURL: detail.item.posterURL ?? detail.item.largePosterURL,
                    businessID: candidate.businessID, playbackTarget: candidate.playbackTarget
                )
                items.append(FilmEpisodeListItem(
                    id: episode.id,
                    episode: episode,
                    accessibilityID: "film.candidate.\(candidate.id)",
                    enabled: available,
                    onSelect: { [weak self] in
                        self?.model?.selectTarget(candidate.playbackTarget, metadataSourceID: snapshot.metadataSourceID)
                        self?.onPresentPlayback?()
                    }
                ))
            }
        } else if metadataKind == .tv {
            for episode in snapshot.episodes {
                let mapped = FilmPlaybackPresentation.episode(
                    episode, fallbackArtworkURL: episodeArtwork(season: episode.position.season)
                )
                items.append(FilmEpisodeListItem(
                    id: mapped.id,
                    episode: mapped,
                    accessibilityID: "film.episode.\(mapped.id)",
                    enabled: available,
                    onSelect: { [weak self] in
                        self?.model?.selectTarget(episode.playbackTarget, metadataSourceID: snapshot.metadataSourceID)
                        self?.onPresentPlayback?()
                    }
                ))
            }
        }

        if items.isEmpty {
            showEpisodeState("暂无集数")
            return
        }
        applyEpisodeItems(items, directory: key)
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
        play.configuration?.title = "暂无片源"
        play.accessibilityLabel = "播放，暂无可用片源"
        seasonButton.setTitle((season?.title ?? "集数") + (selectable ? "  ﹀" : ""), for: .normal)
        seasonButton.menu = UIMenu(title: "选择季度", children: detail.seasons.map { season in
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
            let key = EpisodeDirectoryKey(source: nil, line: nil, metadata: season.id, season: nil)
            applyEpisodeItems(items, directory: key)
        } else {
            showEpisodeState("暂无集数")
        }
        renderEpisodeMenu()
    }

    func playSelection() {
        guard let model, let snapshot = model.presentation, canPlay(snapshot) else { return }
        if snapshot.browsing.sourceID != nil {
            guard let file = playbackFile(snapshot) else { return }
            model.selectTarget(file.playbackTarget, metadataSourceID: snapshot.metadataSourceID)
        } else {
            let target = snapshot.episodes.first { $0.position == model.snapshot?.target.position }?.playbackTarget ??
                snapshot.episodes.first?.playbackTarget ??
                snapshot.metadataSources.first { $0.id == snapshot.metadataSourceID }?.playbackTarget
            guard let target else { return }
            model.selectTarget(target, metadataSourceID: snapshot.metadataSourceID)
        }
        onPresentPlayback?()
    }

    func playbackFile(_ snapshot: FilmPlaybackSnapshot) -> FilmPlaybackFile? {
        let selected = [
            snapshot.target.value["source"]?.filmObject?["candidateId"]?.filmText,
            snapshot.attempt?.candidate.id,
            snapshot.current?.candidate.id,
        ].compactMap { $0 }.first { id in
            snapshot.browsing.files.contains { $0.candidateID == id }
        }
        return selected.flatMap { id in snapshot.browsing.files.first { $0.candidateID == id } } ??
            snapshot.browsing.files.first(where: { $0.candidateID != nil })
    }

    func canPlay(_ snapshot: FilmPlaybackSnapshot) -> Bool {
        guard model?.canSelectFromPresentation == true, model?.directoryHasNoVideoSource != true else { return false }
        if snapshot.browsing.sourceID != nil {
            return playbackFile(snapshot) != nil
        }
        guard snapshot.metadataCache?.status != .stale,
              let selected = snapshot.metadataSources.first(where: { $0.id == snapshot.metadataSourceID }) else { return false }
        return selected.kind == .movie || selected.kind == .tv && !snapshot.episodes.isEmpty
    }

    private func episodeArtwork(season: Int?) -> URL? {
        model?.presentation.flatMap { FilmPlaybackPresentation.seasons($0).first { $0.number == season }?.posterURL } ??
            detail.item.posterURL ?? detail.item.largePosterURL
    }

    private func displayedEpisode(position: FilmEpisodePosition, id: String, name: String,
                                  target: FilmPlaybackTarget?, metadataEpisode: FilmPlaybackEpisode?) -> FilmEpisode {
        if let episode = metadataEpisode {
            let display = FilmPlaybackPresentation.episode(episode, fallbackArtworkURL: episodeArtwork(season: position.season))
            return .init(id: id, title: display.title, number: display.number, summary: display.summary,
                         artworkURL: display.artworkURL, releaseDate: display.releaseDate,
                         businessID: id, playbackTarget: target)
        }
        let number = position.episode ?? position.globalEpisode
        return FilmEpisode(id: id, title: number == nil ? name : "", number: number,
                           artworkURL: episodeArtwork(season: position.season), businessID: id, playbackTarget: target)
    }

    func renderEpisodeMenu() {
        let showsDirectory: Bool
        if let browsing = model?.presentation?.browsing, browsing.sourceID != nil,
           browsing.error == nil, !browsing.originalFiles.isEmpty {
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
        episodeStateContainer.isHidden = text == nil
        episodeState.text = text
        episodeState.isHidden = text == nil
        searchButton.isHidden = true
    }

    func presentLibraryError() {
        guard let error = library.loadError, error != displayedLibraryError else { return }
        if showError(error.localizedDescription, acknowledged: { [weak self] in
            self?.presentPlaybackError()
        }) { displayedLibraryError = error }
    }

    func presentPlaybackError() {
        let active = model?.executionStage == nil ? nil : model?.snapshot
        let reported = active?.reportError.map { FilmPlaybackError(code: $0) }
        let metadata = active?.metadataError.map { FilmPlaybackError(code: $0) }
        let browsing = active?.browsing.error.map { FilmPlaybackError(code: $0) }
        let session = active?.error.map { FilmPlaybackError(code: $0) }
        let entryError = active?.entryResolution == "error" ? FilmPlaybackError(code: "UPSTREAM_ERROR") : nil
        if model?.actionError == nil, model?.observationError == nil, reported == nil,
           metadata == nil, browsing == nil, session == nil, entryError == nil {
            displayedPlaybackError = nil
        }
        guard let model,
              let error = model.actionError ?? model.observationError ?? reported ?? browsing ?? metadata ?? session ?? entryError,
              error != displayedPlaybackError, canPresentPageAlert else { return }
        let presented: Bool
        if model.actionError == nil, model.observationError != nil {
            presented = showRetryableError(error.localizedDescription, retry: { [weak model] in model?.reconnect() })
        } else if model.presentation == nil {
            presented = showRetryableError(error.localizedDescription, retry: { [weak model] in model?.open() })
        } else if model.restoringRead {
            presented = showRetryableError(error.localizedDescription, retry: { [weak model] in model?.refreshBrowsing() })
        } else if model.actionError == nil, model.observationError == nil, reported == nil,
                  browsing != nil || metadata != nil || model.presentation?.entryResolution == "error" {
            presented = showRetryableError(error.localizedDescription, retry: { [weak model] in model?.refreshBrowsing() })
        } else {
            presented = showError(error.localizedDescription, acknowledged: { [weak model] in model?.acknowledgeError() })
        }
        if presented { displayedPlaybackError = error }
    }
}
#endif
