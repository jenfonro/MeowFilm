#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmVideoSourcesViewController: FilmSourcePageController {
    override var usesSettingsTable: Bool { false }
    let manager = ManagementListView()
    private var query = ""
    private lazy var sortButton = ManagementListView.icon("management.sort", label: "排序视频源") { [weak self] in
        self?.openOrder()
    }
    private var detection: Task<Void, Never>?
    private(set) var detecting = false
    private var detectingKeys = Set<FilmSourceKey>()
    private var detectionGeneration = UUID()

    override func viewDidLoad() {
        pageTitle = "视频源"
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.videos"
        manager.accessibilityIdentifier = "film.videos.managementList"
        manager.search.placeholder = "搜索视频源"
        manager.onSearch = { [weak self] in self?.query = $0; self?.render() }
        manager.onSelection = { [weak self] in self?.updateFooterMenu() }
        manager.onDelete = { [weak self] in self?.removeSelected() }
        manager.footerMoreNeedsSelection = false
        sortButton.accessibilityIdentifier = "film.videos.sort"
        manager.tools.addArrangedSubview(sortButton)
        view.addSubview(manager); manager.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            manager.topAnchor.constraint(equalTo: contentTopAnchor),
            manager.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            manager.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            manager.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        manager.applyTheme(theme)
        render()
    }

    override func render() {
        let allVideos = snapshot?.videos ?? []
        let normalizedQuery = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let videos = normalizedQuery.isEmpty ? allVideos : allVideos.filter {
            [$0.name, $0.key.installationId, $0.key.siteKey]
                .contains { $0.localizedCaseInsensitiveContains(normalizedQuery) }
        }
        let detectable = Set((snapshot?.cats ?? []).filter(\.canDetect).map(\.installationId))
        sortButton.isEnabled = !busy && !detecting && allVideos.count > 1
        let rows = videos.map { video in
            let identifier = rowID(video.key)
            let checking = detectingKeys.contains(video.key)
            let canDetect = detectable.contains(video.key.installationId)
            let status = checking ? "检测中" : (video.detection.map { filmSourceState($0.result) } ?? "未检测")
            let open = { [weak self] in
                guard let self, isPageActive, isPageVisible else { return }
                navigationController?.pushViewController(FilmVideoDetailController(service: service, key: video.key,
                    themeProvider: themeProvider), animated: true)
            }
            return ManagementListView.Row(id: identifier, title: video.name,
                detail: status, enabled: video.options.enabled, failed: video.detection?.result == "failed",
                controlsEnabled: !busy,
                edit: open, open: open, toggle: { [weak self] enabled in
                    guard let self, !busy else { return }
                    var options = video.options; options.enabled = enabled
                    perform({ [service] in try await service.saveVideoOptions(video.key, options: options) })
                }, menu: UIMenu(children: [
                    UIAction(title: checking ? "取消检测" : "检测",
                             attributes: (checking || (!detecting && canDetect)) ? [] : .disabled) { [weak self] _ in
                        guard let self else { return }
                        if detecting { cancelDetection(); render() } else { detect([video.key]) }
                    },
                ]))
        }
        manager.setRows(rows, total: allVideos.count,
                        validIDs: Set(allVideos.map { rowID($0.key) }),
                        emptyMessage: snapshot == nil ? "加载中" :
                            (allVideos.isEmpty ? "暂无视频源" : "没有匹配的视频源"))
        updateFooterMenu()
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        manager.applyTheme(theme)
        render()
    }

    private func rowID(_ key: FilmSourceKey) -> String {
        "film.video.\(key.installationId).\(key.siteKey)"
    }

    private func updateFooterMenu() {
        let videos = snapshot?.videos ?? []
        let selected = videos.filter { manager.selected.contains(rowID($0.key)) }
        let canDetect = Set((snapshot?.cats ?? []).filter(\.canDetect).map(\.installationId))
        let hasDetectableSelection = selected.contains { canDetect.contains($0.key.installationId) }
        manager.deleteButton.isEnabled = !busy && !detecting && !selected.isEmpty
        manager.footerMore.isEnabled = detecting || (!busy && hasDetectableSelection)
        manager.footerMore.menu = UIMenu(children: [
            UIAction(title: detecting ? "取消检测" : "检测所选",
                     attributes: (detecting || hasDetectableSelection) ? [] : .disabled) { [weak self] _ in
                guard let self else { return }
                if detecting { cancelDetection(); render() } else { detect(selected.map(\.key)) }
            },
        ])
    }

    private func removeSelected() {
        guard !busy, !detecting else { return }
        let keys = (snapshot?.videos ?? []).filter { manager.selected.contains(rowID($0.key)) }.map(\.key)
        remove(keys)
    }

    private func openOrder() {
        guard let videos = snapshot?.videos, videos.count > 1,
              let navigationController, navigationController.topViewController === self else { return }
        navigationController.pushViewController(
            FilmVideoSourceOrderController(service: service, videos: videos, themeProvider: themeProvider),
            animated: true
        )
    }

    private func remove(_ keys: [FilmSourceKey]) {
        guard !keys.isEmpty else { return }
        confirm("删除选中的 \(keys.count) 个视频源？") { [weak self] in
            guard let self else { return }
            deleteBatch(keys, isPresent: { key, snapshot in
                snapshot.videos.contains { $0.key == key }
            }) { [service] in try await service.deleteVideo($0) }
        }
    }
    private func detect(_ keys: [FilmSourceKey]) {
        guard isPageActive, isPageVisible, !detecting else { return }
        let pageGeneration = sourcePageGeneration
        let generation = UUID(); detectionGeneration = generation; detecting = true; detectingKeys = Set(keys); render()
        detection = Task { [weak self, service] in
            var completedCount = 0
            do {
                for key in keys {
                    try Task.checkCancellation()
                    _ = try await service.detect(key)
                    completedCount += 1
                    guard let self, !Task.isCancelled, detectionGeneration == generation else { return }
                    detectingKeys.remove(key); render()
                }
                guard let self, !Task.isCancelled, detectionGeneration == generation else { return }
                detecting = false; detection = nil; detectingKeys = []; render()
            } catch {
                guard let self, !Task.isCancelled, detectionGeneration == generation else { return }
                detecting = false; detection = nil; detectingKeys = []; render()
                let remaining = Array(keys.dropFirst(completedCount))
                failure(error, retry: { [weak self] in
                    guard let self, canContinueSourceBatch(pageGeneration) else { return }
                    detect(remaining)
                })
            }
        }
    }
    private func cancelDetection() {
        detectionGeneration = UUID(); detection?.cancel(); detection = nil; detecting = false; detectingKeys = []
    }
    override func pageDidBecomeHidden() { super.pageDidBecomeHidden(); cancelDetection() }
    override func pageDidBecomeInactive() { super.pageDidBecomeInactive(); cancelDetection() }
    override func viewWillAppear(_ animated: Bool) { super.viewWillAppear(animated); render() }
}

