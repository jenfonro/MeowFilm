import UIKit
import MeowDebug
import MeowUI
import MeowMusicFeature
import MeowFilmFeature
import MeowNovelFeature

@MainActor
final class MeowfilmLibraryViewController: PageViewController,
    UIPageViewControllerDataSource,
    UIPageViewControllerDelegate,
    PrimaryPageHeaderConfiguring
{
    enum Tab: Int, CaseIterable {
        case music = 0
        case film = 1
        case novel = 2

        var id: String {
            switch self {
            case .music: return "music"
            case .film: return "film"
            case .novel: return "novel"
            }
        }

        var title: String {
            switch self {
            case .music: return "音乐"
            case .film: return "影视"
            case .novel: return "小说"
            }
        }

        init?(identifier: String) {
            switch identifier.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() {
            case "music", "音乐": self = .music
            case "film", "影视": self = .film
            case "novel", "小说": self = .novel
            default: return nil
            }
        }
    }

    let musicPage: MusicLibraryViewController
    let filmPage: FilmPersonalLibraryViewController
    let novelPage: NovelPersonalLibraryViewController

    private let pager = UIPageViewController(transitionStyle: .scroll, navigationOrientation: .horizontal)
    private let tabsView = MeowfilmLibraryTabsView()
    private weak var headerRef: PageHeaderView?
    private var musicTrailingButtons: [UIView] = []
    private weak var pagerScrollView: UIScrollView?
    private var pagerOffsetObservation: NSKeyValueObservation?

    private(set) var selectedTab: Tab = .music
    private var isPaging = false
    private var programmaticTransition: (origin: Tab, target: Tab)?
    private var pendingTab: Tab?

    private var orderedPages: [UIViewController] {
        [musicPage, filmPage, novelPage]
    }

    var activeChildController: UIViewController {
        controller(for: selectedTab)
    }

    init(
        musicPage: MusicLibraryViewController,
        filmPage: FilmPersonalLibraryViewController,
        novelPage: NovelPersonalLibraryViewController,
        themeProvider: any PageThemeProviding
    ) {
        self.musicPage = musicPage
        self.filmPage = filmPage
        self.novelPage = novelPage
        super.init(themeProvider: themeProvider)
        for child in [musicPage as PageViewController, filmPage, novelPage] {
            child.lifetimeOwner = self
        }
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "app.library.pager"
        tabsView.onSelect = { [weak self] tab in
            self?.selectTab(tab, animated: true)
        }
        tabsView.configure(selected: selectedTab, theme: theme)

        pager.dataSource = self
        pager.delegate = self
        addChild(pager)
        view.addSubview(pager.view)
        pager.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            pager.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            pager.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            pager.view.topAnchor.constraint(equalTo: contentTopAnchor),
            pager.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        pager.didMove(toParent: self)
        pager.setViewControllers([controller(for: selectedTab)], direction: .forward, animated: false)

        if let scroll = pager.view.subviews.compactMap({ $0 as? UIScrollView }).first {
            pagerScrollView = scroll
            pagerOffsetObservation = scroll.observe(\.contentOffset, options: [.new]) { [weak self] scroll, _ in
                MainActor.assumeIsolated { self?.updatePagingProgress(scroll) }
            }
        }
        updateHeaderTrailing()
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        tabsView.applyTheme(theme)
    }

    override func pageDidBecomeInactive() {
        for page in orderedPages {
            PageViewController.invalidatePages(in: page)
        }
        super.pageDidBecomeInactive()
    }

    override func pageDidBecomeHidden() {
        isPaging = false
        programmaticTransition = nil
        pendingTab = nil
        tabsView.setPosition(CGFloat(selectedTab.rawValue))
        super.pageDidBecomeHidden()
    }

    func configurePrimaryHeader(
        _ header: PageHeaderView,
        title: String,
        host: PageViewController,
        openSearch: @escaping @MainActor () -> Void
    ) -> ((PageTheme) -> Void)? {
        headerRef = header
        musicTrailingButtons = musicPage.makeHeaderTrailingButtons(host: host)
        header.setTitleView(tabsView)
        updateHeaderTrailing()
        return { [weak self] theme in
            self?.tabsView.applyTheme(theme)
        }
    }

    func selectTab(_ tab: Tab, animated: Bool = true) {
        if isPaging {
            pendingTab = tab
            return
        }
        guard tab != selectedTab || pager.viewControllers?.first !== controller(for: tab) else {
            tabsView.configure(selected: tab, theme: theme)
            updateHeaderTrailing()
            return
        }
        let origin = selectedTab
        let direction: UIPageViewController.NavigationDirection = tab.rawValue >= origin.rawValue ? .forward : .reverse
        let shouldAnimate = animated && isViewLoaded && view.window != nil && !UIAccessibility.isReduceMotionEnabled
        selectedTab = tab
        tabsView.setSelected(tab)
        updateHeaderTrailing()

        guard isViewLoaded else {
            tabsView.setPosition(CGFloat(tab.rawValue))
            return
        }
        if shouldAnimate {
            isPaging = true
            programmaticTransition = (origin, tab)
            pager.setViewControllers([controller(for: tab)], direction: direction, animated: true) { [weak self] _ in
                guard let self else { return }
                self.isPaging = false
                self.programmaticTransition = nil
                self.tabsView.configure(selected: self.selectedTab, theme: self.theme)
                if let pending = self.pendingTab {
                    self.pendingTab = nil
                    self.selectTab(pending, animated: true)
                }
            }
        } else {
            programmaticTransition = nil
            pager.setViewControllers([controller(for: tab)], direction: direction, animated: false)
            tabsView.configure(selected: tab, theme: theme)
        }
    }

    private func controller(for tab: Tab) -> UIViewController {
        switch tab {
        case .music: return musicPage
        case .film: return filmPage
        case .novel: return novelPage
        }
    }

    private func tab(for controller: UIViewController) -> Tab? {
        if controller === musicPage { return .music }
        if controller === filmPage { return .film }
        if controller === novelPage { return .novel }
        return nil
    }

    private func updateHeaderTrailing() {
        headerRef?.setTrailing(selectedTab == .music ? musicTrailingButtons : [])
    }

    private func updatePagingProgress(_ scroll: UIScrollView) {
        let width = scroll.bounds.width
        guard width > 0 else { return }
        if let transition = programmaticTransition {
            let progress = min(1, max(0, abs(scroll.contentOffset.x - width) / width))
            let pos = CGFloat(transition.origin.rawValue) + CGFloat(transition.target.rawValue - transition.origin.rawValue) * progress
            tabsView.setPosition(pos)
            return
        }
        let delta = (scroll.contentOffset.x - width) / width
        guard abs(delta) > 0.0005 else {
            tabsView.setPosition(CGFloat(selectedTab.rawValue))
            return
        }
        let maxIndex = CGFloat(Tab.allCases.count - 1)
        let pos = min(maxIndex, max(0, CGFloat(selectedTab.rawValue) + delta))
        tabsView.setPosition(pos)
    }

    // MARK: - UIPageViewControllerDataSource & Delegate

    func pageViewController(
        _ pageViewController: UIPageViewController,
        viewControllerBefore viewController: UIViewController
    ) -> UIViewController? {
        guard let current = tab(for: viewController),
              let prev = Tab(rawValue: current.rawValue - 1) else { return nil }
        return controller(for: prev)
    }

    func pageViewController(
        _ pageViewController: UIPageViewController,
        viewControllerAfter viewController: UIViewController
    ) -> UIViewController? {
        guard let current = tab(for: viewController),
              let next = Tab(rawValue: current.rawValue + 1) else { return nil }
        return controller(for: next)
    }

    func pageViewController(
        _ pageViewController: UIPageViewController,
        willTransitionTo pendingViewControllers: [UIViewController]
    ) {
        isPaging = true
    }

    func pageViewController(
        _ pageViewController: UIPageViewController,
        didFinishAnimating finished: Bool,
        previousViewControllers: [UIViewController],
        transitionCompleted completed: Bool
    ) {
        isPaging = false
        programmaticTransition = nil
        if completed, let shown = pager.viewControllers?.first, let resolved = tab(for: shown) {
            selectedTab = resolved
            updateHeaderTrailing()
        }
        tabsView.configure(selected: selectedTab, theme: theme)
        if let pending = pendingTab {
            pendingTab = nil
            selectTab(pending, animated: true)
        }
    }

    nonisolated deinit {
        pagerOffsetObservation?.invalidate()
        MainActor.assumeIsolated {
            for page in [musicPage as UIViewController, filmPage, novelPage] {
                PageViewController.invalidatePages(in: page)
            }
        }
    }
}

