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
public final class FilmDetailViewController: PageViewController, UIScrollViewDelegate {
    public var onPlay: ((URL) -> Void)?
    public var onSelectItem: ((FilmItem) -> Void)?
    public private(set) var detail: FilmDetail
    public private(set) var searchContext: FilmSearchContext?
    private let library: FilmLibrary
    private let images: ArtworkImages
    private let scroll = FilmTouchScrollView(), content = UIStackView()
    private let heroClip = UIView()
    private let heroMask = CAGradientLayer()
    private let heroScrim = FilmHeroScrimView()
    private var heroCandidates: [URL] = []
    private var usesLightChrome = false
    private let poster: ArtworkView, logo: ArtworkView
    private let heroSpace = UIView()
    private let titleLabel = UILabel(), metadata = UILabel(), dateLabel = UILabel(), summary = UILabel()
    private var dateContainer: UIView?
    private let play = UIButton(type: .system), favorite = UIButton(type: .custom)
    private let seasonButton = UIButton(type: .system)
    private let episodeRail = FilmDetailRail(), castRail = FilmDetailRail(), similarRail = FilmDetailRail()
    private var selectedSeasonID: String?
    private var heroSpaceHeight: NSLayoutConstraint!
    private var episodeViews: [FilmEpisodeButton] = []
    private var castViews: [FilmCastView] = []
    private var similarViews: [FilmRelatedButton] = []
    private var sectionTitles: [UILabel] = []
    private var emptyLabels: [UILabel] = []
    private var logoHeight: NSLayoutConstraint!
    private lazy var back = PageHeaderButton(image: PageIcon.image(.back, size: 20),
        accessibilityLabel: "返回") { [weak self] in
            (self?.navigationController as? PageNavigationController)?.requestBack()
        }

    public init(detail: FilmDetail, library: FilmLibrary, images: ArtworkImages,
                themeProvider: any PageThemeProviding) {
        self.detail = detail
        self.library = library
        self.images = images
        poster = ArtworkView(size: 0, images: images)
        logo = ArtworkView(size: 0, images: images)
        selectedSeasonID = detail.seasons.first?.id
        super.init(themeProvider: themeProvider)
        pageTitle = detail.title
    }

    public convenience init(searchContext: FilmSearchContext, library: FilmLibrary, images: ArtworkImages,
                            themeProvider: any PageThemeProviding) {
        self.init(detail: searchContext.detail, library: library, images: images, themeProvider: themeProvider)
        self.searchContext = searchContext
        observe(searchContext.session.$snapshot.eraseToAnyPublisher()) { [weak self] _ in
            self?.receiveSearchUpdate()
        }
    }

    private func receiveSearchUpdate() {
        guard let searchContext else { return }
        let previous = detail
        detail = searchContext.detail
        pageTitle = detail.title
        guard isViewLoaded else { return }
        titleLabel.text = detail.title
        summary.text = detail.summary.isEmpty ? "暂无简介" : detail.summary
        dateLabel.text = detail.releaseDate
        dateLabel.isHidden = detail.releaseDate?.isEmpty != false
        dateContainer?.isHidden = dateLabel.isHidden
        renderMetadata()
        if previous.backdropURL != detail.backdropURL || previous.item.posterURL != detail.item.posterURL {
            heroCandidates = []
            for url in [detail.backdropURL, detail.item.largePosterURL, detail.item.posterURL]
                .compactMap({ $0 }) where !heroCandidates.contains(url) {
                heroCandidates.append(url)
            }
            loadNextHeroCandidate()
        }
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
        heroMask.colors = [UIColor.black.cgColor, UIColor.black.cgColor, UIColor.clear.cgColor]
        heroMask.locations = [0, 0.5, 1]
        heroClip.clipsToBounds = true
        heroClip.layer.mask = heroMask
        poster.layer.cornerRadius = 0
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
        renderFavorite()
        applyTheme()
    }

