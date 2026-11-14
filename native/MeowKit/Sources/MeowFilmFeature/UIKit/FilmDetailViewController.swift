#if os(iOS)
import UIKit
import Combine
import MeowUI

struct FilmHeroGeometry {
    /// The poster starts slightly enlarged, so shrinking it never uncovers its fixed frame.
    static let restingZoom: CGFloat = 1.12
    let progress: CGFloat
    let zoom: CGFloat
    let translation: CGFloat

    /// The frame scrolls with the text; `progress` reaches 1 when the title below it meets the header's lower edge.
    init(offset: CGFloat, height: CGFloat, headerHeight: CGFloat, reduceMotion: Bool) {
        progress = min(1, max(0, offset / max(1, height - headerHeight)))
        zoom = reduceMotion ? 1 : Self.restingZoom - (Self.restingZoom - 1) * progress
        translation = -max(0, offset)
    }

    /// The header stays clear while poster art is under it and fades in over the last fifth of the approach
    /// (0.8...1 maps to 0...1, reaching exactly 1 on arrival).
    var headerAlpha: CGFloat { min(1, max(0, 5 * progress - 4)) }
}

@MainActor
public final class FilmDetailViewController: PageViewController, UIScrollViewDelegate, FilmPlaybackPage {
    public var onPresentPlayback: (() -> Void)?
    public var onSelectItem: ((FilmItem) -> Void)?
    public private(set) var detail: FilmDetail
    public private(set) var searchContext: FilmSearchContext?
    public var onSelectRelatedItem: ((FilmItem, FilmPlaybackKind) -> Void)?
    public var onActivatePlayback: (() -> Void)?
    public var filmPlaybackModel: FilmPlaybackModel? { model }
    private let originalDetail: FilmDetail
    private let model: FilmPlaybackModel?
    private let library: FilmLibrary
    private let images: ArtworkImages
    private let scroll = FilmTouchScrollView(), content = UIStackView()
    private let heroClip = UIView()
    private let heroMask = CAGradientLayer()
    private let heroScrim = FilmHeroScrimView()
    private var heroCandidates: [URL] = []
    private var displayedHeroURLs: [URL] = []
    private var usesLightChrome = false
    private let poster: ArtworkView, logo: ArtworkView
    private let heroSpace = UIView()
    private let titleLabel = UILabel(), metadata = UILabel(), dateLabel = UILabel(), summary = UILabel()
    private var dateContainer: UIView?
    private let play = UIButton(type: .system), favorite = UIButton(type: .custom)
    private let seasonButton = UIButton(type: .system), summaryButton = UIButton(type: .system)
    private let sourceButton = UIButton(type: .system), lineButton = UIButton(type: .system)
    private let searchButton = UIButton(type: .system)
    private let episodeState = UILabel(), playbackState = UILabel()
    private let loadingIndicator = UIActivityIndicatorView(style: .medium)
    private let sourceRow = UIStackView(), episodeList = UIStackView()
    private var displayedSnapshot: FilmPlaybackSnapshot?
    private var playbackActivationRequested = false
    private var summaryExpanded = false
    private var episodeListVisible = false
    private var episodeListContainer = UIView()
    private var episodeStateContainer = UIView()
    private let episodeRail = FilmDetailRail(), castRail = FilmDetailRail(), similarRail = FilmDetailRail()
    private var selectedSeasonID: String?
    private var heroSpaceHeight: NSLayoutConstraint!
    private var episodeViews: [FilmEpisodeButton] = []
    private var castViews: [FilmCastView] = []
    private var similarViews: [FilmRelatedButton] = []
    private var sectionTitles: [UILabel] = []
    private var emptyLabels: [UILabel] = []
    private var logoHeight: NSLayoutConstraint!
    private var titleContainer = UIView(), logoContainer = UIView()
    private lazy var back = PageHeaderButton(image: PageIcon.image(.back, size: 20),
        accessibilityLabel: "返回") { [weak self] in
            (self?.navigationController as? PageNavigationController)?.requestBack()
        }
    private lazy var more = PageHeaderButton(image: UIImage(systemName: "ellipsis")!,
        accessibilityLabel: "更多", menu: UIMenu(children: []))
    private lazy var episodeMore = PageHeaderButton(image: UIImage(systemName: "ellipsis")!,
        accessibilityLabel: "选集视图", menu: UIMenu(children: []))

    public init(detail: FilmDetail, library: FilmLibrary, images: ArtworkImages,
                themeProvider: any PageThemeProviding, model: FilmPlaybackModel? = nil) {
        self.detail = detail
        originalDetail = detail
        self.model = model
        self.library = library
        self.images = images
        poster = ArtworkView(size: 0, images: images)
        logo = ArtworkView(size: 0, images: images)
        selectedSeasonID = detail.seasons.first?.id
        super.init(themeProvider: themeProvider)
        pageTitle = detail.title
    }

    public convenience init(searchContext: FilmSearchContext, library: FilmLibrary, images: ArtworkImages,
                            themeProvider: any PageThemeProviding, model: FilmPlaybackModel? = nil) {
        self.init(detail: searchContext.detail, library: library, images: images, themeProvider: themeProvider, model: model)
        self.searchContext = searchContext
        observe(searchContext.session.$snapshot.receive(on: RunLoop.main).eraseToAnyPublisher()) { [weak self] _ in
            self?.receiveSearchUpdate()
        }
    }

    private func receiveSearchUpdate() {
        guard let searchContext, model?.snapshot == nil else { return }
        detail = searchContext.detail
        pageTitle = detail.title
        guard isViewLoaded else { return }
        renderMetadata()
        renderRelated()
        applyTheme()
    }

