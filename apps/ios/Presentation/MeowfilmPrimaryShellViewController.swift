import UIKit
import MeowUI
import MeowMusicFeature

/// The App owns primary chrome. Domain controllers own content and business
/// tasks; retained containment forwards only the visible page's UIKit lifecycle.
@MainActor
final class MeowfilmPrimaryShellViewController: PageViewController {
    var onSelectionChanged: ((MeowfilmPrimaryPage) -> Void)?
    private var pendingChannel: MusicPublicBrowseChannel?
    private var channels: MusicChannelsViewController? {
        pages.page(at: 1) as? MusicChannelsViewController
    }
    private var library: MusicLibraryViewController? {
        pages.page(at: 2) as? MusicLibraryViewController
    }
    private let pages: RetainedPagesController
    private let openMenu: () -> Void
    private let openSearch: () -> Void
    private let menu = UIButton(type: .system), add = UIButton(type: .system), feature = UIButton(type: .system)
    private let search = UIButton(type: .system), titleButton = UIButton(type: .system)
    private lazy var searchTitle = NavigationTitleView(content: search)
    private lazy var pageTitle = NavigationTitleView(content: titleButton)
    private lazy var addItem = UIBarButtonItem(customView: add)
    private lazy var featureItem = UIBarButtonItem(customView: feature)

    init(pageFactories: [@MainActor () -> UIViewController], themeProvider: any PageThemeProviding,
         openMenu: @escaping () -> Void, openSearch: @escaping () -> Void) {
        pages = RetainedPagesController(pageFactories: pageFactories)
        self.openMenu = openMenu
        self.openSearch = openSearch
        super.init(themeProvider: themeProvider)
    }

    var selectedPage: MeowfilmPrimaryPage { MeowfilmPrimaryPage.allCases[pages.selectedIndex] }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "app.primary"
        configureHeader()
        addChild(pages)
        view.addSubview(pages.view)
        pages.view.pinEdges(to: view)
        pages.didMove(toParent: self)
        pages.didSelect = { [weak self] _ in
            guard let self else { return }
            self.prepareSelectedPage()
            self.renderHeader()
            self.onSelectionChanged?(self.selectedPage)
        }
        prepareSelectedPage()
        renderHeader()
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

    override func applyTheme() {
        super.applyTheme()
        for button in [menu, titleButton, add, feature] { button.tintColor = UIColor(theme.ink) }
        search.tintColor = UIColor(theme.muted)
        search.configuration?.baseForegroundColor = UIColor(theme.muted)
        search.backgroundColor = UIColor(theme.control)
        search.layer.borderColor = UIColor(theme.border).cgColor
    }

    private func configureHeader() {
        menu.setImage(PageIcon.image(.menu), for: .normal)
        menu.accessibilityLabel = "菜单"
        menu.accessibilityIdentifier = "app.menu"
        menu.addAction(UIAction { [weak self] _ in self?.openMenu() }, for: .touchUpInside)
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
        search.addAction(UIAction { [weak self] _ in self?.openSearch() }, for: .touchUpInside)
        titleButton.contentHorizontalAlignment = .leading
        titleButton.titleLabel?.font = .systemFont(ofSize: 20, weight: .semibold)
        titleButton.addAction(UIAction { [weak self] _ in
            if self?.selectedPage == .channels { self?.channels?.showPicker() }
        }, for: .touchUpInside)
        add.setImage(PageIcon.image(.plus), for: .normal)
        add.accessibilityLabel = "新建歌单"
        add.addAction(UIAction { [weak self] _ in self?.library?.requestCreatePlaylist() }, for: .touchUpInside)
        feature.setImage(PageIcon.image(.recognize), for: .normal)
        feature.accessibilityLabel = "更多功能"
        feature.accessibilityIdentifier = "app.feature"
        for item in [menu, add, feature] {
            item.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                item.widthAnchor.constraint(equalToConstant: 44),
                item.heightAnchor.constraint(equalToConstant: 44),
            ])
        }
        for item in [search, titleButton] {
            item.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        }
        navigationItem.leftBarButtonItem = UIBarButtonItem(customView: menu)
        if #available(iOS 26.0, *) {
            navigationItem.leftBarButtonItem?.hidesSharedBackground = true
            addItem.hidesSharedBackground = true
            featureItem.hidesSharedBackground = true
        }
    }

    private func prepareSelectedPage() {
        channels?.onChannelChanged = { [weak self] _ in self?.renderHeader() }
        if selectedPage == .channels, let channel = pendingChannel, let channels {
            pendingChannel = nil
            channels.select(channel)
        }
    }

    private func renderHeader() {
        guard isViewLoaded else { return }
        navigationItem.titleView = selectedPage == .recommend ? searchTitle : pageTitle
        navigationItem.rightBarButtonItem = selectedPage == .recommend ? featureItem :
            selectedPage == .library ? addItem : nil
        titleButton.setTitle(selectedPage == .channels ?
                             (channels?.selectedChannel?.title ?? "频道") + " ⌄" : "我的", for: .normal)
        titleButton.accessibilityLabel = selectedPage == .channels ? "选择频道" : "我的"
        applyTheme()
    }
}
