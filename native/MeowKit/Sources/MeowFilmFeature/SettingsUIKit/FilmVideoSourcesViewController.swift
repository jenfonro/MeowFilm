#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmVideoSourcesViewController: FilmSourcePageController {
    private var selecting = false
    private var selected = Set<FilmSourceKey>()
    private var detection: Task<Void, Never>?
    private(set) var detecting = false
    private var detectingKeys = Set<FilmSourceKey>()
    private var detectionGeneration = UUID()

    override func viewDidLoad() { pageTitle = "视频源"; super.viewDidLoad(); view.accessibilityIdentifier = "film.videos" }
    override func render() {
        let videos = snapshot?.videos ?? []
        let detectable = Set((snapshot?.cats ?? []).filter(\.canDetect).map(\.installationId))
        selected.formIntersection(videos.map(\.key))
        var rows = videos.map { video in
            let checking = detectingKeys.contains(video.key)
            let canDetect = detectable.contains(video.key.installationId)
            return sourceRow(title: video.name,
                status: checking ? "检测中" : (video.detection.map { filmSourceState($0.result) } ?? "未检测"),
                id: "film.video.\(video.key.installationId).\(video.key.siteKey)", isOn: video.options.enabled, enabled: !busy,
                selecting: selecting, selected: selected.contains(video.key), open: { [weak self] in
                    guard let self else { return }
                    if selecting {
                        if !selected.insert(video.key).inserted { selected.remove(video.key) }; render()
                    } else {
                        navigationController?.pushViewController(FilmVideoDetailController(service: service, key: video.key,
                            themeProvider: themeProvider), animated: true)
                    }
                }, change: { [weak self] enabled in
                    guard let self else { return }
                    var options = video.options; options.enabled = enabled
                    perform({ [service] in try await service.saveVideoOptions(video.key, options: options) })
                }, actions: [
                    UIAction(title: checking ? "取消检测" : "检测",
                             attributes: (checking || (!detecting && canDetect)) ? [] : .disabled) { [weak self] _ in
                        guard let self else { return }
                        if detecting { cancelDetection(); render() } else { detect([video.key]) }
                    },
                    UIAction(title: "详情") { [weak self] _ in
                        guard let self, isPageActive, isPageVisible else { return }
                        navigationController?.pushViewController(FilmVideoDetailController(service: service, key: video.key,
                            themeProvider: themeProvider), animated: true)
                    },
                    UIAction(title: "删除", attributes: .destructive) { [weak self] _ in self?.remove([video.key]) },
                ])
        }
        rows += sourceStateRows(empty: videos.isEmpty)
        sections = [.init(title: "", rows: [
            .init(title: selecting ? "完成选择" : "选择", id: "film.videos.select", enabled: !busy && !detecting && (selecting || !videos.isEmpty),
                  action: { [weak self] in self?.selecting.toggle(); self?.render() }),
        ]), .init(title: "", rows: rows)]
        if selecting {
            sections.append(.init(title: "已选 \(selected.count)", rows: [
                .init(title: "全选", id: "film.videos.selectAll", enabled: !busy && !detecting, action: { [weak self] in self?.selected = Set(videos.map(\.key)); self?.render() }),
                .init(title: "取消选择", id: "film.videos.deselectAll", enabled: !busy && !detecting, action: { [weak self] in self?.selected.removeAll(); self?.render() }),
                .init(title: detecting ? "取消检测" : "检测", id: "film.videos.detect", enabled: !busy && (detecting || !selected.isEmpty),
                      action: { [weak self] in self?.detectSelected() }),
                .init(title: "删除", id: "film.videos.delete", enabled: !busy && !detecting && !selected.isEmpty, action: { [weak self] in
                    guard let self else { return }
                    let keys = videos.filter { self.selected.contains($0.key) }.map(\.key)
                    remove(keys)
                }),
            ]))
        }
        super.render()
    }
    private func remove(_ keys: [FilmSourceKey]) {
        confirm("删除选中的 \(keys.count) 个视频源？") { [weak self] in
            guard let self else { return }
            deleteBatch(keys, isPresent: { key, snapshot in
                snapshot.videos.contains { $0.key == key }
            }) { [service] in try await service.deleteVideo($0) }
        }
    }
    private func detectSelected() {
        if detecting { cancelDetection(); render(); return }
        let keys = (snapshot?.videos ?? []).filter { selected.contains($0.key) }.map(\.key)
        detect(keys)
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
                              enabled: !busy, ink: UIColor(theme.ink)) { [weak self] value in
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
                .init(title: "删除", id: "film.video.delete", enabled: !busy, action: { [weak self] in
                    self?.confirm("删除视频源？") { [weak self] in
                        guard let self else { return }; cancelDetection()
                        deleteBatch([key], isPresent: { key, snapshot in snapshot.videos.contains { $0.key == key } },
                            completed: { [weak self] in
                            self?.navigationController?.popViewController(animated: true)
                        }) { [service] in try await service.deleteVideo($0) }
                    }
                }),
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
        let alert = UIAlertController(title: "排序", message: nil, preferredStyle: .alert)
        alert.addTextField { field in
            field.text = String(video.order); field.keyboardType = .numbersAndPunctuation
            field.accessibilityLabel = "排序"; field.accessibilityIdentifier = "film.video.order.input"
        }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "保存", style: .default) { [weak self, weak alert] _ in
            guard let self else { return }
            guard let order = Int(alert?.textFields?.first?.text ?? "") else {
                failure(FilmSourceError(code: "排序必须是整数"), retry: { [weak self] in self?.editOrder(video) }); return
            }
            saveOptions(video.options, order: order)
        })
        present(alert, animated: true)
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
#endif
