#if os(iOS)
import UIKit
import MeowUI

/// Inspecting resolved resources does not prepare, stop or replace playback.
/// Only a selection may adopt a source through the existing music service.
@MainActor
final class MusicSourcesViewController: MusicPageViewController,
    UITableViewDelegate
{
    private let model: MusicViewModel
    private let tracks: [MusicRemoteTrack]
    private let originIsCurrent: @MainActor () -> Bool
    private var fence: MusicReferencePlayerFence
    private let table = UITableView(frame: .zero, style: .plain)
    private let more = UIButton(type: .system)
    private let emptyLabel = UILabel()
    private var session: (any MusicSourceInspectionSession)?
    private var snapshot: MusicResolvedSourcesSnapshot?
    private var request = UUID()
    private var loading = false
    private var selecting = false
    /// "Not this song" in flight: keeps rows visible, holds selection/paging.
    private var rejecting = false
    private var inspecting = false
    private var dataSource: UITableViewDiffableDataSource<Int, String>!
    private var rowProjection = IndexedDiffableProjection<Int, String, MusicResolvedSource>()
    private var readError: String?

    init(model: MusicViewModel, tracks: [MusicRemoteTrack], appearance: MusicAppearancePreferencesStore,
         originIsCurrent: @escaping @MainActor () -> Bool = { true }) {
        self.model = model
        self.tracks = tracks
        self.originIsCurrent = originIsCurrent
        fence = .init(occurrence: model.current?.id, attempt: model.activeNativeAttemptID,
                      access: model.accountAccessGeneration, queueRevision: model.state.revision,
                      settingsRevision: model.state.settingsRevision)
        super.init(appearance: appearance)
        pageTitle = "选择音源"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        let close = PageHeaderButton(image: PageIcon.image(.close, size: 18), accessibilityLabel: "关闭") {
            [weak self] in self?.dismissSelf(animated: true)
        }
        close.accessibilityIdentifier = "page.header.close"
        pageHeader.setLeading(close)
        table.backgroundColor = .clear
        table.separatorStyle = .none
        table.delegate = self
        table.register(MusicSourceRowCell.self, forCellReuseIdentifier: MusicSourceRowCell.reuseIdentifier)
        dataSource = UITableViewDiffableDataSource(tableView: table) { [weak self] view, path, id in
            guard let self, let source = self.rowProjection[id],
                  let cell = view.dequeueReusableCell(
                      withIdentifier: MusicSourceRowCell.reuseIdentifier, for: path
                  ) as? MusicSourceRowCell else { return nil }
            cell.configure(source: source, theme: self.theme)
            return cell
        }
        view.addSubview(table)
        table.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            table.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            table.topAnchor.constraint(equalTo: contentTopAnchor),
            table.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        more.setTitle("加载更多", for: .normal)
        more.frame.size.height = 48
        more.addAction(UIAction { [weak self] _ in self?.loadMore(refresh: false) }, for: .touchUpInside)
        table.tableFooterView = more
        pageHeader.setTrailing([PageHeaderButton(
            image: UIImage(systemName: "arrow.clockwise") ?? PageIcon.image(.playing),
            accessibilityLabel: "刷新"
        ) { [weak self] in self?.refresh() }])
        observe(model.playbackChanges) { [weak self] in self?.reconcileInspection() }
        observe(model.accessChanges) { [weak self] in self?.reconcileInspection() }
        render()
    }

    override func pageDidBecomeActive() {
        if admitted { startInspection() }
        else { reconcileInspection() }
    }
    override func pageDidBecomeVisible() { renderState() }

    private func startInspection() {
        guard admitted, session == nil, !inspecting else { return }
        let own = UUID()
        request = own; readError = nil; inspecting = true
        clearErrorSource("read")
        renderState()
        let model = model, tracks = tracks
        runPageTask(key: "inspect") { [weak self, model, tracks] in
            defer {
                if let self, self.request == own {
                    self.inspecting = false
                    self.renderState()
                    self.reconcileInspection()
                }
            }
            guard !Task.isCancelled, self?.request == own, self?.admitted == true else { return }
            do {
                guard let target = model.current.flatMap({ current in
                    tracks.contains { MusicSearchFusion.sameRecording($0, current.track) } ? current.track : nil
                }) ?? tracks.first else { return }
                guard !Task.isCancelled, self?.request == own, self?.admitted == true else { return }
                let value = try await model.inspectSources(for: target.ref)
                guard !Task.isCancelled, self?.request == own, self?.admitted == true else {
                    await value.close()
                    return
                }
                self?.session = value
                let updates = await value.updates()
                do {
                    for try await snapshot in updates {
                        guard !Task.isCancelled, let self, self.request == own else { break }
                        if self.rejecting, self.model.current?.id == self.fence.occurrence,
                           self.model.accountAccessGeneration == self.fence.access, self.originIsCurrent() {
                            self.snapshot = snapshot; self.render()
                            continue
                        }
                        if self.selecting, self.model.current?.id == self.fence.occurrence,
                           self.model.accountAccessGeneration == self.fence.access, self.originIsCurrent() {
                            self.snapshot = snapshot; self.render()
                            continue
                        }
                        guard self.admitted else {
                            self.snapshot = nil
                            self.render()
                            (self.navigationController ?? self).dismissSelf(animated: true)
                            break
                        }
                        self.snapshot = snapshot
                        self.render()
                    }
                } catch {
                    if !Task.isCancelled, let self, self.request == own, self.admitted {
                        self.readError = MusicServiceError.sanitized(error).localizedDescription
                        self.renderState()
                    }
                }
                if self?.request == own {
                    await value.close()
                    if self?.request == own { self?.session = nil }
                }
            } catch {
                if !Task.isCancelled, let self, self.request == own, self.admitted {
                    self.readError = MusicServiceError.sanitized(error).localizedDescription
                    self.renderState()
                }
            }
        }
    }

    override func pageDidBecomeInactive() {
        closeInspection()
        snapshot = nil
        rowProjection = IndexedDiffableProjection()
    }

    private func closeInspection() {
        request = UUID()
        cancelPageTask(key: "inspect")
        if let session { Task { await session.close() } }
        session = nil; inspecting = false
    }

    private func reconcileInspection() {
        guard !selecting, !rejecting, !admitted else { return }
        let shouldDismiss = isPageActive
        closeInspection()
        snapshot = nil
        render()
        if shouldDismiss { (navigationController ?? self).dismissSelf(animated: true) }
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, dataSource != nil else { return }
        more.tintColor = UIColor(theme.ink)
        emptyLabel.textColor = UIColor(theme.muted)
        for path in table.indexPathsForVisibleRows ?? [] {
            if let id = dataSource.itemIdentifier(for: path), let source = rowProjection[id],
               let cell = table.cellForRow(at: path) as? MusicSourceRowCell {
                cell.configure(source: source, theme: theme)
            }
        }
    }

    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard admitted, canSelect, !selecting, !rejecting, let session,
              let id = dataSource.itemIdentifier(for: indexPath), let source = rowProjection[id] else { return }
        let own = request
        runPageTask { [weak self] in
            guard let self else { return }
            guard !Task.isCancelled, self.request == own, self.admitted, !self.selecting else { return }
            self.selecting = true
            defer { self.selecting = false; self.renderState() }
            let occurrence = self.fence.occurrence, access = self.fence.access
            await self.model.chooseInspectedSource(source, session: session)
            guard !Task.isCancelled, self.request == own else { return }
            guard self.model.current?.id == occurrence, self.model.accountAccessGeneration == access,
                  self.originIsCurrent() else { self.dismissSelf(animated: true); return }
            // Only our explicit adoption may advance this inspection's fence.
            self.fence = self.currentFence
            if let error = self.model.notices.error {
                self.enqueueError(error, acknowledged: { [weak model = self.model] in
                    if model?.notices.error == error { model?.notices.error = nil }
                })
            } else { self.dismissSelf(animated: true) }
        }
    }

    /// The primary recording defines the song and is never offered.
    func tableView(_ tableView: UITableView, trailingSwipeActionsConfigurationForRowAt indexPath: IndexPath)
        -> UISwipeActionsConfiguration? {
        guard admitted, canSelect, !selecting, !rejecting, session != nil,
              let id = dataSource.itemIdentifier(for: indexPath), let source = rowProjection[id],
              source.selection.sourceKey != model.current?.track.sources.first?.sourceKey else { return nil }
        let reject = UIContextualAction(style: .destructive, title: "不是这首歌") { [weak self] _, _, finish in
            self?.reject(source)
            finish(true)
        }
        let configuration = UISwipeActionsConfiguration(actions: [reject])
        configuration.performsFirstActionWithFullSwipe = false
        return configuration
    }

    private func reject(_ source: MusicResolvedSource) {
        guard admitted, canSelect, !selecting, !rejecting, let session else { return }
        let own = request
        runPageTask { [weak self] in
            guard let self else { return }
            guard !Task.isCancelled, self.request == own, self.admitted, !self.selecting, !self.rejecting else { return }
            self.rejecting = true
            self.renderState()
            defer { self.rejecting = false; self.renderState() }
            let occurrence = self.fence.occurrence, access = self.fence.access
            await self.model.rejectInspectedSource(source, session: session)
            guard !Task.isCancelled, self.request == own else { return }
            guard self.model.current?.id == occurrence, self.model.accountAccessGeneration == access,
                  self.originIsCurrent() else { self.dismissSelf(animated: true); return }
            // Only our explicit rejection (and its replacement) may advance the fence.
            self.fence = self.currentFence
            if let error = self.model.notices.error {
                self.enqueueError(error, acknowledged: { [weak model = self.model] in
                    if model?.notices.error == error { model?.notices.error = nil }
                })
            }
        }
    }

    private var admitted: Bool {
        isPageActive && !model.isClosed && originIsCurrent() &&
            fence == currentFence
    }

    private var currentFence: MusicReferencePlayerFence {
        MusicReferencePlayerFence(
                occurrence: model.current?.id, attempt: model.activeNativeAttemptID,
                access: model.accountAccessGeneration, queueRevision: model.state.revision,
                settingsRevision: model.state.settingsRevision)
    }

    private var canSelect: Bool {
        model.current.map { current in
            tracks.contains { MusicSearchFusion.sameRecording($0, current.track) }
        } == true && !model.isResolving
    }

    private func render() {
        guard dataSource != nil else { return }
        let update = rowProjection.update(section: 0, items: snapshot?.sources ?? [], id: \.id)
        dataSource.applyIfNeeded(update, animatingDifferences: false)
        renderState()
    }

    private func renderState() {
        guard isViewLoaded else { return }
        more.isHidden = snapshot?.hasMore != true
        more.isEnabled = admitted && !loading && snapshot?.isLoading != true && !selecting && !rejecting
        more.tintColor = UIColor(theme.ink)
        let status = inspecting && snapshot == nil || snapshot?.isLoading == true ? "正在获取音源" :
            snapshot?.sources.isEmpty == true ? "暂无可用音源" : nil
        emptyLabel.text = status
        emptyLabel.textColor = UIColor(theme.muted)
        emptyLabel.textAlignment = .center
        table.backgroundView = status == nil ? nil : emptyLabel
        guard admitted else { return }
        let own = request
        syncErrorSource("read", message: readError, token: own, retry: { [weak self] in
            guard let self, self.request == own, self.admitted else { return }
            self.refresh()
        })
    }

    private func loadMore(refresh: Bool) {
        guard admitted, !loading, snapshot?.isLoading != true, let session else { return }
        let own = request
        runPageTask { [weak self] in
            guard let self else { return }
            guard !Task.isCancelled, self.request == own, self.admitted, !self.loading, !self.rejecting else { return }
            self.loading = true
            self.readError = nil
            self.clearErrorSource("read")
            self.renderState()
            defer { self.loading = false; self.renderState() }
            do {
                let value = try await session.loadMore(refresh: refresh)
                guard !Task.isCancelled, self.request == own, self.admitted else { return }
                self.snapshot = value
                self.render()
            } catch {
                if !Task.isCancelled, self.request == own, self.admitted {
                    self.readError = MusicServiceError.sanitized(error).localizedDescription
                    self.renderState()
                }
            }
        }
    }

    private func refresh() {
        if session == nil { startInspection() }
        else { loadMore(refresh: true) }
    }

    nonisolated deinit {
        MainActor.assumeIsolated {
            if let session { Task { await session.close() } }
        }
    }
}

