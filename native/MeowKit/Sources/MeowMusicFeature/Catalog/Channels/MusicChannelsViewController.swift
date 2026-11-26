#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicChannelsViewController: MusicPageViewController, UIPageViewControllerDataSource,
    UIPageViewControllerDelegate, PrimaryPageHeaderConfiguring
{
    let model: MusicViewModel
    weak var router: (any MusicUIRouting)?
    let selection: MusicChannelSelection
    let catalogue: MusicPublicBrowseViewModel?
    let pager = UIPageViewController(transitionStyle: .scroll, navigationOrientation: .horizontal)
    /// A scroll pager cannot hold an empty controller list while the catalogue is empty.
    private let emptyPage = UIViewController()
    var pages: [String: MusicBrowseViewController] = [:]
    var channels: [MusicPublicBrowseChannel] = []
    private var isPaging = false
    private var observingHome = false
    private var pendingChannel: MusicPublicBrowseChannel?
    private weak var channelPicker: MusicChannelsPickerViewController?
    private lazy var pickerSheet = FixedSheetTransition(edge: .top) { [weak self] viewport in
        let safeTop = self?.view.window?.safeAreaInsets.top ?? 0
        let content = self?.channelPicker?.preferredHeight ?? 360
        return CGSize(width: viewport.width,
                      height: min(min(620, viewport.height * 0.72), safeTop + content))
    }
    private let channelTabs = MusicChannelTabsView()
    private let cataloguePlaceholder = UILabel()
    private let loadingSkeleton = MusicLoadingSkeletonView()

    public init(model: MusicViewModel,
                appearance: MusicAppearancePreferencesStore, router: any MusicUIRouting) {
        self.model = model; self.router = router
        selection = model.channelSelection
        catalogue = model.service.map { MusicPublicBrowseViewModel(service: $0, request: .discovery(.channels)) }
        super.init(appearance: appearance)
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        pickerSheet.onTransitionEnded = { [weak self] presenting, _ in
            guard !presenting else { return }
            self?.channelPicker = nil
        }
        channelTabs.isHidden = true
        pager.dataSource = self; pager.delegate = self
        emptyPage.view.backgroundColor = .clear
        addChild(pager)
        view.addSubview(pager.view)
        cataloguePlaceholder.textAlignment = .center
        cataloguePlaceholder.textColor = UIColor(theme.muted)
        cataloguePlaceholder.isHidden = true
        view.addSubview(cataloguePlaceholder)
        view.addSubview(loadingSkeleton)
        pager.view.translatesAutoresizingMaskIntoConstraints = false
        cataloguePlaceholder.translatesAutoresizingMaskIntoConstraints = false
        loadingSkeleton.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            pager.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            pager.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            pager.view.topAnchor.constraint(equalTo: contentTopAnchor),
            pager.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            cataloguePlaceholder.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 24),
            cataloguePlaceholder.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -24),
            cataloguePlaceholder.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            loadingSkeleton.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            loadingSkeleton.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            loadingSkeleton.topAnchor.constraint(equalTo: contentTopAnchor),
            loadingSkeleton.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        pager.didMove(toParent: self)
        if let catalogue {
            observe(catalogue.$sections.dropFirst().map { _ in }.eraseToAnyPublisher(), whenVisible: true) {
                [weak self] in self?.renderCatalogue()
            }
            let catalogueState = Publishers.MergeMany([
                catalogue.$isLoading.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                catalogue.$loaded.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                catalogue.$error.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            ])
            observe(catalogueState.eraseToAnyPublisher(), whenVisible: true) { [weak self] in self?.renderCatalogueState() }
        }
        observe(selection, whenVisible: true) { [weak self] in self?.renderSelection() }
    }

    public func configurePrimaryHeader(
        _ header: PageHeaderView,
        title: String,
        host: PageViewController,
        openSearch: @escaping @MainActor () -> Void
    ) -> ((PageTheme) -> Void)? {
        let picker = PageHeaderButton(image: PageIcon.image(.channels), accessibilityLabel: "选择频道") { [weak self] in
            self?.showPicker()
        }
        picker.accessibilityIdentifier = "app.channels"
        let search = PageHeaderButton(image: PageIcon.image(.search, size: 22), accessibilityLabel: "搜索", action: openSearch)
        search.layer.cornerRadius = 22
        search.accessibilityIdentifier = "search.open"
        header.setTitleView(channelTabs)
        header.setTrailing([picker, search])
        return { theme in search.backgroundColor = UIColor(theme.control) }
    }

    public override func pageDidBecomeActive() { activateCatalogue() }
    public override func pageDidBecomeInactive() {
        loadingSkeleton.setDisplayVisible(false)
        cancelPageTask(key: "observeHome"); observingHome = false
        catalogue?.disappear()
        for page in pages.values { PageViewController.invalidatePages(in: page) }
        pages.removeAll()
    }
    public override func pageDidBecomeVisible() { activateCatalogue(); renderCatalogueState() }
    public override func pageDidBecomeHidden() {
        isPaging = false
        pendingChannel = nil
        catalogue?.setDisplayVisible(false)
        loadingSkeleton.setDisplayVisible(false)
    }

    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded {
            cataloguePlaceholder.textColor = UIColor(theme.muted)
            let loading = channels.isEmpty && catalogue?.isLoading == true && catalogue?.loaded == false
            loadingSkeleton.configure(loading: loading, visible: isPageVisible, theme: theme)
            updateChannelTabs()
        }
    }

    public func select(_ channel: MusicPublicBrowseChannel) {
        if isPaging { pendingChannel = channel; return }
        selection.selectChannel(channel)
        if !channels.contains(where: { $0.identity == channel.identity }) { channels.insert(channel, at: 0) }
        renderSelection()
    }

    public func showPicker() {
        guard canPresentPageAlert, !model.isClosed else { return }
        let picker = MusicChannelsPickerViewController(channels: channels, selectedID: selection.channel?.identity,
                                                       emptyTitle: pickerEmptyTitle, appearance: appearance) {
            [weak self] channel in
            guard let self, self.isPageActive, !self.model.isClosed else { return }
            self.select(channel)
        }
        channelPicker = picker
        picker.modalPresentationStyle = .custom
        picker.transitioningDelegate = pickerSheet
        pickerSheet.install(on: picker)
        if !presentPageModal(picker) { channelPicker = nil }
    }

    public func pageViewController(_ pageViewController: UIPageViewController,
                                   viewControllerBefore viewController: UIViewController) -> UIViewController? {
        neighbor(of: viewController, step: -1)
    }

    public func pageViewController(_ pageViewController: UIPageViewController,
                                   viewControllerAfter viewController: UIViewController) -> UIViewController? {
        neighbor(of: viewController, step: 1)
    }

    public func pageViewController(_ pageViewController: UIPageViewController,
                                   willTransitionTo pendingViewControllers: [UIViewController]) {
        isPaging = true
    }

    public func pageViewController(_ pageViewController: UIPageViewController, didFinishAnimating finished: Bool,
                                   previousViewControllers: [UIViewController], transitionCompleted completed: Bool) {
        isPaging = false
        if let pending = pendingChannel {
            pendingChannel = nil
            select(pending)
        } else if completed, !model.isClosed,
           let shown = pager.viewControllers?.first,
           let channel = channels.first(where: { pages[$0.identity] === shown }) {
            selection.selectChannel(channel)
        }
        if channels.isEmpty { renderSelection() }
    }

    private func activateCatalogue() {
        guard let catalogue, isPageActive else { return }
        if model.isClosed {
            catalogue.close()
            renderCatalogue()
            return
        }
        catalogue.appear()
        catalogue.setDisplayVisible(isPageVisible)
        if !observingHome {
            observingHome = true
            runPageTask(key: "observeHome") { await catalogue.observeHomeChanges() }
        }
        if isPageVisible, !catalogue.loaded, !catalogue.isLoading, catalogue.error == nil {
            runPageTask { [weak self] in
                if self?.isPageVisible == true { await catalogue.loadIfNeeded() }
            }
        }
        renderCatalogue()
    }

    private func renderCatalogue() {
        guard isPageVisible || catalogue?.sections.isEmpty != false else { return }
        var seen = Set<String>()
        let items = catalogue?.sections.flatMap(\.items).compactMap { item -> MusicPublicBrowseChannel? in
            if case .channel(let value) = item.content, seen.insert(value.identity).inserted { return value }
            return nil
        } ?? []
        channels = items
        selection.restoreChannel(from: channels, catalogueLoaded: catalogue?.loaded == true)
        if catalogue?.loaded == true, !channels.isEmpty, let selected = selection.channel,
           !channels.contains(where: { $0.identity == selected.identity }) {
            channels.insert(selected, at: 0)
        }
        if isViewLoaded { renderSelection(); renderCatalogueState() }
    }

    private var pickerEmptyTitle: String {
        catalogue?.isLoading == true ? "" : "暂无频道"
    }

    private func updateChannelTabs() {
        guard isViewLoaded else { return }
        channelTabs.isHidden = channels.isEmpty || selection.channel == nil
        channelTabs.configure(channels: channels, selectedID: selection.channel?.identity, theme: theme) {
            [weak self] channel in self?.select(channel)
        }
    }

    private func renderCatalogueState() {
        guard isViewLoaded, isPageVisible else { return }
        let loading = channels.isEmpty && catalogue?.isLoading == true && catalogue?.loaded == false
        loadingSkeleton.configure(loading: loading, visible: isPageVisible, theme: theme)
        let text = channels.isEmpty ? catalogue?.isLoading == true ? nil :
            catalogue?.loaded == true ? "暂无频道" : nil : nil
        cataloguePlaceholder.text = text
        cataloguePlaceholder.isHidden = text == nil
        guard let catalogue, let error = catalogue.error else {
            clearErrorSource("catalogue")
            return
        }
        let message = error.message(operation: catalogue.errorContext, diagnosticID: catalogue.errorDiagnosticID)
        let request = catalogue.actionPageID
        syncErrorSource("catalogue", message: message, retry: { [weak self] in
            guard let self, catalogue.actionPageID == request else { return }
            self.runPageTask { await catalogue.retry() }
        })
    }

    private func renderSelection() {
        guard isViewLoaded else { return }
        channelPicker?.update(channels: channels, selectedID: selection.channel?.identity,
                              emptyTitle: pickerEmptyTitle)
        updateChannelTabs()
        guard !channels.isEmpty, let selected = selection.channel, let page = controller(selected) else {
            showEmptySelection()
            return
        }
        show(page)
    }

    private func showEmptySelection() {
        guard !isPaging else { return }
        // Container appearance alone does not invalidate retained pages, and
        // hidden pages can keep their carousel active.
        for page in pages.values { PageViewController.invalidatePages(in: page) }
        pages.removeAll()
        show(emptyPage)
    }

    private func show(_ page: UIViewController) {
        if pager.viewControllers?.first !== page, !isPaging {
            pager.setViewControllers([page], direction: .forward, animated: false)
        }
    }

    private func controller(_ channel: MusicPublicBrowseChannel) -> MusicBrowseViewController? {
        if let existing = pages[channel.identity] { return existing }
        guard let router else { return nil }
        let page = MusicBrowseViewController(
            title: channel.title, model: model,
            request: .discovery(.channel(channel, tag: "")), appearance: appearance,
            router: router, embedded: true
        )
        page.lifetimeOwner = self
        pages[channel.identity] = page
        return page
    }

    private func neighbor(of viewController: UIViewController, step: Int) -> UIViewController? {
        guard let index = channels.firstIndex(where: { pages[$0.identity] === viewController }),
              channels.indices.contains(index + step) else { return nil }
        return controller(channels[index + step])
    }

    nonisolated deinit {
        MainActor.assumeIsolated {
            for page in pages.values { PageViewController.invalidatePages(in: page) }
        }
    }
}

#endif