// MARK: - Header Tabs View

@MainActor
private final class MeowfilmLibraryTabsView: UIView, PageThemeable {
    var onSelect: ((MeowfilmLibraryViewController.Tab) -> Void)?

    private let buttons: [UIButton]
    private let buttonWidths: [CGFloat]
    private let indicator = UIView()
    private var theme = PageTheme()
    private var selected: MeowfilmLibraryViewController.Tab = .music
    private var position: CGFloat = 0

    override init(frame: CGRect) {
        let font = UIFont.systemFont(ofSize: 17, weight: .regular)
        var createdButtons: [UIButton] = []
        var widths: [CGFloat] = []
        for tab in MeowfilmLibraryViewController.Tab.allCases {
            let button = UIButton(type: .custom)
            button.setTitle(tab.title, for: .normal)
            button.titleLabel?.font = font
            button.accessibilityIdentifier = "app.library.tab.\(tab.id)"
            let textWidth = ceil((tab.title as NSString).size(withAttributes: [.font: font]).width)
            widths.append(max(44, textWidth + 16))
            createdButtons.append(button)
        }
        buttons = createdButtons
        buttonWidths = widths
        super.init(frame: frame)
        accessibilityIdentifier = "app.library.tabs"
        for (index, button) in buttons.enumerated() {
            let tab = MeowfilmLibraryViewController.Tab.allCases[index]
            button.addAction(UIAction { [weak self] _ in
                self?.onSelect?(tab)
            }, for: .touchUpInside)
            addSubview(button)
        }
        indicator.layer.cornerRadius = 1.5
        indicator.isUserInteractionEnabled = false
        indicator.accessibilityIdentifier = "app.library.tab.indicator"
        addSubview(indicator)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(selected: MeowfilmLibraryViewController.Tab, theme: PageTheme) {
        self.selected = selected
        self.theme = theme
        setSelected(selected)
        setPosition(CGFloat(selected.rawValue))
    }

    func setSelected(_ tab: MeowfilmLibraryViewController.Tab) {
        selected = tab
        for (index, button) in buttons.enumerated() {
            button.accessibilityTraits = index == tab.rawValue ? [.button, .selected] : .button
        }
    }

    func setPosition(_ value: CGFloat) {
        let maxPos = CGFloat(max(0, buttons.count - 1))
        position = min(maxPos, max(0, value))
        applyVisualState()
    }

    func applyTheme(_ theme: PageTheme) {
        self.theme = theme
        applyVisualState()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let totalWidth = buttonWidths.reduce(0, +)
        let gaps = CGFloat(max(1, buttons.count - 1))
        let preferredSpacing: CGFloat = 14
        let spacing: CGFloat = bounds.width > 0 && totalWidth + gaps * preferredSpacing > bounds.width
            ? max(4, floor((bounds.width - totalWidth) / gaps))
            : preferredSpacing
        var x: CGFloat = 0
        for (index, button) in buttons.enumerated() {
            let w = buttonWidths[index]
            button.frame = CGRect(x: x, y: 0, width: w, height: bounds.height)
            x += w + spacing
        }
        applyVisualState()
    }

    private func applyVisualState() {
        guard !buttons.isEmpty else { return }
        indicator.backgroundColor = UIColor(theme.ink)
        let clamped = min(CGFloat(buttons.count - 1), max(0, position))
        let lowerIndex = Int(clamped)
        let upperIndex = min(lowerIndex + 1, buttons.count - 1)
        let progress = clamped - CGFloat(lowerIndex)

        UIView.performWithoutAnimation {
            for (index, button) in buttons.enumerated() {
                let amount = max(0, 1 - abs(clamped - CGFloat(index)))
                let blended = amount <= 0 ? theme.muted
                    : amount >= 1 ? theme.ink
                    : theme.muted.mixed(with: theme.ink, ratio: Double(amount))
                button.setTitleColor(UIColor(blended), for: .normal)
            }
            guard buttons[lowerIndex].bounds.width > 0 else { return }
            let lowerX = buttons[lowerIndex].frame.midX - 10
            let upperX = buttons[upperIndex].frame.midX - 10
            let interpolatedX = lowerX + (upperX - lowerX) * progress
            indicator.frame = CGRect(
                x: interpolatedX,
                y: max(0, bounds.height - 4),
                width: 20,
                height: 3
            )
        }
    }
}

// MARK: - Debug Support

extension MeowfilmLibraryViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    var debugPageStatus: DebugPageStatus {
        let childStatus = (activeChildController as? any DebugPageInspectable)?.debugPageStatus
        return DebugPageStatus(
            target: selectedTab == .music ? "music.library" : "library.\(selectedTab.id)",
            moduleId: selectedTab.id,
            route: "library/\(selectedTab.id)",
            title: "我的 · \(selectedTab.title)",
            isBusy: childStatus?.isBusy ?? false,
            statusText: childStatus?.statusText,
            isError: childStatus?.isError ?? false,
            errorText: childStatus?.errorText
        )
    }

