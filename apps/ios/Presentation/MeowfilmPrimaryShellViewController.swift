import UIKit
import MeowUI
import MeowMusicFeature

/// The App owns primary chrome. Domain controllers own content and business
/// tasks; retained containment forwards only the visible page's UIKit lifecycle.
/// Each primary page keeps its own header, which moves with that page.
@MainActor
final class MeowfilmPrimaryShellViewController: PageViewController, UIGestureRecognizerDelegate {
    /// Recommend, channels, film and library are separate pages with separate headers.
    override var usesPageHeader: Bool { false }

    var onSelectionChanged: ((MeowfilmPrimaryPage, TimeInterval) -> Void)?
    private var pendingChannel: MusicPublicBrowseChannel?
    private var channels: MusicChannelsViewController? {
        (pages.page(at: index(of: .channels)) as? MeowfilmPrimaryPageViewController)?.content as? MusicChannelsViewController
    }
    private var library: MusicLibraryViewController? {
        (pages.page(at: index(of: .library)) as? MeowfilmPrimaryPageViewController)?.content as? MusicLibraryViewController
    }
    private let pageFactories: [@MainActor () -> UIViewController]
    private lazy var pages = RetainedPagesController(
        pageFactories: zip(MeowfilmPrimaryPage.allCases, pageFactories).map { page, factory in
            { [weak self] in self?.makePage(page, content: factory()) ?? UIViewController() }
        }
    )
    private let openMenu: () -> Void
    private let openSearch: () -> Void
    private let channelBackGesture = UIScreenEdgePanGestureRecognizer()

    init(pageFactories: [@MainActor () -> UIViewController], themeProvider: any PageThemeProviding,
         openMenu: @escaping () -> Void, openSearch: @escaping () -> Void) {
        self.pageFactories = pageFactories
        self.openMenu = openMenu
        self.openSearch = openSearch
        super.init(themeProvider: themeProvider)
    }

    var selectedPage: MeowfilmPrimaryPage { MeowfilmPrimaryPage.allCases[pages.selectedIndex] }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "app.primary"
        channelBackGesture.edges = .left
        channelBackGesture.delegate = self
        channelBackGesture.addTarget(self, action: #selector(channelBackPanned))
        view.addGestureRecognizer(channelBackGesture)
        addChild(pages)
        view.addSubview(pages.view)
        pages.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            pages.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            pages.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            pages.view.topAnchor.constraint(equalTo: contentTopAnchor),
            pages.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        pages.didMove(toParent: self)
        pages.willSelect = { [weak self] _, duration in
            guard let self else { return }
            self.prepareSelectedPage()
            self.onSelectionChanged?(self.selectedPage, duration)
        }
        prepareSelectedPage()
    }

    func select(_ page: MeowfilmPrimaryPage) {
        guard let index = MeowfilmPrimaryPage.allCases.firstIndex(of: page) else { return }
        pages.select(index)
    }

    func selectChannel(_ channel: MusicPublicBrowseChannel) {
        pendingChannel = channel
        select(.channels)
        prepareSelectedPage()
    }

    private func prepareSelectedPage() {
        channelBackGesture.isEnabled = selectedPage == .channels
        if selectedPage == .channels, let channel = pendingChannel, let channels {
            pendingChannel = nil
            channels.select(channel)
        }
    }

    /// Configure each page's own header before its view loads, so the menu,
    /// not a default return button, leads it.
    private func makePage(_ page: MeowfilmPrimaryPage, content: UIViewController) -> UIViewController {
        let primary = MeowfilmPrimaryPageViewController(page: page, content: content, themeProvider: themeProvider)
        let header = primary.pageHeader
        header.setLeading(Self.iconButton(.menu, label: "菜单", identifier: "app.menu") { [weak self] in
            self?.openMenu()
        })
        switch page {
        case .recommend, .film:
            let search = makeSearchField()
            header.setTitleView(search)
            if page == .recommend {
                header.setTrailing([Self.iconButton(.recognize, label: "更多功能", identifier: "app.feature")])
            }
            primary.themeHeader = { theme in
                search.tintColor = UIColor(theme.muted)
                search.configuration?.baseForegroundColor = UIColor(theme.muted)
                search.backgroundColor = UIColor(theme.control)
                search.layer.borderColor = UIColor(theme.border).cgColor
            }
        case .channels:
            let switcher = (content as? MusicChannelsViewController)?.channelSwitcherView ?? UIView()
            let search = PageHeaderButton(
                image: PageIcon.image(.search, size: 22), accessibilityLabel: "搜索"
            ) { [weak self] in self?.openSearch() }
            search.layer.cornerRadius = 22
            search.accessibilityIdentifier = "search.open"
            header.setTitleView(switcher)
            header.setTrailing([
                Self.iconButton(.channels, label: "选择频道", identifier: "app.channels") { [weak self] in
                    guard self?.selectedPage == .channels else { return }
                    self?.channels?.showPicker()
                },
                search,
            ])
            primary.themeHeader = { theme in
                search.backgroundColor = UIColor(theme.control)
            }
        case .library:
            let title = PageHeaderTextButton(title: page.title) {}
            title.contentHorizontalAlignment = .leading
            title.titleLabel?.font = .systemFont(ofSize: 20, weight: .semibold)
            title.accessibilityIdentifier = "app.primary.title"
            title.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
            header.setTitleView(title)
            header.setTrailing([Self.iconButton(.plus, label: "新建歌单", identifier: "app.primary.add") {
                [weak self] in self?.library?.requestCreatePlaylist()
            }])
            primary.themeHeader = { theme in title.setTitleColor(UIColor(theme.ink), for: .normal) }
        }
        return primary
    }

