#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicChannelsViewController: MusicPageViewController, UIPageViewControllerDataSource,
    UIPageViewControllerDelegate, UICollectionViewDataSource, UICollectionViewDelegateFlowLayout
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
    private lazy var modes = RetainedPagesController(pages: [gridPage, pager])
    private var pages: [String: MusicBrowseViewController] = [:]
    private var channels: [MusicPublicBrowseChannel] = []
    private var pagingAccess: UUID?
    private var pendingChannel: MusicPublicBrowseChannel?
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
        grid.dataSource = self; grid.delegate = self
        grid.register(MusicChannelCell.self, forCellWithReuseIdentifier: "channel")
        gridPage.view = grid
        pager.dataSource = self; pager.delegate = self
        addChild(modes)
        view.addSubview(modes.view); modes.view.pinEdges(to: view)
        modes.didMove(toParent: self)
        if let catalogue { observe(catalogue) { [weak self] in self?.renderCatalogue() } }
        observe(selection) { [weak self] in self?.renderSelection() }
        observe(model) { [weak self] in self?.activateCatalogue() }
    }

    public override func pageDidBecomeActive() { activateCatalogue() }
    public override func pageDidBecomeInactive() { catalogue?.disappear(preservingContent: true) }

    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded { grid.reloadData() }
    }

    public func select(_ channel: MusicPublicBrowseChannel) {
        if pagingAccess != nil { pendingChannel = channel; return }
        selection.selectChannel(channel)
        if !channels.contains(where: { $0.id == channel.id }) { channels.insert(channel, at: 0) }
        renderSelection()
    }

    public func showPicker() {
        let picker = MusicChannelsPickerViewController(channels: channels, appearance: appearance) {
            [weak self] channel in self?.select(channel)
        }
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
                  let channel = channels.first(where: { pages[$0.id] === shown }) {
            selection.selectChannel(channel)
            onChannelChanged?(channel)
        }
    }

    public func collectionView(_ collectionView: UICollectionView, numberOfItemsInSection section: Int) -> Int {
        channels.count
    }

    public func collectionView(_ collectionView: UICollectionView, cellForItemAt indexPath: IndexPath)
        -> UICollectionViewCell {
        let cell = collectionView.dequeueReusableCell(withReuseIdentifier: "channel", for: indexPath) as! MusicChannelCell
        cell.configure(channels[indexPath.item], theme: theme)
        return cell
    }

    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        select(channels[indexPath.item])
    }

    public func collectionView(_ collectionView: UICollectionView, layout collectionViewLayout: UICollectionViewLayout,
                               sizeForItemAt indexPath: IndexPath) -> CGSize {
        CGSize(width: max(0, (collectionView.bounds.width - 48) / 4), height: 52)
    }

    private func activateCatalogue() {
        guard let catalogue, isPageActive else { return }
        catalogue.setAccess(available: model.isAvailable && !model.isClosed,
                            generation: model.accountAccessGeneration)
        catalogue.appear()
        if !catalogue.loaded { runPageTask { await catalogue.loadIfNeeded() } }
        renderCatalogue()
    }

    private func renderCatalogue() {
        let items = catalogue?.sections.flatMap(\.items).compactMap { item -> MusicPublicBrowseChannel? in
            if case .channel(let value) = item.content { return value }; return nil
        } ?? []
        if !items.isEmpty { channels = items }
        selection.restoreChannel(from: channels)
        if let selected = selection.channel, !channels.contains(where: { $0.id == selected.id }) {
            channels.insert(selected, at: 0)
        }
        if isViewLoaded { grid.reloadData(); renderSelection() }
    }

    private func renderSelection() {
        guard isViewLoaded else { return }
        guard let selected = selection.channel, let page = controller(selected) else {
            modes.select(0, animated: false)
            return
        }
        modes.select(1, animated: false)
        if pager.viewControllers?.first !== page, pagingAccess == nil {
            pager.setViewControllers([page], direction: .forward, animated: false)
        }
        onChannelChanged?(selected)
    }

    private func controller(_ channel: MusicPublicBrowseChannel) -> MusicBrowseViewController? {
        if let existing = pages[channel.id] { return existing }
        guard let router else { return nil }
        let page = MusicBrowseViewController(
            title: channel.title, model: model, browser: browser,
            request: .discovery(.channel(channel, tag: "")), appearance: appearance,
            router: router, embedded: true
        )
        pages[channel.id] = page
        return page
    }

    private func neighbor(of viewController: UIViewController, step: Int) -> UIViewController? {
        guard let index = channels.firstIndex(where: { pages[$0.id] === viewController }),
              channels.indices.contains(index + step) else { return nil }
        return controller(channels[index + step])
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
        accessibilityIdentifier = "music.channel.\(channel.id)"
    }
}

@MainActor
private final class MusicChannelsPickerViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    private let channels: [MusicPublicBrowseChannel]
    private let onSelect: (MusicPublicBrowseChannel) -> Void
    private let table = UITableView(frame: .zero, style: .plain)
    init(channels: [MusicPublicBrowseChannel], appearance: MusicAppearancePreferencesStore,
         onSelect: @escaping (MusicPublicBrowseChannel) -> Void) {
        self.channels = channels; self.onSelect = onSelect
        super.init(appearance: appearance)
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear; table.dataSource = self; table.delegate = self
        view.addSubview(table); table.pinEdges(to: view)
        table.rowHeight = 52
    }
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { channels.count }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let cell = UITableViewCell(style: .default, reuseIdentifier: nil)
        cell.textLabel?.text = channels[indexPath.row].title
        cell.textLabel?.textColor = UIColor(theme.ink); cell.backgroundColor = .clear
        return cell
    }
    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        let channel = channels[indexPath.row]
        dismiss(animated: true) { [onSelect] in onSelect(channel) }
    }
}
#endif