    private func configureTitle() {
        titleLabel.text = detail.title
        titleLabel.font = .systemFont(ofSize: 32, weight: .bold)
        titleLabel.numberOfLines = 2
        titleLabel.textAlignment = .center
        titleLabel.accessibilityIdentifier = "film.detail.title"
        titleLabel.accessibilityTraits = .header
        let titleContainer = inset(titleLabel, horizontal: 24)
        content.addArrangedSubview(titleContainer)
        content.setCustomSpacing(16, after: content.arrangedSubviews.last!)
        logo.contentMode = .scaleAspectFit
        logo.backgroundColor = .clear
        logo.layer.cornerRadius = 0
        logo.isHidden = detail.logoURL == nil
        logoHeight = logo.heightAnchor.constraint(equalToConstant: 72)
        logoHeight.isActive = true
        if let url = detail.logoURL {
            let logoContainer = inset(logo, horizontal: 56)
            logoContainer.isHidden = true
            content.insertArrangedSubview(logoContainer, at: 1)
            logo.onImage = { [weak titleContainer, weak logoContainer] _, _ in
                titleContainer?.isHidden = true
                logoContainer?.isHidden = false
            }
            logo.load(url)
        }
        renderMetadata()
        metadata.font = .systemFont(ofSize: 15, weight: .medium)
        metadata.numberOfLines = 2
        metadata.textAlignment = .center
        content.addArrangedSubview(inset(metadata, horizontal: 24))
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

    private func renderMetadata() {
        var facts: [String] = []
        if let seasons = detail.seasonCount, seasons > 0 { facts.append("\(seasons) 季") }
        if let episodes = detail.episodeCount, episodes > 0 { facts.append("\(episodes) 集") }
        if facts.isEmpty, !detail.item.subtitle.isEmpty { facts.append(detail.item.subtitle) }
        if !detail.item.score.isEmpty { facts.append("评分 \(detail.item.score)") }
        metadata.text = facts.joined(separator: " · ")
        metadata.isHidden = facts.isEmpty
    }

    private func configureActions() {
        var configuration = UIButton.Configuration.filled()
        configuration.title = "播放"
        configuration.image = PageIcon.image(.play, size: 16)
        configuration.imagePadding = 8
        configuration.cornerStyle = .capsule
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var attributes = $0
            attributes.font = .systemFont(ofSize: 17, weight: .semibold)
            return attributes
        }
        play.configuration = configuration
        play.accessibilityIdentifier = "film.detail.play"
        play.heightAnchor.constraint(equalToConstant: 48).isActive = true
        play.addAction(UIAction { [weak self] _ in self?.playSelection() }, for: .touchUpInside)
        let playContainer = inset(play, horizontal: 44)
        content.addArrangedSubview(playContainer)
        content.setCustomSpacing(10, after: playContainer)
        favorite.titleLabel?.font = .systemFont(ofSize: 12)
        favorite.accessibilityIdentifier = "film.detail.favorite"
        favorite.heightAnchor.constraint(equalToConstant: 48).isActive = true
        favorite.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.library.toggle(self.detail.item)
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
        summary.numberOfLines = 0
        let summaryContainer = inset(summary)
        content.addArrangedSubview(summaryContainer)
        content.setCustomSpacing(26, after: summaryContainer)
    }

    private func configureSections() {
        seasonButton.contentHorizontalAlignment = .leading
        seasonButton.titleLabel?.font = .systemFont(ofSize: 21, weight: .bold)
        seasonButton.accessibilityIdentifier = "film.detail.season"
        seasonButton.heightAnchor.constraint(equalToConstant: 44).isActive = true
        seasonButton.showsMenuAsPrimaryAction = true
        let seasonTitle = inset(seasonButton)
        content.addArrangedSubview(seasonTitle)
        content.setCustomSpacing(6, after: seasonTitle)
        content.addArrangedSubview(episodeRail)
        content.setCustomSpacing(26, after: episodeRail)
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
        let season = detail.seasons.first { $0.id == selectedSeasonID } ?? detail.seasons.first
        selectedSeasonID = season?.id
        let selectable = detail.seasons.count > 1
        play.isEnabled = selectedPlaybackURL != nil
        play.configuration?.title = play.isEnabled ? "播放" : "暂无片源"
        play.accessibilityLabel = play.isEnabled ? "播放" : "播放，暂无可用片源"
        seasonButton.setTitle((season?.title ?? "集数") + (selectable ? "  ﹀" : ""), for: .normal)
        seasonButton.menu = UIMenu(title: "选择季度", children: detail.seasons.map { season in
            UIAction(title: season.title, state: season.id == selectedSeasonID ? .on : .off) { [weak self] _ in
                self?.selectedSeasonID = season.id
                self?.renderSeason()
            }
        })
        seasonButton.isUserInteractionEnabled = selectable
        seasonButton.accessibilityTraits = selectable ? [.header, .button] : .header
        episodeRail.removeAll()
        emptyLabels.removeAll { $0.superview == nil }
        episodeViews.removeAll()
        if let season, !season.episodes.isEmpty {
            for episode in season.episodes {
                let button = FilmEpisodeButton(episode: episode, images: images)
                button.isEnabled = episode.playbackURL != nil
                button.addAction(UIAction { [weak self] _ in
                    guard let url = episode.playbackURL else { return }
                    self?.onPlay?(url)
                }, for: .touchUpInside)
                episodeRail.append(button, width: 176)
                episodeViews.append(button)
            }
        } else {
            addEmpty("暂无集数", to: episodeRail)
        }
        episodeRail.resetOffset()
        applyTheme()
    }

    private func playSelection() {
        guard let url = selectedPlaybackURL else { return }
        onPlay?(url)
    }

    /// Detail artwork prefers sharper sources and falls back once per failed candidate.
    private func loadNextHeroCandidate() {
        guard !heroCandidates.isEmpty else { return }
        poster.load(heroCandidates.removeFirst())
    }

    private var selectedPlaybackURL: URL? {
        detail.seasons.first { $0.id == selectedSeasonID }?
            .episodes.first { $0.playbackURL != nil }?.playbackURL ?? detail.playbackURL
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
        }
        if usesLightChrome != lightChrome {
            usesLightChrome = lightChrome
            setNeedsStatusBarAppearanceUpdate()
        }
    }

    private func addHeading(_ title: String) {
        let label = UILabel()
        label.text = title
        label.font = .systemFont(ofSize: 21, weight: .bold)
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
