#if os(iOS)
import UIKit
import Combine
import MeowDebug
import MeowUI

@MainActor
public final class FilmDetailViewController: PageViewController, UIScrollViewDelegate, FilmPlaybackPage {
    struct EpisodeDirectoryKey: Hashable {
        let source: String?
        let line: String?
        let season: Int?
    }
    struct EpisodeMenuState: Equatable {
        let listVisible: Bool
        let showsDirectory: Bool
    }

    public var onPresentPlayback: (() -> Void)?
    public var onSelectItem: ((FilmItem) -> Void)?
    public internal(set) var detail: FilmDetail
    public private(set) var searchContext: FilmSearchContext?
    public var onSelectRelatedItem: ((FilmItem, FilmPlaybackKind) -> Void)?
    public var onActivatePlayback: (() -> Void)?
    public var filmPlaybackModel: FilmPlaybackModel? { model }

    let originalDetail: FilmDetail
    let model: FilmPlaybackModel?
    let library: FilmLibrary
    let images: ArtworkImages
    let scroll = FilmTouchScrollView()
    let content = UIStackView()
    private let heroClip = UIView()
    private let heroMask = CAGradientLayer()
    private let heroScrim = FilmHeroScrimView()
    private var heroCandidates: [URL] = []
    private var displayedHeroURLs: [URL] = []
    private var currentHeroURL: URL?
    private var usesLightChrome = false
    private let poster: ArtworkView
    private let logo: ArtworkView
    private let heroSpace = UIView()
    private let titleLabel = UILabel()
    private let metadata = UILabel()
    private let dateLabel = UILabel()
    private let summary = UILabel()
    private var dateContainer: UIView?
    let play = UIButton(type: .system)
    private let favorite = UIButton(type: .custom)
    private var togglingFavorite = false
    let seasonButton = UIButton(type: .system)
    private let summaryButton = UIButton(type: .system)
    let sourceButton = UIButton(type: .system)
    let lineButton = UIButton(type: .system)
    let searchButton = UIButton(type: .system)
    let episodesMoreButton = UIButton(type: .system)
    var episodesMoreContainer = UIView()
    let episodeState = UILabel()
    let playbackState = UILabel()
    let loadingIndicator = UIActivityIndicatorView(style: .medium)
    private let sourceRow = UIStackView()
    var displayedSnapshot: FilmPlaybackSnapshot?
    var displayedPreviewState: (preview: Bool, canToggle: Bool)?
    var displayedCanRetryMedia: Bool?
    var displayedEpisodeMenuState: EpisodeMenuState?
    var renderedEmptySourceMenus = false
    private var playbackActivationRequested = false
    private let presentationOwner = UUID()
    private var summaryExpanded = false
    var episodeListVisible = false
    var episodeStateContainer = UIView()
    private(set) lazy var episodeCollection = FilmEpisodeCollectionView(images: images, theme: theme)
    private(set) lazy var castRail = FilmCastRailView(images: images, theme: theme)
    private(set) lazy var similarRail = FilmRelatedRailView(images: images, theme: theme)
    private var castHeadingContainer: UIView?
    private var similarHeadingContainer: UIView?
    var selectedSeasonID: String?
    private var heroSpaceHeight: NSLayoutConstraint!
    var episodeDirectory: EpisodeDirectoryKey?
    var episodeOffsets: [EpisodeDirectoryKey: CGPoint] = [:]
    var episodeListOffsets: [EpisodeDirectoryKey: CGPoint] = [:]
    var episodeLayouts: [EpisodeDirectoryKey: Bool] = [:]
    var shownEpisodeList = false
    private var sectionTitles: [UILabel] = []
    private var logoHeight: NSLayoutConstraint!
    private var titleContainer = UIView()
    private var logoContainer = UIView()