    func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let tabs = Tab.allCases.map { tab in
            DebugTabItem(
                group: "domain",
                index: tab.rawValue,
                id: tab.id,
                title: tab.title,
                selected: tab == selectedTab,
                enabled: true
            )
        }
        guard let inspectable = activeChildController as? any DebugPageInspectable else {
            return DebugContentSnapshot(target: debugPageStatus.target, kind: "empty", tabs: tabs)
        }
        let inner = inspectable.debugContent(maxItems: maxItems, includeRaw: includeRaw)
        return DebugContentSnapshot(
            target: debugPageStatus.target,
            kind: inner.kind,
            loading: inner.loading,
            error: inner.error,
            tabs: tabs,
            filters: inner.filters,
            sections: inner.sections,
            rows: inner.rows,
            hasMore: inner.hasMore,
            detail: inner.detail,
            player: inner.player,
            reader: inner.reader,
            settings: inner.settings,
            extra: ["selectedTab": .string(selectedTab.id)]
        )
    }

    func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "tab.select" {
            let picked: Tab
            if let idx = request.params["index"]?.intValue {
                guard let tab = Tab(rawValue: idx) else {
                    throw DebugAPIError.invalidArgument("Library tab index \(idx) out of range (0...2)")
                }
                picked = tab
            } else if let id = request.params["id"]?.stringValue {
                guard let tab = Tab(identifier: id) else {
                    throw DebugAPIError.notFound("Library tab not found: \(id)")
                }
                picked = tab
            } else {
                throw DebugAPIError.invalidArgument("Provide 'id' or 'index' for tab.select")
            }
            selectTab(picked, animated: false)
            return .object(["group": .string("domain"), "selected": .string(picked.id)])
        }
        guard let actionable = activeChildController as? any DebugPageActionable else {
            throw DebugAPIError.invalidState("Active library tab does not support '\(request.action)'")
        }
        return try await actionable.debugPerformAction(request)
    }

    func debugRefresh() {
        (activeChildController as? any DebugPageRefreshable)?.debugRefresh()
    }
}