    private func index(of page: MeowfilmPrimaryPage) -> Int {
        MeowfilmPrimaryPage.allCases.firstIndex(of: page)!
    }

    private func makeSearchField() -> PageHeaderTextButton {
        let search = PageHeaderTextButton(title: "") { [weak self] in self?.openSearch() }
        var configuration = UIButton.Configuration.plain()
        configuration.title = "搜索歌曲、歌手、歌单"
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { attributes in
            var result = attributes
            result.font = .systemFont(ofSize: 15)
            return result
        }
        configuration.image = PageIcon.image(.search, size: 18)
        configuration.imagePadding = 8
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 0, leading: 14, bottom: 0, trailing: 14)
        search.configuration = configuration
        search.contentHorizontalAlignment = .leading
        search.layer.cornerRadius = PageLayoutMetrics.searchControl / 2
        search.layer.borderWidth = 0.5
        search.accessibilityIdentifier = "search.open"
        search.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        return search
    }

    private static func iconButton(_ kind: PageIcon.Kind, label: String, identifier: String,
                                   action: (@MainActor () -> Void)? = nil) -> UIButton {
        let button = UIButton(type: .custom)
        button.setImage(PageIcon.image(kind), for: .normal)
        button.accessibilityLabel = label
        button.accessibilityIdentifier = identifier
        if let action { button.addAction(UIAction { _ in action() }, for: .touchUpInside) }
        button.adjustsImageWhenHighlighted = false
        button.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            button.widthAnchor.constraint(equalToConstant: 44),
            button.heightAnchor.constraint(equalToConstant: 44),
        ])
        return button
    }

    func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard gestureRecognizer === channelBackGesture else { return true }
        let velocity = channelBackGesture.velocity(in: view)
        return selectedPage == .channels && canPresentPageAlert &&
            channels?.presentedViewController == nil && velocity.x > abs(velocity.y)
    }

    /// Channel paging, rails, banners and lists start only after the edge return fails.
    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer,
                           shouldBeRequiredToFailBy otherGestureRecognizer: UIGestureRecognizer) -> Bool {
        gestureRecognizer === channelBackGesture &&
            otherGestureRecognizer === (otherGestureRecognizer.view as? UIScrollView)?.panGestureRecognizer
    }

    @objc private func channelBackPanned(_ gesture: UIScreenEdgePanGestureRecognizer) {
        guard gesture.state == .ended, selectedPage == .channels else { return }
        if BackNavigationSwipePolicy.commits(
            distance: Double(gesture.translation(in: view).x),
            velocity: Double(gesture.velocity(in: view).x), width: Double(view.bounds.width)
        ) {
            select(.recommend)
        }
    }
}

/// A primary page: its own App-owned header above a domain page. The domain
/// page has a header ancestor, so it shares this header instead of adding one.
@MainActor
private final class MeowfilmPrimaryPageViewController: PageViewController {
    let content: UIViewController
    var themeHeader: ((PageTheme) -> Void)?
    private let page: MeowfilmPrimaryPage

    init(page: MeowfilmPrimaryPage, content: UIViewController, themeProvider: any PageThemeProviding) {
        self.page = page
        self.content = content
        super.init(themeProvider: themeProvider)
    }

    /// The header and the domain page paint their own window-aligned surfaces.
    override func loadView() {
        view = UIView()
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "app.page.\(page.rawValue)"
        addChild(content)
        view.addSubview(content.view)
        content.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            content.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            content.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            content.view.topAnchor.constraint(equalTo: contentTopAnchor),
            content.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        content.didMove(toParent: self)
    }

    override func applyTheme() {
        super.applyTheme()
        if isViewLoaded { themeHeader?(theme) }
    }
}