@MainActor
final class FilmVideoDetailController: FilmSourcePageController {
    let key: FilmSourceKey
    private(set) var detecting = false
    private var detection: Task<Void, Never>?
    private var detectionGeneration = UUID()
    init(service: FilmSourceService, key: FilmSourceKey, themeProvider: any PageThemeProviding) {
        self.key = key; super.init(service: service, themeProvider: themeProvider); pageTitle = "视频源"
    }
    override func render() {
        guard let video = snapshot?.videos.first(where: { $0.key == key }) else {
            sections = [.init(title: "", rows: sourceStateRows(empty: true))]; super.render(); return
        }
        pageTitle = video.name
        let canDetect = snapshot?.cats.first(where: { $0.installationId == key.installationId })?.canDetect == true
        let options: [(String, String, WritableKeyPath<FilmVideoOptions, Bool>)] = [
            ("启用", "enabled", \.enabled), ("首页显示", "home", \.homeVisible),
            ("搜索启用", "search", \.searchEnabled), ("聚合图片", "images", \.aggregateImages),
        ]
        sections = [.init(title: "选项", rows: options.map { title, id, path in
                filmSwitchRow(title, id: "film.video.option.\(id)", isOn: video.options[keyPath: path],
                              enabled: !busy) { [weak self] value in
                    guard let self else { return }
                    var changed = video.options; changed[keyPath: path] = value
                    saveOptions(changed)
                }
            }),
            .init(title: "排序", rows: [.init(title: "排序", detail: String(video.order), id: "film.video.order",
                enabled: !busy, action: { [weak self] in self?.editOrder(video) })]),
            .init(title: "操作", rows: [
                .init(title: detecting ? "取消检测" : "检测", id: "film.video.detect", enabled: !busy && (canDetect || detecting),
                      action: { [weak self] in self?.detect() }),
            ]),
        ]
        if let result = video.detection {
            sections.append(.init(title: "检测结果", rows: [
                .init(title: filmSourceState(result.result), detail: filmSourceDate(result.checkedAt), id: "film.video.result"),
            ] + result.steps.enumerated().map { index, step in
                .init(title: "\(step.operation) · \(filmSourceState(step.state))", detail: step.error, id: "film.video.step.\(index)")
            }))
        }
        if busy { sections.append(.init(title: "", rows: sourceStateRows(empty: false))) }
        super.render()
    }
    func saveOptions(_ options: FilmVideoOptions, order: Int? = nil) {
        perform({ [service, key] in try await service.saveVideoOptions(key, options: options, order: order) })
    }
    private func editOrder(_ video: FilmVideoSource) {
        guard canPresentPageAlert else { return }
        let dialog = ManagementDialog(title: "排序", action: "保存", themeProvider: themeProvider)
        let input = ManagementDialog.input(placeholder: "排序")
        input.text = String(video.order)
        input.keyboardType = .numbersAndPunctuation
        input.accessibilityIdentifier = "film.video.order.input"
        dialog.body.addArrangedSubview(input)
        dialog.submit = { [weak self] in
            guard let self else { return false }
            guard let order = Int(input.text ?? "") else {
                throw FilmSourceError(code: "排序必须是整数")
            }
            try await self.service.saveVideoOptions(video.key, options: video.options, order: order)
            return true
        }
        present(dialog, animated: true)
    }
    func detect() {
        if detecting { cancelDetection(); render(); return }
        let generation = UUID(); detectionGeneration = generation; detecting = true; render()
        detection = Task { [weak self, service, key] in
            do {
                _ = try await service.detect(key)
                guard let self, !Task.isCancelled, detectionGeneration == generation else { return }
                detecting = false; detection = nil; render()
            } catch {
                guard let self, !Task.isCancelled, detectionGeneration == generation else { return }
                detecting = false; detection = nil; render(); failure(error, retry: { [weak self] in self?.detect() })
            }
        }
    }
    func cancelDetection() {
        detectionGeneration = UUID(); detection?.cancel(); detection = nil; detecting = false
    }
    override func pageDidBecomeHidden() { super.pageDidBecomeHidden(); cancelDetection() }
    override func pageDidBecomeInactive() { super.pageDidBecomeInactive(); cancelDetection() }
    override func viewWillAppear(_ animated: Bool) { super.viewWillAppear(animated); render() }
}

