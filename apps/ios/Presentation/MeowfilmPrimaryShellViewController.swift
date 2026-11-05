import UIKit
import MeowUI
import MeowMusicFeature
import MeowFilmFeature

/// The App owns primary chrome. Domain controllers own content and business
/// tasks; retained containment forwards only the visible page's UIKit lifecycle.
@MainActor
final class MeowfilmPrimaryShellViewController: PageViewController {
    var onSelectionChanged: ((MeowfilmPrimaryPage, TimeInterval) -> Void)?
    private var pendingChannel: MusicPublicBrowseChannel?
    private var channels: MusicChannelsViewController? {
        pages.page(at: index(of: .channels)) as? MusicChannelsViewController
    }
    private var film: FilmHomeViewController? {
        pages.page(at: index(of: .film)) as? FilmHomeViewController
    }
    private var library: MusicLibraryViewController? {
        pages.page(at: index(of: .library)) as? MusicLibraryViewController
    }
    private let pages: RetainedPagesController
    private let openMenu: () -> Void
    private let openSearch: () -> Void
    private let menu = UIButton(type: .custom), add = UIButton(type: .custom), feature = UIButton(type: .custom)
    private let channelMenu = UIButton(type: .custom)
    private lazy var search = PageHeaderTextButton(title: "") { [weak self] in self?.openSearch() }
    private lazy var titleButton = PageHeaderTextButton(title: "") {}
    private let channelTitleHost = UIView()
    private let filmTitleHost = UIView()

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
            self.renderHeader(duration: duration)
            self.onSelectionChanged?(self.selectedPage, duration)
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
        for button in [menu, titleButton, add, feature, channelMenu] { button.tintColor = UIColor(theme.ink) }
        search.tintColor = UIColor(theme.muted)
        search.configuration?.baseForegroundColor = UIColor(theme.muted)
        search.backgroundColor = UIColor(theme.control)
        search.layer.borderColor = UIColor(theme.border).cgColor
        titleButton.setTitleColor(UIColor(theme.ink), for: .normal)
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
        titleButton.contentHorizontalAlignment = .leading
        titleButton.titleLabel?.font = .systemFont(ofSize: 20, weight: .semibold)
        titleButton.accessibilityIdentifier = "app.primary.title"
        add.setImage(PageIcon.image(.plus), for: .normal)
        add.accessibilityLabel = "新建歌单"
        add.accessibilityIdentifier = "app.primary.add"
        add.addAction(UIAction { [weak self] _ in self?.library?.requestCreatePlaylist() }, for: .touchUpInside)
        feature.setImage(PageIcon.image(.recognize), for: .normal)
        feature.accessibilityLabel = "更多功能"
        feature.accessibilityIdentifier = "app.feature"
        channelMenu.setImage(PageIcon.image(.channels), for: .normal)
        channelMenu.accessibilityLabel = "选择频道"
        channelMenu.accessibilityIdentifier = "app.channels"
        channelMenu.addAction(UIAction { [weak self] _ in
            guard self?.selectedPage == .channels else { return }
            self?.channels?.showPicker()
        }, for: .touchUpInside)
        for item in [menu, add, feature, channelMenu] {
            item.adjustsImageWhenHighlighted = false
            item.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                item.widthAnchor.constraint(equalToConstant: 44),
                item.heightAnchor.constraint(equalToConstant: 44),
            ])
        }
        for item in [search, titleButton] {
            item.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        }
        pageHeader.setLeading(menu)
        pageHeader.title = nil
    }

    private func prepareSelectedPage() {
        if let switcher = channels?.channelSwitcherView {
            if switcher.superview !== channelTitleHost {
                channelTitleHost.subviews.forEach { $0.removeFromSuperview() }
                channelTitleHost.addSubview(switcher)
                switcher.pinEdges(to: channelTitleHost)
            }
        }
        if selectedPage == .film, let switcher = film?.sourceSwitcherView, switcher.superview !== filmTitleHost {
            filmTitleHost.subviews.forEach { $0.removeFromSuperview() }
            filmTitleHost.addSubview(switcher)
            switcher.pinEdges(to: filmTitleHost)
        }
        if selectedPage == .channels, let channel = pendingChannel, let channels {
            pendingChannel = nil
            channels.select(channel)
        }
    }

    private func renderHeader(duration: TimeInterval = 0) {
        guard isViewLoaded else { return }
        let apply = {
            self.pageHeader.setTitleView(self.selectedPage == .recommend ? self.search :
                self.selectedPage == .channels ? self.channelTitleHost :
                self.selectedPage == .film ? self.filmTitleHost : self.titleButton)
            self.pageHeader.setTrailing(self.selectedPage == .recommend ? [self.feature] :
                self.selectedPage == .channels ? [self.channelMenu] :
                self.selectedPage == .library ? [self.add] : [])
            self.titleButton.setTitle("我的", for: .normal)
            self.titleButton.accessibilityLabel = "我的"
            self.applyTheme()
        }
        if duration > 0 && view.window != nil {
            UIView.transition(with: pageHeader, duration: duration,
                              options: [.transitionCrossDissolve, .allowAnimatedContent],
                              animations: apply)
        } else {
            apply()
        }
    }

    private func index(of page: MeowfilmPrimaryPage) -> Int {
        MeowfilmPrimaryPage.allCases.firstIndex(of: page)!
    }
}
