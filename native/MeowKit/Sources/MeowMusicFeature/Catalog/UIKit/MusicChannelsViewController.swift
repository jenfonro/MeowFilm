#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicChannelsViewController: MusicPageViewController, UIPageViewControllerDataSource,
    UIPageViewControllerDelegate, UICollectionViewDelegateFlowLayout
{
    public var onChannelChanged: ((MusicPublicBrowseChannel?) -> Void)?
    public var selectedChannel: MusicPublicBrowseChannel? { selection.channel }
    private let model: MusicViewModel
    private let browser: (any MusicPublicBrowsing)?
    private weak var router: (any MusicUIRouting)?
    private let selection = MusicChannelSelection()
    private let catalogue: MusicPublicBrowseViewModel?
    private let pager = UIPageViewController(transitionStyle: .scroll, navigationOrientation: .horizontal)
    private let gridPage = UIViewController()
    private lazy var modes = RetainedPagesController(pageFactories: [
        { [gridPage] in gridPage }, { [pager] in pager },
    ])
    private var pages: [String: MusicBrowseViewController] = [:]
    private var channels: [MusicPublicBrowseChannel] = []
    private var pagingAccess: UUID?
    private var observation: Task<Void, Never>?
    private var observationAccess: UUID?
    private var pendingChannel: MusicPublicBrowseChannel?
    private var dataSource: UICollectionViewDiffableDataSource<Int, String>!
    private var shownCatalogueError: String?
    private weak var channelPicker: MusicChannelsPickerViewController?
    private lazy var grid = UICollectionView(frame: .zero, collectionViewLayout: UICollectionViewFlowLayout())

    public init(model: MusicViewModel, browser: (any MusicPublicBrowsing)?,
                appearance: MusicAppearancePreferencesStore, router: any MusicUIRouting) {
        self.model = model; self.browser = browser; self.router = router
        catalogue = browser.map { MusicPublicBrowseViewModel(service: $0, request: .discovery(.channels)) }
        super.init(appearance: appearance)
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        grid.backgroundColor = .clear
        grid.delegate = self
        grid.alwaysBounceVertical = true
        grid.register(MusicChannelCell.self, forCellWithReuseIdentifier: "channel")
        dataSource = UICollectionViewDiffableDataSource(collectionView: grid) { [weak self] view, path, id in
            guard let self, let channel = self.channels.first(where: { $0.identity == id }) else { return nil }
            let cell = view.dequeueReusableCell(withReuseIdentifier: "channel", for: path) as! MusicChannelCell
            cell.configure(channel, theme: self.theme)
            return cell
        }
        grid.refreshControl = UIRefreshControl()
        grid.refreshControl?.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            guard let catalogue = self.catalogue else { self.grid.refreshControl?.endRefreshing(); return }
            self.runPageTask {
                await catalogue.refresh()
                self.grid.refreshControl?.endRefreshing()
            }
        }, for: .valueChanged)
        gridPage.view = grid
        pager.dataSource = self; pager.delegate = self
        addChild(modes)
        view.addSubview(modes.view); modes.view.pinEdges(to: view)
        modes.didMove(toParent: self)
        if let catalogue {
            observe(catalogue.$sections.map { _ in }.eraseToAnyPublisher()) {
                [weak self] in self?.renderCatalogue()
            }
            observe(catalogue) { [weak self] in self?.renderCatalogueState() }
        }
        observe(selection) { [weak self] in self?.renderSelection() }
        observe(model.accessChanges) { [weak self] in self?.activateCatalogue() }
    }

    public override func pageDidBecomeActive() { activateCatalogue() }
    public override func pageDidBecomeInactive() {
        observation?.cancel(); observation = nil; observationAccess = nil
        catalogue?.disappear()
        for page in pages.values { PageViewController.invalidatePages(in: page) }
        pages.removeAll()
    }
    public override func pageDidBecomeVisible() { renderCatalogueState() }
    public override func pageDidFinishAppearing() {
        if catalogue?.error != nil { renderCatalogueState() }
    }

    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded, dataSource != nil {
            for path in grid.indexPathsForVisibleItems {
                guard let id = dataSource.itemIdentifier(for: path),
                      let channel = channels.first(where: { $0.identity == id }),
                      let cell = grid.cellForItem(at: path) as? MusicChannelCell else { continue }
                cell.configure(channel, theme: theme)
            }
        }
    }

    public func select(_ channel: MusicPublicBrowseChannel) {
        if pagingAccess != nil { pendingChannel = channel; return }
        selection.selectChannel(channel)
        if !channels.contains(where: { $0.identity == channel.identity }) { channels.insert(channel, at: 0) }
        updateGrid()
        renderSelection()
    }

    public func showPicker() {
        guard canPresentPageAlert, model.isAvailable, !model.isClosed else { return }
        let access = model.accountAccessGeneration
        let picker = MusicChannelsPickerViewController(channels: channels, selectedID: selection.channel?.identity,
                                                       appearance: appearance) {
            [weak self] channel in
            guard let self, self.isPageActive, self.model.accountAccessGeneration == access else { return }
            self.select(channel)
        }
        channelPicker = picker
        picker.modalPresentationStyle = .pageSheet
        picker.sheetPresentationController?.detents = [.custom { context in
            min(620, context.maximumDetentValue * 0.72)
        }]
        picker.sheetPresentationController?.preferredCornerRadius = 24
        picker.sheetPresentationController?.prefersGrabberVisible = true
        present(picker, animated: true)
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
        pagingAccess = model.accountAccessGeneration
    }

    public func pageViewController(_ pageViewController: UIPageViewController, didFinishAnimating finished: Bool,
                                   previousViewControllers: [UIViewController], transitionCompleted completed: Bool) {
        let access = pagingAccess
        pagingAccess = nil
        if let pending = pendingChannel {
            pendingChannel = nil
            select(pending)
        } else if completed, access == model.accountAccessGeneration,
                  let shown = pager.viewControllers?.first,
                  let channel = channels.first(where: { pages[$0.identity] === shown }) {
            selection.selectChannel(channel)
            onChannelChanged?(channel)
        }
    }

    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        guard let id = dataSource.itemIdentifier(for: indexPath),
              let channel = channels.first(where: { $0.identity == id }) else { return }
        select(channel)
    }

    public func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
                               sizeForItemAt indexPath: IndexPath) -> CGSize {
        CGSize(width: max(0, (collectionView.bounds.width - 48) / 4), height: 52)
    }

    public func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
                               insetForSectionAt section: Int) -> UIEdgeInsets {
        UIEdgeInsets(top: PageLayoutMetrics.contentGap, left: 0, bottom: 0, right: 0)
    }

    private func activateCatalogue() {
        guard let catalogue, isPageActive else { return }
        catalogue.setAccess(available: model.isAvailable && !model.isClosed,
                            generation: model.accountAccessGeneration)
        catalogue.appear()
        if observationAccess != model.accountAccessGeneration {
            observation?.cancel(); observationAccess = model.accountAccessGeneration
            observation = Task { await catalogue.observeHomeChanges() }
        }
        if !catalogue.loaded, !catalogue.isLoading, catalogue.error == nil {
            runPageTask { await catalogue.loadIfNeeded() }
        }
        renderCatalogue()
    }

    private func renderCatalogue() {
        let items = catalogue?.sections.flatMap(\.items).compactMap { item -> MusicPublicBrowseChannel? in
            if case .channel(let value) = item.content { return value }; return nil
        } ?? []
        channels = items
        selection.restoreChannel(from: channels)
        if catalogue?.loaded == true, let selected = selection.channel,
           !channels.contains(where: { $0.identity == selected.identity }) {
            channels.insert(selected, at: 0)
        }
        channelPicker?.update(channels: channels, selectedID: selection.channel?.identity)
        if isViewLoaded { updateGrid(); renderSelection(); renderCatalogueState() }
    }

    private func updateGrid() {
        guard dataSource != nil else { return }
        var snapshot = NSDiffableDataSourceSnapshot<Int, String>()
        snapshot.appendSections([0])
        snapshot.appendItems(channels.map(\.identity))
        if dataSource.snapshot().itemIdentifiers != snapshot.itemIdentifiers {
            dataSource.apply(snapshot, animatingDifferences: false)
        }
        applyTheme()
    }

    private func renderCatalogueState() {
        guard isViewLoaded else { return }
        let text = channels.isEmpty ? catalogue?.isLoading == true ? "加载中" :
            catalogue?.loaded == true ? "暂无频道" : nil : nil
        if let text {
            let label = UILabel()
            label.text = text; label.textAlignment = .center; label.textColor = UIColor(theme.muted)
            grid.backgroundView = label
        } else { grid.backgroundView = nil }
        if catalogue?.error == nil { shownCatalogueError = nil; return }
        guard canPresentPageAlert, let catalogue, let error = catalogue.error else { return }
        let message = error.message(operation: catalogue.errorContext, diagnosticID: catalogue.errorDiagnosticID)
        guard shownCatalogueError != message else { return }
        let request = catalogue.actionPageID
        let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
            guard let self, catalogue.actionPageID == request else { return }
            self.shownCatalogueError = nil
            self.runPageTask { await catalogue.retry() }
        })
        shownCatalogueError = message
        present(alert, animated: true)
    }

    private func renderSelection() {
        guard isViewLoaded else { return }
        guard !channels.isEmpty, let selected = selection.channel, let page = controller(selected) else {
            modes.select(0, animated: false)
            onChannelChanged?(nil)
            return
        }
        modes.select(1, animated: false)
        if pager.viewControllers?.first !== page, pagingAccess == nil {
            pager.setViewControllers([page], direction: .forward, animated: false)
        }
        onChannelChanged?(selected)
    }

    private func controller(_ channel: MusicPublicBrowseChannel) -> MusicBrowseViewController? {
        if let existing = pages[channel.identity] { return existing }
        guard let router else { return nil }
        let page = MusicBrowseViewController(
            title: channel.title, model: model, browser: browser,
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

    isolated deinit {
        observation?.cancel()
        for page in pages.values { PageViewController.invalidatePages(in: page) }
    }
}

@MainActor
private final class MusicChannelCell: UICollectionViewCell {
    private let title = UILabel()
    override init(frame: CGRect) {
        super.init(frame: frame)
        title.font = .systemFont(ofSize: 13); title.numberOfLines = 2; title.textAlignment = .center
        contentView.addSubview(title); title.pinEdges(to: contentView)
        contentView.layer.cornerRadius = 13
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    func configure(_ channel: MusicPublicBrowseChannel, theme: PageTheme) {
        title.text = channel.title; title.textColor = UIColor(theme.ink)
        contentView.backgroundColor = UIColor(theme.chip)
        accessibilityIdentifier = "music.channel.\(channel.identity)"
    }
}

#endif