@MainActor
final class FilmVideoSourceOrderController: FilmSourcePageController {
    private var ordered: [FilmVideoSource]
    private var saving = false

    init(service: FilmSourceService, videos: [FilmVideoSource],
         themeProvider: any PageThemeProviding) {
        ordered = videos
        super.init(service: service, themeProvider: themeProvider)
        pageTitle = "视频源排序"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.videos.order"
        render()
    }

    override func pageDidBecomeVisible() {
        if !saving, let snapshot { ordered = snapshot.videos }
        render()
    }

    override func render() {
        sections = [.init(title: "", rows: ordered.enumerated().map { index, video in
            let title = UILabel()
            title.text = video.name
            title.textColor = UIColor(theme.settings.ink)
            title.font = .preferredFont(forTextStyle: .body)
            title.adjustsFontForContentSizeCategory = true
            title.numberOfLines = 0

            let up = PageHeaderButton(image: UIImage(systemName: "chevron.up")!,
                accessibilityLabel: "\(video.name) 上移") { [weak self] in
                    self?.move(from: index, to: index - 1)
                }
            up.accessibilityIdentifier = "film.video.order.\(index).up"
            up.isEnabled = !saving && index > 0
            up.tintColor = UIColor(theme.settings.ink)

            let down = PageHeaderButton(image: UIImage(systemName: "chevron.down")!,
                accessibilityLabel: "\(video.name) 下移") { [weak self] in
                    self?.move(from: index, to: index + 1)
                }
            down.accessibilityIdentifier = "film.video.order.\(index).down"
            down.isEnabled = !saving && index + 1 < ordered.count
            down.tintColor = UIColor(theme.settings.ink)

            let controls = UIStackView(arrangedSubviews: [up, down])
            controls.axis = .horizontal
            controls.spacing = 0
            controls.setContentHuggingPriority(.required, for: .horizontal)
            let row = UIStackView(arrangedSubviews: [title, controls])
            row.axis = .horizontal
            row.alignment = .center
            row.spacing = 12
            return FilmSettingsRow(title: video.name, detail: "第 \(index + 1) 位",
                id: "film.video.order.\(index)", enabled: !saving, content: row)
        })]
        super.render()
    }

    private func move(from: Int, to: Int) {
        guard isPageActive, isPageVisible, !saving,
              ordered.indices.contains(from), ordered.indices.contains(to) else { return }
        ordered.swapAt(from, to)
        save()
    }

    private func save() {
        guard !saving else { return }
        let values = ordered
        let generation = sourcePageGeneration
        saving = true
        render()
        Task { [weak self, service] in
            do {
                for (index, video) in values.enumerated() {
                    guard self?.canContinueSourceBatch(generation) == true else { break }
                    let current = await service.snapshot
                    guard self?.canContinueSourceBatch(generation) == true else { break }
                    guard let source = current.videos.first(where: { $0.key == video.key }) else {
                        throw FilmSourceError(code: "NOT_FOUND")
                    }
                    if source.order != index {
                        try await service.saveVideoOptions(source.key, options: source.options, order: index)
                    }
                }
                guard let self else { return }
                ordered = (await service.snapshot).videos
                saving = false
                render()
            } catch {
                guard let self else { return }
                saving = false
                render()
                failure(error, retry: { [weak self] in self?.save() })
            }
        }
    }
}
#endif
