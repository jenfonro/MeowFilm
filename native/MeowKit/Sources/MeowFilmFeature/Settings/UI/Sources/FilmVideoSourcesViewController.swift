#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmVideoSourcesViewController: FilmSourceManagementController {
    private var query = ""
    private(set) var detecting = false
    private var detectingKeys = Set<FilmSourceKey>()

    override func viewDidLoad() {
        pageTitle = "视频源"
        view.accessibilityIdentifier = "film.videos"
        manager.accessibilityIdentifier = "film.videos.managementList"
        manager.search.placeholder = "搜索视频源"
        manager.onSearch = { [weak self] in self?.query = $0; self?.render() }
        manager.onSelection = { [weak self] in self?.updateFooterMenu() }
        manager.onDelete = { [weak self] in self?.removeSelected() }
        manager.footerMoreNeedsSelection = false
        manager.configureToolbar(showsSearch: true, buttons: [])
        super.viewDidLoad()
    }

    override func render() {
        let allVideos = snapshot?.videos ?? []
        let normalizedQuery = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let videos = normalizedQuery.isEmpty ? allVideos : allVideos.filter {
            [$0.name, $0.key.installationId, $0.key.siteKey]
                .contains { $0.localizedCaseInsensitiveContains(normalizedQuery) }
        }
        let detectable = Set((snapshot?.cats ?? []).filter(\.canDetect).map(\.installationId))
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

    private func remove(_ keys: [FilmSourceKey]) {
        guard !keys.isEmpty else { return }
        presentPageConfirmation(title: "删除选中的 \(keys.count) 个视频源？", confirmTitle: "确认", destructive: true) { [weak self] in
            guard let self else { return }
            deleteBatch(keys, isPresent: { key, snapshot in
                snapshot.videos.contains { $0.key == key }
            }) { [service] in try await service.deleteVideo($0) }
        }
    }

    private func detect(_ keys: [FilmSourceKey]) {
        guard isPageActive, isPageVisible, !detecting else { return }
        detecting = true; detectingKeys = Set(keys); render()
        var completedCount = 0
        runPageTask(
            key: "detect",
            retry: { [weak self] in
                guard let self, self.isPageActive, self.isPageVisible else { return }
                self.detect(Array(keys.dropFirst(completedCount)))
            },
            onFinally: { [weak self] in
                guard let self, !Task.isCancelled else { return }
                self.detecting = false
                self.detectingKeys = []
                self.render()
            }
        ) { [weak self, service] in
            for key in keys {
                try Task.checkCancellation()
                _ = try await service.detect(key)
                completedCount += 1
                guard let self, !Task.isCancelled else { return }
                self.detectingKeys.remove(key)
                self.render()
            }
        }
    }

    private func cancelDetection() {
        let wasDetecting = detecting || !detectingKeys.isEmpty
        cancelPageTask(key: "detect")
        detecting = false
        detectingKeys = []
        if wasDetecting, isViewLoaded { render() }
    }
    override func pageDidBecomeHidden() { super.pageDidBecomeHidden(); cancelDetection() }
    override func pageDidBecomeInactive() { super.pageDidBecomeInactive(); cancelDetection() }
}

@MainActor
final class FilmVideoDetailController: FilmSourcePageController {
    let key: FilmSourceKey
    private(set) var detecting = false

    init(service: FilmSourceService, key: FilmSourceKey, themeProvider: any PageThemeProviding) {
        self.key = key; super.init(service: service, themeProvider: themeProvider); pageTitle = "视频源"
    }

    override func render() {
        guard isViewLoaded else { return }
        guard let video = snapshot?.videos.first(where: { $0.key == key }) else {
            applySections([SettingsListSection(id: "state", rows: sourceStateRows(empty: true))])
            return
        }
        pageTitle = video.name
        let canDetect = snapshot?.cats.first(where: { $0.installationId == key.installationId })?.canDetect == true
        let options: [(String, String, WritableKeyPath<FilmVideoOptions, Bool>)] = [
            ("启用", "enabled", \.enabled), ("首页显示", "home", \.homeVisible),
            ("搜索启用", "search", \.searchEnabled), ("聚合图片", "images", \.aggregateImages),
        ]
        var sections: [SettingsListSection] = [
            SettingsListSection(id: "options", title: "选项", rows: options.map { title, id, path in
                SettingsListRow(
                    id: "film.video.option.\(id)",
                    title: title,
                    accessory: .toggle(isOn: video.options[keyPath: path], enabled: !busy) { [weak self] value in
                        guard let self else { return }
                        var changed = video.options; changed[keyPath: path] = value
                        saveOptions(changed)
                    },
                    enabled: !busy
                )
            }),
            SettingsListSection(id: "order", title: "排序", rows: [
                SettingsListRow(id: "film.video.order", title: "排序", detail: String(video.order),
                                enabled: !busy, action: { [weak self] in self?.editOrder(video) }),
            ]),
            SettingsListSection(id: "actions", title: "操作", rows: [
                SettingsListRow(id: "film.video.detect", title: detecting ? "取消检测" : "检测",
                                enabled: !busy && (canDetect || detecting),
                                action: { [weak self] in self?.detect() }),
            ]),
        ]
        if let result = video.detection {
            sections.append(SettingsListSection(id: "detection", title: "检测结果", rows: [
                SettingsListRow(id: "film.video.result", title: filmSourceState(result.result), detail: filmSourceDate(result.checkedAt)),
            ] + result.steps.enumerated().map { index, step in
                SettingsListRow(id: "film.video.step.\(index)", title: "\(step.operation) · \(filmSourceState(step.state))", detail: step.error)
            }))
        }
        if busy {
            sections.append(SettingsListSection(id: "busy", rows: sourceStateRows(empty: false)))
        }
        applySections(sections)
    }

    func saveOptions(_ options: FilmVideoOptions, order: Int? = nil) {
        perform({ [service, key] in try await service.saveVideoOptions(key, options: options, order: order) })
    }

    private func editOrder(_ video: FilmVideoSource) {
        let dialog = ManagementDialog(title: "排序", action: "保存", themeProvider: themeProvider)
        let input = ManagementDialog.input(placeholder: "排序")
        input.text = String(video.order)
        input.keyboardType = .numbersAndPunctuation
        input.accessibilityIdentifier = "film.video.order.input"
        dialog.addInput(input, title: "排序")
        dialog.submit = { [weak self] in
            guard let self else { return false }
            guard let order = Int((input.text ?? "").trimmingCharacters(in: .whitespacesAndNewlines)) else {
                throw FilmSourceError(code: "排序必须是整数")
            }
            try await self.service.saveVideoOptions(video.key, options: video.options, order: order)
            return true
        }
        presentPageModal(dialog)
    }

    func detect() {
        if detecting { cancelDetection(); render(); return }
        detecting = true; render()
        runPageTask(
            key: "detect",
            retry: { [weak self] in self?.detect() },
            onFinally: { [weak self] in
                guard let self, !Task.isCancelled else { return }
                self.detecting = false
                self.render()
            }
        ) { [service, key] in
            _ = try await service.detect(key)
        }
    }

    func cancelDetection() {
        let wasDetecting = detecting
        cancelPageTask(key: "detect")
        detecting = false
        if wasDetecting, isViewLoaded { render() }
    }
    override func pageDidBecomeHidden() { super.pageDidBecomeHidden(); cancelDetection() }
    override func pageDidBecomeInactive() { super.pageDidBecomeInactive(); cancelDetection() }
}

#endif