@MainActor
private final class MusicSourceRowCell: UITableViewCell {
    static let reuseIdentifier = "music.source.row"

    private let titleLabel = UILabel()
    private let detailLabel = UILabel()

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        titleLabel.font = .preferredFont(forTextStyle: .body)
        titleLabel.adjustsFontForContentSizeCategory = true
        titleLabel.numberOfLines = 0
        detailLabel.font = .preferredFont(forTextStyle: .subheadline)
        detailLabel.adjustsFontForContentSizeCategory = true
        detailLabel.numberOfLines = 0
        let stack = UIStackView(arrangedSubviews: [titleLabel, detailLabel])
        stack.axis = .vertical
        stack.spacing = 4
        stack.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 16),
            stack.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -16),
            stack.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 10),
            stack.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -10),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        titleLabel.text = nil
        detailLabel.text = nil
    }

    func configure(source: MusicResolvedSource, theme: PageTheme) {
        let platform = MusicPublicBrowsePlatform(rawValue: source.selection.siteId)?.title ?? source.selection.siteId
        let quality = source.delivery.map { MusicQualityLabel.name($0.quality) }
            ?? "请求\(MusicQualityLabel.name(source.selection.quality)) · 实际音质未确认"
        titleLabel.text = source.title
        titleLabel.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
        titleLabel.textColor = UIColor(theme.ink)
        detailLabel.text = platform + " · " + quality + "\n录音 " + source.selection.sourceKey
        detailLabel.font = .preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection)
        detailLabel.textColor = UIColor(theme.muted)
    }
}
#endif