    private lazy var back = PageHeaderButton(image: PageIcon.image(.back, size: 20),
        accessibilityLabel: "返回") { [weak self] in
            (self?.navigationController as? PageNavigationController)?.requestBack()
        }
    lazy var more: PageHeaderButton = {
        let button = PageHeaderButton(image: UIImage(systemName: "ellipsis")!, accessibilityLabel: "更多", menu: nil)
        button.isEnabled = false
        return button
    }()
    lazy var episodeMore = PageHeaderButton(image: UIImage(systemName: "ellipsis")!,
        accessibilityLabel: "选集视图", menu: nil)

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
        observe(searchContext.session.$snapshot.dropFirst().eraseToAnyPublisher(), whenVisible: true) { [weak self] _ in
            self?.receiveSearchUpdate()
        }
    }

    private func receiveSearchUpdate() {
        guard let searchContext, model?.presentation == nil else { return }
        detail = searchContext.detail
        pageTitle = detail.title
        guard isViewLoaded else { return }
        renderMetadata()
        renderRelated()
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
        observe(library, whenVisible: true) { [weak self] in self?.renderFavorite() }
        if let model {
            let presentationVisuals = Publishers.MergeMany([
                model.presentationStore.$snapshot.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                model.presentationStore.$presentation.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                model.presentationStore.$opening.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                model.presentationStore.$nativeStatus.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            ]).eraseToAnyPublisher()
            observe(presentationVisuals, whenVisible: true) { [weak self] in
                self?.renderPlayback()
            }
            let presentationErrors = Publishers.MergeMany([
                model.presentationStore.$observationError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                model.presentationStore.$actionError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                model.presentationStore.$mediaStopError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                model.presentationStore.$sessionReleaseError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            ]).eraseToAnyPublisher()
            observe(presentationErrors, whenVisible: true) { [weak self] in
                self?.presentPlaybackError()
            }
            renderPlayback()
        }
        renderFavorite()
        applyTheme()
    }

    public override func pageDidBecomeVisible() {
        model?.setPresentationVisible(true, owner: presentationOwner)
        activatePlaybackIfVisible()
        resumeVisibleArtworks()
        presentLibraryError()
        presentPlaybackError()
    }

    private func activatePlaybackIfVisible() {
        guard model != nil, !playbackActivationRequested, canPresentPageAlert else { return }
        playbackActivationRequested = true
        if let onActivatePlayback { onActivatePlayback() } else { model?.open() }
    }

    public override func pageDidBecomeHidden() {
        model?.setPresentationVisible(false, owner: presentationOwner)
        playbackActivationRequested = false
        cancelVisibleArtworks()
    }

    public override func pageDidBecomeInactive() {
        cancelVisibleArtworks()
        guard let model else { return }
        Task { await model.close(allowSystemOwner: true) }
    }

    private func cancelVisibleArtworks() {
        poster.cancelPendingLoad()
        logo.cancelPendingLoad()
        episodeCollection.cancelVisibleArtworks()
        castRail.cancelVisibleArtworks()
        similarRail.cancelVisibleArtworks()
    }

    private func resumeVisibleArtworks() {
        if let url = currentHeroURL { poster.load(url) }
        if let url = detail.logoURL { logo.load(url) }
        episodeCollection.resumeVisibleArtworks()
        castRail.resumeVisibleArtworks()
        similarRail.resumeVisibleArtworks()
    }

    public override func pageDidFinishAppearing() {
        super.pageDidFinishAppearing()
        activatePlaybackIfVisible()
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
            guard let self, !self.togglingFavorite else { return }
            self.togglingFavorite = true
            let library = self.library, item = self.detail.item
            Task { [weak self] in
                defer { self?.togglingFavorite = false }
                do { try await library.toggle(item) }
                catch { self?.enqueueError(error.localizedDescription) }
                self?.renderFavorite()
            }
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
        content.setCustomSpacing(24, after: favoriteContainer)
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
        content.addArrangedSubview(episodeCollection)
        content.setCustomSpacing(12, after: episodeCollection)

        var moreEpConfig = UIButton.Configuration.tinted()
        moreEpConfig.title = "加载更多"
        moreEpConfig.cornerStyle = .capsule
        moreEpConfig.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var attrs = $0; attrs.font = .systemFont(ofSize: 14); return attrs
        }
        episodesMoreButton.configuration = moreEpConfig
        episodesMoreButton.accessibilityIdentifier = "film.detail.episodes-load-more"
        episodesMoreButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        episodesMoreButton.addAction(UIAction { [weak model] _ in model?.loadMoreEpisodes() }, for: .touchUpInside)
        let moreEpRow = UIStackView(arrangedSubviews: [episodesMoreButton])
        moreEpRow.axis = .vertical; moreEpRow.alignment = .center
        episodesMoreContainer = inset(moreEpRow)
        episodesMoreContainer.isHidden = true
        content.addArrangedSubview(episodesMoreContainer)
        content.setCustomSpacing(26, after: episodesMoreContainer)

        episodeState.font = .systemFont(ofSize: 14)
        episodeState.textAlignment = .center; episodeState.numberOfLines = 0
        episodeState.accessibilityIdentifier = "film.detail.episodes-state"
        episodeState.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        var searchConfiguration = UIButton.Configuration.tinted()
        searchConfiguration.title = "加载更多"
        searchConfiguration.cornerStyle = .capsule
        searchConfiguration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var attrs = $0; attrs.font = .systemFont(ofSize: 14); return attrs
        }
        searchButton.configuration = searchConfiguration
        searchButton.accessibilityIdentifier = "film.detail.search"
        searchButton.isHidden = true
        searchButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        searchButton.addAction(UIAction { [weak model] _ in model?.loadMoreSources() }, for: .touchUpInside)
        let episodeStatus = UIStackView(arrangedSubviews: [episodeState, searchButton])
        episodeStatus.axis = .vertical; episodeStatus.alignment = .center; episodeStatus.spacing = 8
        episodeStateContainer = inset(episodeStatus)
        episodeStateContainer.isHidden = model == nil
        content.addArrangedSubview(episodeStateContainer)
        content.setCustomSpacing(26, after: episodeStateContainer)
        renderSeason()
        castHeadingContainer = addHeading("演职人员")
        content.addArrangedSubview(castRail)
        content.setCustomSpacing(26, after: castRail)
        similarHeadingContainer = addHeading("相似作品")
        similarRail.onSelect = { [weak self] item in
            guard let self else { return }
            if let kind = item.kind, let onSelectRelatedItem {
                onSelectRelatedItem(item, kind == .movie ? .movie : .tv)
            } else if let kind = detail.item.kind, let onSelectRelatedItem {
                onSelectRelatedItem(item, kind == .movie ? .movie : .tv)
            } else { onSelectItem?(item) }
        }
        content.addArrangedSubview(similarRail)
        renderRelated()
        let bottom = UIView()
        bottom.heightAnchor.constraint(equalToConstant: 30).isActive = true
        content.addArrangedSubview(bottom)
    }

    private func configureSources() {
        sourceRow.axis = .horizontal; sourceRow.spacing = 8; sourceRow.distribution = .fillEqually
        sourceRow.isHidden = model == nil
        for (button, title, id) in [(sourceButton, "站点源", "source"), (lineButton, "线路", "line")] {
            button.setTitle(title, for: .normal)
            button.accessibilityLabel = title
            button.titleLabel?.font = .systemFont(ofSize: 14)
            button.titleLabel?.lineBreakMode = .byTruncatingTail
            button.accessibilityIdentifier = "film.detail.\(id)"
            button.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
            sourceRow.addArrangedSubview(button)
        }
        sourceButton.showsMenuAsPrimaryAction = true; lineButton.showsMenuAsPrimaryAction = true
        renderSourceMenus(nil)
        let sourceContainer = inset(sourceRow)
        sourceContainer.isHidden = model == nil
        content.addArrangedSubview(sourceContainer)
        content.setCustomSpacing(8, after: sourceContainer)
    }

    func renderMetadata() {
        titleLabel.text = detail.title; pageTitle = detail.title
        var facts: [String] = []
        if let count = detail.seasonCount, count > 0 {
            facts.append("共\u{00A0}\(count)\u{00A0}季" + (detail.episodeCount.map { "\u{00A0}\($0)\u{00A0}集" } ?? ""))
        } else if let count = detail.episodeCount, count > 0 { facts.append("共\u{00A0}\(count)\u{00A0}集") }
        if facts.isEmpty, !detail.item.subtitle.isEmpty { facts.append(detail.item.subtitle) }
        if !detail.item.score.isEmpty { facts.append("评\u{2060}分\u{00A0}\(detail.item.score)") }
        metadata.text = facts.joined(separator: " · "); metadata.isHidden = facts.isEmpty
        metadata.superview?.isHidden = facts.isEmpty
        dateLabel.text = detail.releaseDate; dateLabel.isHidden = detail.releaseDate?.isEmpty != false
        dateContainer?.isHidden = dateLabel.isHidden
        summary.text = detail.summary.isEmpty ? "暂无简介" : detail.summary
        summary.textColor = UIColor(detail.summary.isEmpty ? theme.muted : theme.ink)
        summaryButton.isHidden = detail.summary.isEmpty
        var candidates: [URL] = []
        for url in [detail.backdropURL, detail.item.largePosterURL, detail.item.posterURL]
            .compactMap({ $0 }) where !candidates.contains(url) { candidates.append(url) }
        if candidates != displayedHeroURLs {
            displayedHeroURLs = candidates
            heroCandidates = candidates
            if candidates.isEmpty {
                currentHeroURL = nil
                poster.load(nil)
            } else {
                loadNextHeroCandidate()
            }
        }
        logo.isHidden = detail.logoURL == nil
        logo.load(detail.logoURL)
        titleContainer.isHidden = logo.image != nil
        logoContainer.isHidden = logo.image == nil
    }

    func renderRelated() {
        let width = max(240, view.safeAreaLayoutGuide.layoutFrame.width)
        let hasCast = !detail.cast.isEmpty
        castHeadingContainer?.isHidden = !hasCast
        castRail.isHidden = !hasCast
        castRail.apply(members: detail.cast, theme: theme)

        let hasSimilar = !detail.similarItems.isEmpty
        similarHeadingContainer?.isHidden = !hasSimilar
        similarRail.isHidden = !hasSimilar
        similarRail.apply(items: detail.similarItems, viewportWidth: width, theme: theme)
    }

    private func loadNextHeroCandidate() {
        guard !heroCandidates.isEmpty else { return }
        let next = heroCandidates.removeFirst()
        currentHeroURL = next
        poster.load(next)
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
        DebugUIMetrics.shared.increment("film.detail.applyTheme")
        titleLabel.textColor = UIColor(theme.ink)
        metadata.textColor = UIColor(theme.ink)
        dateLabel.textColor = UIColor(theme.muted)
        summary.textColor = UIColor(detail.summary.isEmpty ? theme.muted : theme.ink)
        play.configuration?.baseBackgroundColor = UIColor(theme.accent)
        play.configuration?.baseForegroundColor = UIColor(theme.onAccent)
        seasonButton.setTitleColor(UIColor(seasonButton.isUserInteractionEnabled ? theme.accent : theme.ink), for: .normal)
        playbackState.textColor = UIColor(theme.muted)
        episodeState.textColor = UIColor(theme.muted)
        summaryButton.setTitleColor(UIColor(theme.accent), for: .normal)
        searchButton.configuration?.baseForegroundColor = UIColor(theme.accent)
        episodesMoreButton.configuration?.baseForegroundColor = UIColor(theme.accent)
        loadingIndicator.color = UIColor(theme.muted)
        for button in [sourceButton, lineButton] {
            button.setTitleColor(UIColor(theme.ink), for: .normal)
        }
        for label in sectionTitles { label.textColor = UIColor(theme.ink) }
        episodeCollection.applyTheme(theme)
        castRail.applyTheme(theme)
        similarRail.applyTheme(theme)
        renderFavorite()
        updateHero()
    }

    public override func viewSafeAreaInsetsDidChange() {
        super.viewSafeAreaInsetsDidChange()
        updateScrollInsets()
        view.setNeedsLayout()
    }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let width = view.safeAreaLayoutGuide.layoutFrame.width
        let height = min(340, width * 0.59) + view.safeAreaInsets.top + 150
        if heroSpaceHeight.constant != height { heroSpaceHeight.constant = height }
        updateScrollInsets()
        heroScrim.frame = CGRect(x: 0, y: 0, width: view.bounds.width,
                                 height: view.safeAreaInsets.top + PageLayoutMetrics.navigation + 24)
        episodeCollection.updateViewportWidth(width)
        similarRail.updateViewportWidth(width)
        updateHero()
    }

    private func updateScrollInsets() {
        guard isViewLoaded else { return }
        let bottomInset = view.safeAreaInsets.bottom + 24
        if abs(scroll.contentInset.bottom - bottomInset) > 0.5 {
            scroll.contentInset.bottom = bottomInset
        }
        let topIndicator = pageHeader.frame.maxY
        if abs(scroll.verticalScrollIndicatorInsets.top - topIndicator) > 0.5 {
            scroll.verticalScrollIndicatorInsets.top = topIndicator
        }
        if abs(scroll.verticalScrollIndicatorInsets.bottom - bottomInset) > 0.5 {
            scroll.verticalScrollIndicatorInsets.bottom = bottomInset
        }
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

    @discardableResult
    private func addHeading(_ title: String) -> UIView {
        let label = UILabel()
        label.text = title
        label.font = .systemFont(ofSize: 21)
        label.accessibilityTraits = .header
        sectionTitles.append(label)
        let container = inset(label)
        content.addArrangedSubview(container)
        content.setCustomSpacing(12, after: container)
        return container
    }

    func inset(_ child: UIView, horizontal: CGFloat = 16) -> UIView {
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
#endif
