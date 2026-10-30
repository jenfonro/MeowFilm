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
    private let refreshTrack: @MainActor (MusicRemoteTrack) async throws -> MusicRemoteTrack
    private let originIsCurrent: @MainActor () -> Bool
    private var fence: MusicReferencePlayerFence
    private let table = UITableView(frame: .zero, style: .plain)
    private let more = UIButton(type: .system)
    private var session: (any MusicSourceInspectionSession)?
    private var snapshot: MusicResolvedSourcesSnapshot?
    private var request = UUID()
    private var loading = false
    private var selecting = false
    private var inspecting = false
    private var dataSource: UITableViewDiffableDataSource<Int, String>!
    private var rows: [String: MusicResolvedSource] = [:]
    private var readError: String?
    private var shownReadError: String?

    init(model: MusicViewModel, tracks: [MusicRemoteTrack], appearance: MusicAppearancePreferencesStore,
         originIsCurrent: @escaping @MainActor () -> Bool = { true },
         refresh: @escaping @MainActor (MusicRemoteTrack) async throws -> MusicRemoteTrack = { $0 }) {
        self.model = model
        self.tracks = tracks
        refreshTrack = refresh
        self.originIsCurrent = originIsCurrent
        fence = .init(occurrence: model.current?.id, attempt: model.activeNativeAttemptID,
                      access: model.accountAccessGeneration, queueRevision: model.state.revision,
                      settingsRevision: model.state.settingsRevision)
        super.init(appearance: appearance)
        title = "选择音源"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.separatorStyle = .none
        table.delegate = self
        table.register(UITableViewCell.self, forCellReuseIdentifier: "Source")
        dataSource = UITableViewDiffableDataSource(tableView: table) { [weak self] view, path, id in
            guard let self, let source = self.rows[id] else { return nil }
            let cell = view.dequeueReusableCell(withIdentifier: "Source", for: path)
            self.configure(cell, source: source)
            return cell
        }
        view.addSubview(table)
        table.pinEdges(to: view)
        more.setTitle("加载更多", for: .normal)
        more.frame.size.height = 48
        more.addAction(UIAction { [weak self] _ in self?.loadMore(refresh: false) }, for: .touchUpInside)
        table.tableFooterView = more
        navigationItem.rightBarButtonItem = UIBarButtonItem(
            barButtonSystemItem: .refresh, target: self, action: #selector(refresh))
        observe(model.playbackChanges) { [weak self] in self?.reconcileInspection() }
        observe(model.accessChanges) { [weak self] in self?.reconcileInspection() }
        render()
    }

    override func pageDidBecomeActive() {
        if admitted { startInspection() }
        else { reconcileInspection() }
    }
    override func pageDidBecomeVisible() { renderState() }
    override func pageDidFinishAppearing() {
        if readError != nil { renderState() }
    }

    private func startInspection() {
        guard admitted, session == nil, !inspecting else { return }
        let own = UUID()
        request = own; readError = nil; shownReadError = nil; inspecting = true
        renderState()
        let model = model, tracks = tracks, refreshTrack = refreshTrack
        runPageTask { [weak self, model, tracks, refreshTrack] in
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
                let track: MusicRemoteTrack
                if self?.canSelect == true { track = target }
                else { track = try await refreshTrack(target) }
                guard !Task.isCancelled, self?.request == own, self?.admitted == true else { return }
                let value = try await model.inspectSources(for: track.ref)
                guard !Task.isCancelled, self?.request == own, self?.admitted == true else {
                    await value.close()
                    return
                }
                self?.session = value
                let updates = await value.updates()
                self?.loadMore(refresh: false)
                do {
                    for try await snapshot in updates {
                        guard !Task.isCancelled, let self, self.request == own else { break }
                        if self.selecting, self.model.current?.id == self.fence.occurrence,
                           self.model.accountAccessGeneration == self.fence.access, self.originIsCurrent() {
                            if snapshot.sources.isEmpty { self.snapshot = snapshot; self.render() }
                            continue
                        }
                        guard self.admitted else {
                            self.snapshot = nil
                            self.render()
                            self.dismiss(animated: true)
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
        snapshot = nil; rows = [:]
    }

    private func closeInspection() {
        request = UUID()
        if let session { Task { await session.close() } }
        session = nil; inspecting = false
    }

    private func reconcileInspection() {
        guard !selecting, !admitted else { return }
        closeInspection()
        snapshot = nil
        render()
        dismiss(animated: true)
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, dataSource != nil else { return }
        for path in table.indexPathsForVisibleRows ?? [] {
            if let id = dataSource.itemIdentifier(for: path), let source = rows[id],
               let cell = table.cellForRow(at: path) { configure(cell, source: source) }
        }
        renderState()
    }

    private func configure(_ cell: UITableViewCell, source: MusicResolvedSource) {
        var content = cell.defaultContentConfiguration()
        content.text = source.title
        content.secondaryText = MusicQualityLabel.name(source.selection.quality)
        content.textProperties.color = UIColor(theme.ink)
        content.secondaryTextProperties.color = UIColor(theme.muted)
        cell.contentConfiguration = content
        cell.backgroundColor = .clear
    }

    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard admitted, canSelect, !selecting, let session,
              let id = dataSource.itemIdentifier(for: indexPath), let source = rows[id] else { return }
        let own = request
        runPageTask { [weak self] in
            guard let self else { return }
            guard !Task.isCancelled, self.request == own, self.admitted, !self.selecting else { return }
            self.selecting = true
            defer { self.selecting = false }
            let occurrence = self.fence.occurrence, access = self.fence.access
            await self.model.chooseInspectedSource(source, session: session)
            guard !Task.isCancelled, self.request == own else { return }
            guard self.model.current?.id == occurrence, self.model.accountAccessGeneration == access,
                  self.originIsCurrent() else { self.dismiss(animated: true); return }
            // Only our explicit adoption may advance this inspection's fence.
            self.fence = self.currentFence
            if let error = self.model.error { self.showError(error) }
            else { self.dismiss(animated: true) }
        }
    }

    private var admitted: Bool {
        isPageActive && model.isAvailable && !model.isClosed && originIsCurrent() &&
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
        let previous = rows
        let sources = snapshot?.sources ?? []
        rows = Dictionary(uniqueKeysWithValues: sources.map { ($0.id, $0) })
        var value = NSDiffableDataSourceSnapshot<Int, String>()
        value.appendSections([0]); value.appendItems(sources.map(\.id))
        let changed = value.itemIdentifiers.filter { previous[$0] != nil && previous[$0] != rows[$0] }
        value.reconfigureItems(changed)
        if dataSource.snapshot().itemIdentifiers != value.itemIdentifiers || !changed.isEmpty {
            dataSource.apply(value, animatingDifferences: false)
        }
        renderState()
    }

    private func renderState() {
        guard isViewLoaded else { return }
        more.isHidden = snapshot?.hasMore != true
        more.isEnabled = admitted && !loading && !selecting
        more.tintColor = UIColor(theme.ink)
        let status = inspecting && snapshot == nil || snapshot?.isLoading == true ? "正在获取音源" :
            snapshot?.sources.isEmpty == true ? "暂无可用音源" : nil
        let label = UILabel()
        label.text = status
        label.textColor = UIColor(theme.muted)
        label.textAlignment = .center
        table.backgroundView = status == nil ? nil : label
        guard canPresentPageAlert, admitted, let readError, shownReadError != readError else { return }
        let own = request
        let alert = UIAlertController(title: "错误", message: readError, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
            guard let self, self.request == own, self.admitted else { return }
            self.refresh()
        })
        shownReadError = readError
        present(alert, animated: true)
    }

    private func loadMore(refresh: Bool) {
        guard admitted, !loading, let session else { return }
        let own = request
        runPageTask { [weak self] in
            guard let self else { return }
            guard !Task.isCancelled, self.request == own, self.admitted, !self.loading else { return }
            self.loading = true
            self.readError = nil; self.shownReadError = nil
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

    @objc private func refresh() {
        if session == nil { startInspection() }
        else { loadMore(refresh: true) }
    }

    isolated deinit {
        if let session { Task { await session.close() } }
    }
}
#endif
