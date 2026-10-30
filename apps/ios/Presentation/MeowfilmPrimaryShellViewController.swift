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
    private let header = UIView()
    private let menu = UIButton(type: .system), search = UIButton(type: .system)
    private let titleButton = UIButton(type: .system), add = UIButton(type: .system)

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
            pages.view.topAnchor.constraint(equalTo: header.bottomAnchor),
            pages.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
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
        for button in [menu, titleButton, add] { button.tintColor = UIColor(theme.ink) }
        search.tintColor = UIColor(theme.muted)
        search.configuration?.baseForegroundColor = UIColor(theme.muted)
        search.backgroundColor = UIColor(theme.control)
        search.layer.borderColor = UIColor(theme.border).cgColor
    }

    private func configureHeader() {
        view.addSubview(header)
        header.translatesAutoresizingMaskIntoConstraints = false
        menu.setImage(PageIcon.image(.menu), for: .normal)
        menu.accessibilityLabel = "菜单"
        menu.accessibilityIdentifier = "app.menu"
        menu.addAction(UIAction { [weak self] _ in self?.openMenu() }, for: .touchUpInside)
        var configuration = UIButton.Configuration.plain()
        configuration.title = "搜索歌曲、歌手、歌单"
        configuration.image = PageIcon.image(.search, size: 18)
        configuration.imagePadding = 8
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 0, leading: 14, bottom: 0, trailing: 14)
        search.configuration = configuration
        search.contentHorizontalAlignment = .leading
        search.layer.cornerRadius = 20
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
        for item in [menu, search, titleButton, add] {
            item.translatesAutoresizingMaskIntoConstraints = false
            header.addSubview(item)
        }
        NSLayoutConstraint.activate([
            header.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 12),
            header.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -12),
            header.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor),
            header.heightAnchor.constraint(equalToConstant: PageLayoutMetrics.header),
            menu.leadingAnchor.constraint(equalTo: header.leadingAnchor),
            menu.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            menu.widthAnchor.constraint(equalToConstant: 44), menu.heightAnchor.constraint(equalToConstant: 44),
            search.leadingAnchor.constraint(equalTo: menu.trailingAnchor, constant: 8),
            search.trailingAnchor.constraint(equalTo: header.trailingAnchor),
            search.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            search.heightAnchor.constraint(equalToConstant: 40),
            titleButton.leadingAnchor.constraint(equalTo: menu.trailingAnchor, constant: 8),
            titleButton.trailingAnchor.constraint(equalTo: add.leadingAnchor),
            titleButton.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            titleButton.heightAnchor.constraint(equalToConstant: 44),
            add.trailingAnchor.constraint(equalTo: header.trailingAnchor),
            add.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            add.widthAnchor.constraint(equalToConstant: 44), add.heightAnchor.constraint(equalToConstant: 44),
        ])
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
        search.isHidden = selectedPage != .recommend
        titleButton.isHidden = selectedPage == .recommend
        add.isHidden = selectedPage != .library
        titleButton.setTitle(selectedPage == .channels ?
                             (channels?.selectedChannel?.title ?? "频道") + " ⌄" : "我的", for: .normal)
        titleButton.accessibilityLabel = selectedPage == .channels ? "选择频道" : "我的"
        applyTheme()
    }
}
