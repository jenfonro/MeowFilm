#if os(iOS)
import UIKit
import MeowUI

/// Inspecting resolved resources does not prepare, stop or replace playback.
/// Only a selection may adopt a source through the existing music service.
@MainActor
final class MusicSourcesViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
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
        table.dataSource = self
        table.delegate = self
        table.register(UITableViewCell.self, forCellReuseIdentifier: "Source")
        view.addSubview(table)
        table.pinEdges(to: view)
        more.setTitle("加载更多", for: .normal)
        more.frame.size.height = 48
        more.addAction(UIAction { [weak self] _ in self?.loadMore(refresh: false) }, for: .touchUpInside)
        table.tableFooterView = more
        navigationItem.rightBarButtonItem = UIBarButtonItem(
            barButtonSystemItem: .refresh, target: self, action: #selector(refresh))
        observe(model) { [weak self] in
            guard let self else { return }
            if !self.selecting, !self.admitted { self.dismiss(animated: true) }
        }
    }

    override func pageDidBecomeActive() {
        guard admitted, session == nil, !inspecting else { return }
        let own = UUID()
        request = own
        runPageTask { [weak self] in
            guard let self else { return }
            guard !Task.isCancelled, self.request == own, self.admitted,
                  self.session == nil, !self.inspecting else { return }
            self.inspecting = true
            defer {
                self.inspecting = false
                if self.isPageActive, self.request != own, self.session == nil { self.pageDidBecomeActive() }
            }
            do {
                guard let target = self.model.current.flatMap({ current in
                    self.tracks.contains { MusicSearchFusion.sameRecording($0, current.track) } ? current.track : nil
                }) ?? self.tracks.first else { return }
                let track: MusicRemoteTrack
                if self.canSelect { track = target }
                else { track = try await self.refreshTrack(target) }
                guard !Task.isCancelled, self.request == own, self.admitted else { return }
                let value = try await self.model.inspectSources(for: track.ref)
                guard !Task.isCancelled, self.request == own, self.admitted else {
                    await value.close()
                    return
                }
                self.session = value
                let updates = await value.updates()
                self.loadMore(refresh: false)
                do {
                    for try await snapshot in updates {
                        guard !Task.isCancelled, self.request == own, self.admitted else { break }
                        self.snapshot = snapshot
                        self.render()
                    }
                } catch {
                    if !Task.isCancelled, self.request == own, self.admitted {
                        self.showError(MusicServiceError.sanitized(error).localizedDescription)
                    }
                }
                await value.close()
                if self.request == own { self.session = nil }
            } catch {
                if !Task.isCancelled, self.request == own, self.admitted {
                    self.showError(MusicServiceError.sanitized(error).localizedDescription)
                }
            }
        }
    }

    override func pageDidBecomeInactive() {
        request = UUID()
        session = nil
    }

    override func applyTheme() {
        super.applyTheme()
        if isViewLoaded { render() }
    }

    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        snapshot?.sources.count ?? 0
    }

    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let source = snapshot!.sources[indexPath.row]
        let cell = tableView.dequeueReusableCell(withIdentifier: "Source", for: indexPath)
        var content = cell.defaultContentConfiguration()
        content.text = source.title
        content.secondaryText = MusicQualityLabel.name(source.selection.quality)
        content.textProperties.color = UIColor(theme.ink)
        content.secondaryTextProperties.color = UIColor(theme.muted)
        cell.contentConfiguration = content
        cell.backgroundColor = .clear
        return cell
    }

    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard admitted, canSelect, !selecting, let session, let source = snapshot?.sources[indexPath.row] else { return }
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
        table.reloadData()
        more.isHidden = snapshot?.hasMore != true
        more.tintColor = UIColor(theme.ink)
        let status = snapshot?.isLoading == true ? "正在获取音源" :
            snapshot?.sources.isEmpty == true ? "暂无可用音源" : nil
        let label = UILabel()
        label.text = status
        label.textColor = UIColor(theme.muted)
        label.textAlignment = .center
        table.backgroundView = status == nil ? nil : label
    }

    private func loadMore(refresh: Bool) {
        guard admitted, !loading, let session else { return }
        let own = request
        runPageTask { [weak self] in
            guard let self else { return }
            guard !Task.isCancelled, self.request == own, self.admitted, !self.loading else { return }
            self.loading = true
            defer { self.loading = false }
            do {
                let value = try await session.loadMore(refresh: refresh)
                guard !Task.isCancelled, self.request == own, self.admitted else { return }
                self.snapshot = value
                self.render()
            } catch {
                if !Task.isCancelled, self.request == own, self.admitted {
                    self.showError(MusicServiceError.sanitized(error).localizedDescription)
                }
            }
        }
    }

    @objc private func refresh() { loadMore(refresh: true) }
}
#endif
