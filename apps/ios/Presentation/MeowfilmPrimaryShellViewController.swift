import UIKit
import MeowUI

/// The App owns primary chrome. Domain controllers own content and business
/// tasks; retained containment forwards only the visible page's UIKit lifecycle.
/// Each primary page keeps its own header, which moves with that page.
@MainActor
final class MeowfilmPrimaryShellViewController: PageViewController, UIGestureRecognizerDelegate {
    /// Recommend, channels, film and library are separate pages with separate headers.
    override var usesPageHeader: Bool { false }

    var onSelectionChanged: ((MeowfilmPrimaryPage, TimeInterval) -> Void)?
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

    var activeContentViewController: UIViewController? {
        (pages.page(at: pages.selectedIndex) as? MeowfilmPrimaryPageViewController)?.content
    }

    func contentViewController(for page: MeowfilmPrimaryPage) -> UIViewController? {
        (pages.page(at: index(of: page)) as? MeowfilmPrimaryPageViewController)?.content
    }

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

    func select(_ page: MeowfilmPrimaryPage, animated: Bool = true) {
        guard let index = MeowfilmPrimaryPage.allCases.firstIndex(of: page) else { return }
        pages.select(index, animated: animated)
    }

    private func prepareSelectedPage() {
        channelBackGesture.isEnabled = selectedPage == .channels
    }

    /// Configure each page's own header before its view loads, so the menu,
    /// not a default return button, leads it.
    private func makePage(_ page: MeowfilmPrimaryPage, content: UIViewController) -> UIViewController {
        let primary = MeowfilmPrimaryPageViewController(page: page, content: content, themeProvider: themeProvider)
        let header = primary.pageHeader
        let menu = PageHeaderButton(image: PageIcon.image(.menu), accessibilityLabel: "菜单") { [weak self] in
            self?.openMenu()
        }
        menu.accessibilityIdentifier = "app.menu"
        header.setLeading(menu)
        if let configurable = content as? any PrimaryPageHeaderConfiguring {
            primary.themeHeader = configurable.configurePrimaryHeader(
                header, title: page.title, host: primary, openSearch: openSearch
            )
            return primary
        }
        let placeholder: String
        switch page {
        case .novel: placeholder = "搜索小说"
        case .film: placeholder = "搜索影视"
        default: placeholder = "搜索歌曲、歌手、歌单"
        }
        let search = PageHeaderSearchButton(placeholder: placeholder) { [weak self] in self?.openSearch() }
        header.setTitleView(search)
        if page == .recommend {
            let feature = PageHeaderButton(image: PageIcon.image(.recognize), accessibilityLabel: "更多功能")
            feature.accessibilityIdentifier = "app.feature"
            header.setTrailing([feature])
        }
        return primary
    }

    private func index(of page: MeowfilmPrimaryPage) -> Int {
        MeowfilmPrimaryPage.allCases.firstIndex(of: page)!
    }

    func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard gestureRecognizer === channelBackGesture else { return true }
        let velocity = channelBackGesture.velocity(in: view)
        return selectedPage == .channels && canPresentPageAlert &&
            activeContentViewController?.presentedViewController == nil && velocity.x > abs(velocity.y)
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

/// Capsule search trigger using `UIButton.Configuration` exclusively so `PageThemeable`
/// updates never mix legacy `setTitle`/`setTitleColor` APIs with configuration titles.
@MainActor
private final class PageHeaderSearchButton: UIButton, PageThemeable {
    override var isHighlighted: Bool {
        get { false }
        set {}
    }

    init(placeholder: String, action: @escaping @MainActor () -> Void) {
        super.init(frame: .zero)
        var configuration = UIButton.Configuration.plain()
        configuration.title = placeholder
        configuration.titleLineBreakMode = .byTruncatingTail
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { attributes in
            var result = attributes
            result.font = .systemFont(ofSize: 15, weight: .regular)
            return result
        }
        configuration.image = PageIcon.image(.search, size: 18)
        configuration.imagePadding = 8
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 0, leading: 14, bottom: 0, trailing: 14)
        self.configuration = configuration
        contentHorizontalAlignment = .leading
        tintAdjustmentMode = .normal
        layer.cornerRadius = PageLayoutMetrics.searchControl / 2
        layer.borderWidth = 0.5
        accessibilityIdentifier = "search.open"
        accessibilityLabel = placeholder
        setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        addAction(UIAction { _ in action() }, for: .touchUpInside)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func applyTheme(_ theme: PageTheme) {
        let muted = UIColor(theme.muted)
        tintColor = muted
        if var updated = configuration {
            updated.baseForegroundColor = muted
            configuration = updated
        }
        backgroundColor = UIColor(theme.control)
        layer.borderColor = UIColor(theme.border).cgColor
    }
}