    public override var preferredStatusBarStyle: UIStatusBarStyle {
        usesLightChrome ? .lightContent : .default
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.detail"
        back.accessibilityIdentifier = "page.header.back"
        pageHeader.setLeading(back)
        more.accessibilityIdentifier = "film.detail.more"
        more.isHidden = model == nil
        pageHeader.setTrailing([more])
        heroMask.colors = [UIColor.black.cgColor, UIColor.black.cgColor, UIColor.clear.cgColor]
        heroMask.locations = [0, 0.5, 1]
        heroClip.clipsToBounds = true
        heroClip.layer.mask = heroMask
        poster.layer.cornerRadius = 0
        poster.accessibilityIdentifier = "film.detail.hero"
        poster.backgroundColor = .clear
        poster.onImage = { [weak self] _, _ in self?.updateHero() }
        poster.onFailure = { [weak self] _ in self?.loadNextHeroCandidate() }
        heroClip.addSubview(poster)
        view.addSubview(heroClip)
        heroScrim.accessibilityIdentifier = "film.detail.scrim"
        view.addSubview(heroScrim)
        for url in [detail.backdropURL, detail.item.largePosterURL, detail.item.posterURL]
            .compactMap({ $0 }) where !heroCandidates.contains(url) {
            heroCandidates.append(url)
        }
        displayedHeroURLs = heroCandidates
        loadNextHeroCandidate()
        scroll.accessibilityIdentifier = "film.detail.scroll"
        scroll.contentInsetAdjustmentBehavior = .never
        scroll.alwaysBounceVertical = true
        scroll.delaysContentTouches = false
        scroll.delegate = self
        view.addSubview(scroll)
        scroll.pinEdges(to: view)
        content.axis = .vertical
        content.alignment = .fill
        content.spacing = 0
        scroll.addSubview(content)
        content.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            content.topAnchor.constraint(equalTo: scroll.contentLayoutGuide.topAnchor),
            content.bottomAnchor.constraint(equalTo: scroll.contentLayoutGuide.bottomAnchor),
            content.leadingAnchor.constraint(equalTo: scroll.contentLayoutGuide.leadingAnchor),
            content.trailingAnchor.constraint(equalTo: scroll.contentLayoutGuide.trailingAnchor),
            content.widthAnchor.constraint(equalTo: scroll.frameLayoutGuide.widthAnchor),
        ])
        heroSpaceHeight = heroSpace.heightAnchor.constraint(equalToConstant: 240)
        heroSpaceHeight.isActive = true
        content.addArrangedSubview(heroSpace)
        configureTitle()
        configureActions()
        configureSections()
        observe(library) { [weak self] in self?.renderFavorite() }
        if let model {
            observe(model) { [weak self] in self?.renderPlayback() }
            renderPlayback()
        }
        renderFavorite()
        applyTheme()
    }

    public override func pageDidBecomeVisible() {
        activatePlaybackIfVisible()
        presentLibraryError()
        presentPlaybackError()
    }

    private func activatePlaybackIfVisible() {
        guard model != nil, !playbackActivationRequested, canPresentPageAlert else { return }
        playbackActivationRequested = true
        if let onActivatePlayback { onActivatePlayback() } else { model?.open() }
        if model?.snapshot != nil { model?.reconnect() }
    }

    public override func pageDidBecomeHidden() { playbackActivationRequested = false }

    public override func pageDidBecomeInactive() {
        guard let model else { return }
        Task { await model.close(allowSystemOwner: true) }
    }

    public override func pageDidFinishAppearing() {
        super.pageDidFinishAppearing()
        activatePlaybackIfVisible()
        presentLibraryError()
        presentPlaybackError()
    }

    private func configureTitle() {
        titleLabel.text = detail.title
        titleLabel.font = .systemFont(ofSize: 32)
        titleLabel.numberOfLines = 2
        titleLabel.textAlignment = .center
        titleLabel.accessibilityIdentifier = "film.detail.title"
        titleLabel.accessibilityTraits = .header
        titleContainer = inset(titleLabel, horizontal: 24)
        content.addArrangedSubview(titleContainer)
        content.setCustomSpacing(16, after: content.arrangedSubviews.last!)
        logo.contentMode = .scaleAspectFit
        logo.accessibilityIdentifier = "film.detail.logo"
        logo.backgroundColor = .clear
        logo.layer.cornerRadius = 0
        logo.isHidden = detail.logoURL == nil
        logoHeight = logo.heightAnchor.constraint(equalToConstant: 72)
        logoHeight.isActive = true
        logoContainer = inset(logo, horizontal: 56)
        logoContainer.isHidden = true
        content.insertArrangedSubview(logoContainer, at: 1)
        logo.onImage = { [weak self] _, _ in
            self?.titleContainer.isHidden = true
            self?.logoContainer.isHidden = false
        }
        logo.onFailure = { [weak self] _ in
            self?.titleContainer.isHidden = false
            self?.logoContainer.isHidden = true
        }
        if let url = detail.logoURL { logo.load(url) }
        metadata.font = .systemFont(ofSize: 15)
        metadata.numberOfLines = 2
        metadata.textAlignment = .center
        content.addArrangedSubview(inset(metadata, horizontal: 24))
        renderMetadata()
        content.setCustomSpacing(10, after: content.arrangedSubviews.last!)
        dateLabel.text = detail.releaseDate
        dateLabel.font = .systemFont(ofSize: 14)
        dateLabel.textAlignment = .center
        dateLabel.isHidden = detail.releaseDate?.isEmpty != false
        let date = inset(dateLabel, horizontal: 24)
        dateContainer = date
        date.isHidden = dateLabel.isHidden
        content.addArrangedSubview(date)
        content.setCustomSpacing(24, after: date)
    }

    private func configureActions() {
        var configuration = UIButton.Configuration.filled()
        configuration.title = "播放"
        configuration.image = PageIcon.image(.play, size: 16)
        configuration.imagePadding = 8
        configuration.cornerStyle = .capsule
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var attributes = $0
            attributes.font = .systemFont(ofSize: 17)
            return attributes
        }
        play.configuration = configuration
        play.accessibilityIdentifier = "film.detail.play"
        play.heightAnchor.constraint(equalToConstant: 48).isActive = true
        play.addAction(UIAction { [weak self] _ in self?.playSelection() }, for: .touchUpInside)
        let playContainer = inset(play, horizontal: 44)
        content.addArrangedSubview(playContainer)
        content.setCustomSpacing(10, after: playContainer)
        playbackState.font = .systemFont(ofSize: 14)
        playbackState.textAlignment = .center; playbackState.numberOfLines = 2
        playbackState.accessibilityIdentifier = "film.detail.playback-state"
        let status = UIStackView(arrangedSubviews: [loadingIndicator, playbackState])
        status.alignment = .center; status.spacing = 8
        let statusContainer = inset(status, horizontal: 24)
        statusContainer.isHidden = model == nil
        content.addArrangedSubview(statusContainer)
        content.setCustomSpacing(12, after: statusContainer)
        favorite.titleLabel?.font = .systemFont(ofSize: 12)
        favorite.accessibilityIdentifier = "film.detail.favorite"
        favorite.heightAnchor.constraint(equalToConstant: 48).isActive = true
        favorite.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            do { try self.library.toggle(self.detail.item) }
            catch { self.showError(error.localizedDescription) }
            self.renderFavorite()
        }, for: .touchUpInside)
        let favoriteContainer = UIView()
        favoriteContainer.addSubview(favorite)
        favorite.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            favorite.centerXAnchor.constraint(equalTo: favoriteContainer.centerXAnchor),
            favorite.topAnchor.constraint(equalTo: favoriteContainer.topAnchor),
            favorite.bottomAnchor.constraint(equalTo: favoriteContainer.bottomAnchor),
            favorite.widthAnchor.constraint(greaterThanOrEqualToConstant: 76),
        ])
        content.addArrangedSubview(favoriteContainer)
        content.setCustomSpacing(22, after: favoriteContainer)
        summary.text = detail.summary.isEmpty ? "暂无简介" : detail.summary
        summary.font = .systemFont(ofSize: 15)
        summary.numberOfLines = 2
        summary.accessibilityIdentifier = "film.detail.summary"
        summaryButton.setTitle("展开", for: .normal)
        summaryButton.titleLabel?.font = .systemFont(ofSize: 14)
        summaryButton.accessibilityIdentifier = "film.detail.summary-toggle"
        summaryButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        summaryButton.isHidden = detail.summary.isEmpty
        summaryButton.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            summaryExpanded.toggle()
            summary.numberOfLines = summaryExpanded ? 0 : 2
            summaryButton.setTitle(summaryExpanded ? "收起" : "展开", for: .normal)
            summaryButton.accessibilityValue = summaryExpanded ? "已展开" : "已收起"
        }, for: .touchUpInside)
        summaryButton.contentHorizontalAlignment = .leading
        let summaryContent = UIStackView(arrangedSubviews: [summary, summaryButton])
        summaryContent.axis = .vertical
        let summaryContainer = inset(summaryContent)
        content.addArrangedSubview(summaryContainer)
        content.setCustomSpacing(26, after: summaryContainer)
    }

    private func configureSections() {
        configureSources()
        seasonButton.contentHorizontalAlignment = .leading
        seasonButton.titleLabel?.font = .systemFont(ofSize: 21)
        seasonButton.accessibilityIdentifier = "film.detail.season"
        seasonButton.heightAnchor.constraint(equalToConstant: 44).isActive = true
        seasonButton.showsMenuAsPrimaryAction = true
        episodeMore.accessibilityIdentifier = "film.detail.episodes-more"
        let seasonRow = UIStackView(arrangedSubviews: [seasonButton, episodeMore])
        seasonRow.alignment = .center; seasonRow.spacing = 8
        let seasonTitle = inset(seasonRow)
        content.addArrangedSubview(seasonTitle)
        content.setCustomSpacing(6, after: seasonTitle)
        content.addArrangedSubview(episodeRail)
        episodeRail.accessibilityIdentifier = "film.detail.episode-cards"
        episodeList.axis = .vertical; episodeList.spacing = 16
        episodeList.accessibilityIdentifier = "film.detail.episode-list"
        episodeListContainer = inset(episodeList)
        episodeListContainer.isHidden = true
        content.addArrangedSubview(episodeListContainer)
        episodeState.font = .systemFont(ofSize: 14)
        episodeState.textAlignment = .center; episodeState.numberOfLines = 0
        episodeState.accessibilityIdentifier = "film.detail.episodes-state"
        episodeState.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        var searchConfiguration = UIButton.Configuration.tinted()
        searchConfiguration.title = "搜索剧集"
        searchConfiguration.cornerStyle = .capsule
        searchButton.configuration = searchConfiguration
        searchButton.accessibilityIdentifier = "film.detail.search"
        searchButton.isHidden = true
        searchButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        searchButton.addAction(UIAction { [weak model] _ in model?.send("search.more") }, for: .touchUpInside)
        let episodeStatus = UIStackView(arrangedSubviews: [episodeState, searchButton])
        episodeStatus.axis = .vertical; episodeStatus.alignment = .center; episodeStatus.spacing = 8
        episodeStateContainer = inset(episodeStatus)
        episodeStateContainer.isHidden = model == nil
        content.addArrangedSubview(episodeStateContainer)
        content.setCustomSpacing(26, after: episodeStateContainer)
        content.setCustomSpacing(26, after: episodeRail)
        content.setCustomSpacing(26, after: episodeListContainer)
        renderSeason()
        addHeading("演职人员")
        content.addArrangedSubview(castRail)
        if detail.cast.isEmpty {
            addEmpty("暂无演职人员", to: castRail)
        } else {
            for member in detail.cast {
                let view = FilmCastView(member: member, images: images)
                castRail.append(view, width: 86)
                castViews.append(view)
            }
        }
        content.setCustomSpacing(26, after: castRail)
        addHeading("相似作品")
        content.addArrangedSubview(similarRail)
        if detail.similarItems.isEmpty {
            addEmpty("暂无相似作品", to: similarRail)
        } else {
            for item in detail.similarItems {
                let button = FilmRelatedButton(item: item, images: images)
                button.addAction(UIAction { [weak self] _ in self?.onSelectItem?(item) }, for: .touchUpInside)
                similarRail.append(button, width: 88)
                similarViews.append(button)
            }
        }
        let bottom = UIView()
        bottom.heightAnchor.constraint(equalToConstant: 30).isActive = true
        content.addArrangedSubview(bottom)
    }

    private func renderSeason() {
        if model != nil { renderRuntimeEpisodes(); renderEpisodeMenu(); return }
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
        resetEpisodeViews()
        if let season, !season.episodes.isEmpty {
            for episode in season.episodes {
                let button = FilmEpisodeButton(episode: episode, images: images, list: episodeListVisible)
                button.isEnabled = false
                appendEpisode(button)
            }
        } else {
            showEpisodeState("暂无集数")
        }
        renderEpisodeMenu()
        applyTheme()
    }

    private func playSelection() {
        guard let model, let snapshot = model.snapshot, canPlay(snapshot) else { return }
        if snapshot.browsing.sourceID != nil {
            guard let id = playbackCandidateID(snapshot) else { return }
            model.selectCandidate(id)
        } else if let season = snapshot.browsing.season, season != snapshot.target.position.season,
                  snapshot.target.kind != .movie {
            guard let episode = snapshot.episodes.first else { return }
            model.selectEpisode(episode.position)
        } else { model.send("play") }
        onPresentPlayback?()
    }

    private func playbackCandidateID(_ snapshot: FilmPlaybackSnapshot) -> String? {
        let selected = [
            snapshot.target.value["source"]?.filmObject?["candidateId"]?.filmText,
            snapshot.attempt?.candidate.id,
            snapshot.current?.candidate.id,
        ].compactMap { $0 }.first { id in
            snapshot.browsing.originalFiles.contains { $0.candidateID == id }
        }
        return selected ?? snapshot.browsing.files.first(where: { $0.candidateID != nil })?.candidateID
    }

    private func canPlay(_ snapshot: FilmPlaybackSnapshot) -> Bool {
        guard !(snapshot.browsing.loading && !snapshot.browsing.refreshing), snapshot.browsing.error == nil else { return false }
        if snapshot.browsing.sourceID != nil {
            return playbackCandidateID(snapshot) != nil
        }
        if snapshot.metadataCache?.status == .stale { return false }
        if snapshot.target.kind == .group, snapshot.target.value["members"]?.filmArray?.isEmpty != false { return false }
        if let season = snapshot.browsing.season, season != snapshot.target.position.season,
           snapshot.target.kind != .movie { return !snapshot.episodes.isEmpty }
        return true
    }

    private func configureSources() {
        sourceRow.axis = .horizontal; sourceRow.spacing = 8; sourceRow.distribution = .fillEqually
        sourceRow.isHidden = model == nil
        for (button, title, id) in [(sourceButton, "站点源", "source"), (lineButton, "网盘源", "line")] {
            button.setTitle(title, for: .normal)
            button.accessibilityLabel = title
            button.titleLabel?.font = .systemFont(ofSize: 14)
            button.titleLabel?.lineBreakMode = .byTruncatingTail
            button.accessibilityIdentifier = "film.detail.\(id)"
            button.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
            sourceRow.addArrangedSubview(button)
        }
        sourceButton.showsMenuAsPrimaryAction = true; lineButton.showsMenuAsPrimaryAction = true
        let sourceContainer = inset(sourceRow)
        sourceContainer.isHidden = model == nil
        content.addArrangedSubview(sourceContainer)
        content.setCustomSpacing(8, after: sourceContainer)
    }

    private func showDirectory() {
        guard let model, let browsing = model.snapshot?.browsing, browsing.error == nil,
              let sourceID = browsing.sourceID, !browsing.originalFiles.isEmpty else { return }
        let page = FilmDirectoryViewController(model: model, sourceID: sourceID, lineID: browsing.lineID,
                                               themeProvider: themeProvider)
        page.onPresentPlayback = onPresentPlayback
        navigationController?.pushViewController(page, animated: true)
    }

    private func renderPlayback() {
        guard isViewLoaded, let model else { return }
        guard let snapshot = model.snapshot else {
            play.isEnabled = false
            play.configuration?.title = model.opening ? "准备中" : "播放"
            playbackState.text = model.opening ? "加载中" : nil
            if model.opening { loadingIndicator.startAnimating() } else { loadingIndicator.stopAnimating() }
            presentPlaybackError()
            return
        }
        play.isEnabled = canPlay(snapshot)
        play.configuration?.title = play.isEnabled ? "播放" : "暂无片源"
        play.accessibilityLabel = play.isEnabled ? "播放" : "播放，暂无可用片源"
        let busy = ["loading", "searching", "resolving", "pending"].contains(snapshot.stage)
        if busy { loadingIndicator.startAnimating() } else { loadingIndicator.stopAnimating() }
        switch snapshot.stage {
        case "prepared": playbackState.text = "地址已准备"
        case "playing": playbackState.text = "播放中"
        case "searching": playbackState.text = "匹配片源"
        case "resolving", "pending": playbackState.text = "准备中"
        case "ended": playbackState.text = "播放结束"
        default: playbackState.text = nil
        }
        let metadataChanged = displayedSnapshot?.metadata != snapshot.metadata ||
            displayedSnapshot?.metadataCache != snapshot.metadataCache ||
            displayedSnapshot?.metadataSupplement != snapshot.metadataSupplement ||
            displayedSnapshot?.target.metadataProvider != snapshot.target.metadataProvider ||
            displayedSnapshot?.target.kind != snapshot.target.kind ||
            displayedSnapshot?.target.value["metadataId"] != snapshot.target.value["metadataId"] ||
            displayedSnapshot?.metadataSourceID != snapshot.metadataSourceID ||
            displayedSnapshot?.metadataSources != snapshot.metadataSources
        if metadataChanged {
            detail = FilmPlaybackPresentation.detail(snapshot, original: originalDetail)
            renderMetadata()
            renderRelated()
        }
        if displayedSnapshot?.sources != snapshot.sources || displayedSnapshot?.browsing != snapshot.browsing ||
            displayedSnapshot?.searchComplete != snapshot.searchComplete ||
            displayedSnapshot?.entryResolution != snapshot.entryResolution ||
            displayedSnapshot?.metadataSources != snapshot.metadataSources || metadataChanged {
            renderSourceMenus(snapshot)
        }
        if displayedSnapshot?.episodes != snapshot.episodes || displayedSnapshot?.browsing != snapshot.browsing ||
            displayedSnapshot?.sources != snapshot.sources || displayedSnapshot?.entryResolution != snapshot.entryResolution ||
            displayedSnapshot?.candidates != snapshot.candidates || metadataChanged {
            renderRuntimeEpisodes()
        }
        more.menu = UIMenu(children: [
            UIAction(title: "点映", identifier: .init("film.detail.preview"),
                     state: snapshot.previewEpisodes ? .on : .off) { [weak model] _ in
                guard let model, let current = model.snapshot else { return }
                model.setPreview(!current.previewEpisodes)
            }
        ])
        renderEpisodeMenu()
        displayedSnapshot = snapshot
        presentPlaybackError()
        applyTheme()
    }

    private func renderMetadata() {
        titleLabel.text = detail.title; pageTitle = detail.title
        var facts: [String] = []
        if let count = detail.seasonCount, count > 0 { facts.append("\(count) 季") }
        if let count = detail.episodeCount, count > 0 { facts.append("\(count) 集") }
        if facts.isEmpty, !detail.item.subtitle.isEmpty { facts.append(detail.item.subtitle) }
        if !detail.item.score.isEmpty { facts.append("评分 \(detail.item.score)") }
        metadata.text = facts.joined(separator: " · "); metadata.isHidden = facts.isEmpty
        metadata.superview?.isHidden = facts.isEmpty
        dateLabel.text = detail.releaseDate; dateLabel.isHidden = detail.releaseDate?.isEmpty != false
        dateLabel.superview?.isHidden = dateLabel.isHidden
        summary.text = detail.summary.isEmpty ? "暂无简介" : detail.summary
        summaryButton.isHidden = detail.summary.isEmpty
        var candidates: [URL] = []
        for url in [detail.backdropURL, detail.item.largePosterURL, detail.item.posterURL]
            .compactMap({ $0 }) where !candidates.contains(url) { candidates.append(url) }
        if candidates != displayedHeroURLs {
            displayedHeroURLs = candidates
            heroCandidates = candidates
            if candidates.isEmpty { poster.load(nil) } else { loadNextHeroCandidate() }
        }
        logo.isHidden = detail.logoURL == nil
        logo.load(detail.logoURL)
        titleContainer.isHidden = logo.image != nil
        logoContainer.isHidden = logo.image == nil
    }

    private func renderRelated() {
        castRail.removeAll(); similarRail.removeAll(); castViews.removeAll(); similarViews.removeAll()
        if detail.cast.isEmpty { addEmpty("暂无演职人员", to: castRail) }
        for member in detail.cast {
            let view = FilmCastView(member: member, images: images)
            castRail.append(view, width: 86); castViews.append(view)
        }
        if detail.similarItems.isEmpty { addEmpty("暂无相似作品", to: similarRail) }
        for item in detail.similarItems {
            let button = FilmRelatedButton(item: item, images: images)
            button.addAction(UIAction { [weak self] _ in
                guard let self else { return }
                if let kind = item.kind, let onSelectRelatedItem {
                    onSelectRelatedItem(item, kind == .movie ? .movie : .tv)
                } else if let kind = model?.snapshot?.target.kind, (kind == .movie || kind == .tv),
                          let onSelectRelatedItem {
                    onSelectRelatedItem(item, kind)
                } else { onSelectItem?(item) }
            }, for: .touchUpInside)
            similarRail.append(button, width: 88); similarViews.append(button)
        }
        emptyLabels.removeAll { $0.superview == nil }
    }

    private func renderSourceMenus(_ snapshot: FilmPlaybackSnapshot) {
        var actions: [UIMenuElement] = snapshot.metadataSources.map { row in
            UIAction(title: "\(row.provider.title) · \(row.title)",
                     state: snapshot.browsing.sourceID == nil && row.id == snapshot.metadataSourceID ? .on : .off) { [weak model] _ in
                model?.browseMetadata(row.id)
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
            UIAction(title: "重试地址", identifier: .init("film.detail.retry-media")) { [weak model] _ in model?.retryMedia() }
        ]))
        sourceButton.menu = UIMenu(title: "来源", children: actions)
        sourceButton.setTitle(snapshot.sources.first { $0.id == snapshot.browsing.sourceID }?.source.name ??
                              snapshot.metadataSources.first { $0.id == snapshot.metadataSourceID }?.provider.title ??
                              snapshot.target.metadataProvider?.title ?? "站点源",
                              for: .normal)
        lineButton.isHidden = snapshot.browsing.sourceID == nil
        lineButton.setTitle(snapshot.browsing.lines.first { $0.id == snapshot.browsing.lineID }?.label ?? "网盘源", for: .normal)
        lineButton.menu = UIMenu(title: "网盘源", children: snapshot.browsing.lines.map { line in
            UIAction(title: line.label, state: line.id == snapshot.browsing.lineID ? .on : .off) { [weak model] _ in
                model?.browse(sourceID: snapshot.browsing.sourceID, lineID: line.id)
            }
        })
    }

    private func renderRuntimeEpisodes() {
        guard let snapshot = model?.snapshot else { return }
        resetEpisodeViews()
        let browsing = snapshot.browsing
        let seasons = detail.seasons
        let number = browsing.season ?? snapshot.target.position.season ?? snapshot.episodes.first?.position.season
        let season = seasons.first { $0.number == number }
        let seasonTitle = season?.title ??
            (snapshot.target.kind == .tv ? "第一季" : snapshot.target.kind == .movie ? "正片" : "文件")
        let metadataActions: [UIMenuElement] = snapshot.metadataSources.map { row in
            UIAction(title: "\(row.provider.title) · \(row.title)",
                     state: row.id == snapshot.metadataSourceID ? .on : .off) { [weak model] _ in
                model?.browseMetadata(row.id, keepSource: true)
            }
        }
        seasonButton.setTitle(seasonTitle, for: .normal)
        seasonButton.isUserInteractionEnabled = seasons.count > 1 ||
            (browsing.sourceID != nil && snapshot.metadataSources.count > 1)
        seasonButton.accessibilityTraits = seasonButton.isUserInteractionEnabled ? [.header, .button] : .header
        let seasonActions: [UIMenuElement] = seasons.compactMap { season in
            guard let number = season.number else { return nil }
            return UIAction(title: season.title, state: number == (browsing.season ?? snapshot.target.position.season) ? .on : .off) { [weak model] _ in
                model?.browseSeason(number)
            }
        }
        var choices = seasonActions
        if browsing.sourceID != nil, !metadataActions.isEmpty {
            choices.insert(UIMenu(title: "分季资料", children: metadataActions), at: 0)
        }
        seasonButton.menu = UIMenu(title: "季度", children: choices)
        if snapshot.entryResolution == "loading" { showEpisodeState("加载中"); return }
        if snapshot.entryResolution == "error" { showEpisodeState(nil); return }
        if browsing.loading && !browsing.refreshing { showEpisodeState("加载中"); return }
        if browsing.error != nil { showEpisodeState(nil); return }
        if browsing.sourceID != nil {
            for file in browsing.files {
                let button = FilmEpisodeButton(episode: displayedEpisode(position: file.position,
                    id: file.id, name: file.name), images: images, list: episodeListVisible)
                button.accessibilityIdentifier = "film.file.\(file.id)"
                button.isEnabled = file.candidateID != nil
                button.addAction(UIAction { [weak self] _ in
                    guard let self, let id = file.candidateID else { return }
                    model?.selectCandidate(id); onPresentPlayback?()
                }, for: .touchUpInside)
                appendEpisode(button)
            }
        } else if snapshot.target.kind == .movie {
            for candidate in snapshot.candidates {
                let episode = FilmEpisode(id: candidate.id, title: candidate.name,
                    summary: [candidate.quality, candidate.source.name, candidate.lineLabel]
                        .compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · "),
                    artworkURL: detail.item.posterURL ?? detail.item.largePosterURL)
                let button = FilmEpisodeButton(episode: episode, images: images, list: episodeListVisible)
                button.accessibilityIdentifier = "film.candidate.\(candidate.id)"
                button.isEnabled = snapshot.metadataCache?.status != .stale
                button.addAction(UIAction { [weak self] _ in
                    self?.model?.selectCandidate(candidate.id); self?.onPresentPlayback?()
                }, for: .touchUpInside)
                appendEpisode(button)
            }
        } else if snapshot.target.kind == .tv {
            for episode in snapshot.episodes {
                let button = FilmEpisodeButton(episode: FilmPlaybackPresentation.episode(episode,
                    fallbackArtworkURL: episodeArtwork(season: episode.position.season)),
                    images: images, list: episodeListVisible)
                button.isEnabled = snapshot.metadataCache?.status != .stale
                button.addAction(UIAction { [weak self] _ in
                    self?.model?.selectEpisode(episode.position); self?.onPresentPlayback?()
                }, for: .touchUpInside)
                appendEpisode(button)
            }
        }
        if episodeViews.isEmpty {
            let manualSearch = snapshot.entryResolution == "unmatched" && snapshot.error != "SOURCE_NOT_FOUND" &&
                browsing.sourceID == nil && snapshot.sources.isEmpty
            showEpisodeState(manualSearch || !snapshot.sources.isEmpty ? nil :
                snapshot.target.kind == .tv ? "暂无集数" : "暂无片源")
            searchButton.isHidden = !manualSearch
            if manualSearch { episodeStateContainer.isHidden = false }
        }
        episodeRail.resetOffset()
    }

    private func episodeArtwork(season: Int?) -> URL? {
        detail.seasons.first { $0.number == season }?.posterURL ?? detail.item.posterURL ?? detail.item.largePosterURL
    }

    private func displayedEpisode(position: FilmEpisodePosition, id: String, name: String) -> FilmEpisode {
        if let episode = model?.snapshot?.episodes.first(where: {
            $0.position.season == position.season && $0.position.episode == position.episode
        }) {
            return FilmPlaybackPresentation.episode(episode, fallbackArtworkURL: episodeArtwork(season: position.season))
        }
        let number = position.episode ?? position.globalEpisode
        return FilmEpisode(id: id, title: number == nil ? name : "", number: number,
                           artworkURL: episodeArtwork(season: position.season))
    }

    private func resetEpisodeViews() {
        episodeRail.removeAll()
        for view in episodeList.arrangedSubviews { episodeList.removeArrangedSubview(view); view.removeFromSuperview() }
        episodeViews.removeAll()
        emptyLabels.removeAll { $0.superview == nil }
        episodeRail.isHidden = episodeListVisible
        episodeListContainer.isHidden = !episodeListVisible
        episodeStateContainer.isHidden = true
        episodeState.isHidden = true
        searchButton.isHidden = true
    }

    private func appendEpisode(_ button: FilmEpisodeButton) {
        if episodeListVisible { episodeList.addArrangedSubview(button) }
        else { episodeRail.append(button, width: 176) }
        episodeViews.append(button)
    }

    private func renderEpisodeMenu() {
        let viewActions: [UIMenuElement] = [
            UIAction(title: "卡片视图", identifier: .init("film.detail.cards"),
                     state: episodeListVisible ? .off : .on) { [weak self] _ in
                self?.episodeListVisible = false; self?.renderSeason()
            },
            UIAction(title: "列表视图", identifier: .init("film.detail.list"),
                     state: episodeListVisible ? .on : .off) { [weak self] _ in
                self?.episodeListVisible = true; self?.renderSeason()
            },
        ]
        var actions = viewActions
        if let browsing = model?.snapshot?.browsing, browsing.sourceID != nil,
           browsing.error == nil, !browsing.originalFiles.isEmpty {
            actions.append(UIAction(title: "查看原始列表", identifier: .init("film.detail.directory")) { [weak self] _ in
                self?.showDirectory()
            })
        }
        episodeMore.menu = UIMenu(children: actions)
    }

    private func showEpisodeState(_ text: String?) {
        episodeRail.isHidden = true; episodeListContainer.isHidden = true
        episodeStateContainer.isHidden = text == nil
        episodeState.text = text
        episodeState.isHidden = text == nil
        searchButton.isHidden = true
    }

    private var displayedLibraryError: FilmLibraryError?
    private func presentLibraryError() {
        guard let error = library.loadError, error != displayedLibraryError else { return }
        if showError(error.localizedDescription, acknowledged: { [weak self] in
            guard let alert = self?.presentedViewController as? UIAlertController else { return }
            alert.dismiss(animated: true) { [weak self] in self?.presentPlaybackError() }
        }) { displayedLibraryError = error }
    }

    private var displayedPlaybackError: FilmPlaybackError?
    private func presentPlaybackError() {
        let reported = model?.snapshot?.reportError.map { FilmPlaybackError(code: $0) }
        let metadata = model?.snapshot?.metadataError.map { FilmPlaybackError(code: $0) }
        let browsing = model?.snapshot?.browsing.error.map { FilmPlaybackError(code: $0) }
        let session = model?.snapshot?.error.map { FilmPlaybackError(code: $0) }
        if model?.actionError == nil, model?.observationError == nil, reported == nil,
           metadata == nil, browsing == nil, session == nil {
            displayedPlaybackError = nil
        }
        guard let model, let error = model.actionError ?? model.observationError ?? reported ?? browsing ?? metadata ?? session,
              error != displayedPlaybackError, canPresentPageAlert else { return }
        let presented: Bool
        if model.actionError == nil, model.observationError != nil {
            presented = showRetryableError(error.localizedDescription, retry: { [weak model] in model?.reconnect() })
        } else if model.snapshot == nil {
            presented = showRetryableError(error.localizedDescription, retry: { [weak model] in model?.open() })
        } else if model.actionError == nil, model.observationError == nil, reported == nil,
                  browsing != nil || metadata != nil || model.snapshot?.entryResolution == "error" {
            presented = showRetryableError(error.localizedDescription, retry: { [weak model] in model?.refreshBrowsing() })
        } else {
            presented = showError(error.localizedDescription, acknowledged: { [weak model] in model?.acknowledgeError() })
        }
        if presented { displayedPlaybackError = error }
    }

    /// Detail artwork prefers sharper sources and falls back once per failed candidate.
    private func loadNextHeroCandidate() {
        guard !heroCandidates.isEmpty else { return }
        poster.load(heroCandidates.removeFirst())
    }

    private func renderFavorite() {
        let saved = library.contains(detail.item)
        var configuration = UIButton.Configuration.plain()
        configuration.image = PageIcon.image(.heart, filled: saved, size: 23)
        configuration.title = saved ? "已收藏" : "收藏"
        configuration.imagePlacement = .top
        configuration.imagePadding = 4
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var attributes = $0
            attributes.font = .systemFont(ofSize: 12)
            return attributes
        }
        configuration.baseForegroundColor = UIColor(saved ? theme.accent : theme.muted)
        favorite.configuration = configuration
        favorite.accessibilityLabel = (saved ? "取消收藏 " : "收藏 ") + detail.title
        favorite.accessibilityTraits = saved ? [.button, .selected] : .button
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        titleLabel.textColor = UIColor(theme.ink)
        metadata.textColor = UIColor(theme.ink)
        dateLabel.textColor = UIColor(theme.muted)
        summary.textColor = UIColor(detail.summary.isEmpty ? theme.muted : theme.ink)
        play.configuration?.baseBackgroundColor = UIColor(theme.accent)
        play.configuration?.baseForegroundColor = UIColor(theme.onAccent)
        seasonButton.setTitleColor(UIColor(detail.seasons.count > 1 ? theme.accent : theme.ink), for: .normal)
        playbackState.textColor = UIColor(theme.muted)
        episodeState.textColor = UIColor(theme.muted)
        summaryButton.setTitleColor(UIColor(theme.accent), for: .normal)
        searchButton.configuration?.baseForegroundColor = UIColor(theme.accent)
        loadingIndicator.color = UIColor(theme.muted)
        for button in [sourceButton, lineButton] {
            button.setTitleColor(UIColor(theme.ink), for: .normal)
        }
        for label in sectionTitles { label.textColor = UIColor(theme.ink) }
        for label in emptyLabels { label.textColor = UIColor(theme.muted) }
        for button in episodeViews { button.applyTheme(theme) }
        for view in castViews { view.applyTheme(theme) }
        for button in similarViews { button.applyTheme(theme) }
        renderFavorite()
        updateHero()
    }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let width = view.safeAreaLayoutGuide.layoutFrame.width
        let height = min(340, width * 0.59) + view.safeAreaInsets.top + 150
        if heroSpaceHeight.constant != height { heroSpaceHeight.constant = height }
        scroll.contentInset.bottom = view.safeAreaInsets.bottom
        scroll.verticalScrollIndicatorInsets.top = pageHeader.frame.maxY
        scroll.verticalScrollIndicatorInsets.bottom = view.safeAreaInsets.bottom
        heroScrim.frame = CGRect(x: 0, y: 0, width: view.bounds.width,
                                 height: view.safeAreaInsets.top + PageLayoutMetrics.navigation + 24)
        let metrics = FilmPosterMetrics(viewportWidth: width)
        for button in similarViews { similarRail.setWidth(metrics.width, for: button) }
        for button in episodeViews { episodeRail.setWidth(min(250, width * 0.44), for: button) }
        updateHero()
    }

    public func scrollViewDidScroll(_ scrollView: UIScrollView) { updateHero() }

    private func updateHero() {
        guard isViewLoaded, heroSpaceHeight != nil else { return }
        let height = heroSpaceHeight.constant
        let geometry = FilmHeroGeometry(offset: scroll.contentOffset.y, height: height,
                                        headerHeight: view.safeAreaInsets.top + PageLayoutMetrics.navigation,
                                        reduceMotion: UIAccessibility.isReduceMotionEnabled)
        heroClip.frame = CGRect(x: 0, y: geometry.translation, width: view.bounds.width, height: height)
        poster.bounds = heroClip.bounds
        poster.center = CGPoint(x: heroClip.bounds.midX, y: heroClip.bounds.midY)
        poster.transform = CGAffineTransform(scaleX: geometry.zoom, y: geometry.zoom)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        heroMask.frame = heroClip.bounds
        CATransaction.commit()
        let headerAlpha = geometry.headerAlpha
        let posterChrome = hasOwnPageHeader && poster.image != nil
        let lightChrome = posterChrome && headerAlpha < 0.5
        heroScrim.alpha = posterChrome ? 1 - headerAlpha : 0
        if hasOwnPageHeader {
            pageHeader.background.alpha = headerAlpha
            pageHeader.title = headerAlpha > 0.75 ? detail.title : nil
            back.tintColor = lightChrome ? .white : UIColor(theme.ink)
            more.tintColor = lightChrome ? .white : UIColor(theme.ink)
        }
        if usesLightChrome != lightChrome {
            usesLightChrome = lightChrome
            setNeedsStatusBarAppearanceUpdate()
        }
    }

    private func addHeading(_ title: String) {
        let label = UILabel()
        label.text = title
        label.font = .systemFont(ofSize: 21)
        label.accessibilityTraits = .header
        sectionTitles.append(label)
        let container = inset(label)
        content.addArrangedSubview(container)
        content.setCustomSpacing(12, after: container)
    }

    private func addEmpty(_ title: String, to rail: FilmDetailRail) {
        let label = UILabel()
        label.text = title
        label.font = .systemFont(ofSize: 14)
        label.textColor = UIColor(theme.muted)
        label.heightAnchor.constraint(equalToConstant: 36).isActive = true
        rail.append(label, width: 200)
        emptyLabels.append(label)
    }

    private func inset(_ child: UIView, horizontal: CGFloat = 16) -> UIView {
        let container = UIView()
        container.addSubview(child)
        child.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            child.topAnchor.constraint(equalTo: container.topAnchor),
            child.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            child.leadingAnchor.constraint(equalTo: container.safeAreaLayoutGuide.leadingAnchor, constant: horizontal),
            child.trailingAnchor.constraint(equalTo: container.safeAreaLayoutGuide.trailingAnchor, constant: -horizontal),
        ])
        return container
    }
}

/// Darkens only the status bar and header band over poster art and fades out as the header appears.
@MainActor
final class FilmHeroScrimView: UIView {
    override class var layerClass: AnyClass { CAGradientLayer.self }

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        alpha = 0
        (layer as? CAGradientLayer)?.colors = [UIColor.black.withAlphaComponent(0.45).cgColor,
                                               UIColor.black.withAlphaComponent(0).cgColor]
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
#endif
