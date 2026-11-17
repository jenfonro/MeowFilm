#if os(iOS)
import UIKit
import MeowContracts
import MeowUI

@MainActor
final class FilmPlayerSettingsController: FilmSettingsListController {
    enum Area { case settings, controls }
    private let owner: FilmPlayerPreferencesStore
    private let area: Area
    private var displayedError: String?

    init(owner: FilmPlayerPreferencesStore, area: Area = .settings, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.area = area
        super.init(themeProvider: themeProvider)
        pageTitle = area == .settings ? "播放器设置" : "播放控制"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.accessibilityIdentifier = area == .settings ? "film.player.settings" : "film.player.controls"
        observe(owner) { [weak self] in self?.update() }
        owner.reload()
        update()
    }

    override func pageDidFinishAppearing() {
        owner.reload()
        update()
        super.pageDidFinishAppearing()
    }

    private func update() {
        switch area {
        case .settings:
            sections = [.init(title: "", rows: [
                choiceRow("默认内核", id: "engine", keyPath: \.engine, options: { _ in PlaybackVideoEngine.allCases },
                          title: { $0.title }),
                .init(title: "播放控制", id: "film.player.controls", disclosure: true, action: { [weak self] in
                    guard let self else { return }
                    navigationController?.pushViewController(FilmPlayerSettingsController(owner: owner,
                        area: .controls, themeProvider: themeProvider), animated: true)
                }),
            ])]
        case .controls:
            sections = [
                .init(title: "播放控制", rows: [
                    choiceRow("默认倍速", id: "defaultRate", keyPath: \.defaultRate,
                              options: { FilmPlayerPreferences.defaultRates(for: $0.engine) }, title: Self.rateTitle),
                    choiceRow("长按倍速", id: "holdRate", keyPath: \.holdRate,
                              options: { FilmPlayerPreferences.holdRates(for: $0.engine) }, title: Self.rateTitle),
                    choiceRow("快进/快退步长", id: "seekStep", keyPath: \.seekStep,
                              options: { _ in FilmPlayerPreferences.seekSteps }, title: { "\(Int($0)) 秒" }),
                    switchRow("控制中心显示", id: "showsControlCenter", keyPath: \.showsControlCenter),
                    switchRow("自动识别片头片尾", id: "recognizesChapters", keyPath: \.recognizesChapters),
                ]),
                .init(title: "单击手势", rows: [
                    choiceRow("单击屏幕中间", id: "singleTapCenter", keyPath: \.singleTapCenter,
                              options: { _ in [.none, .toggleControls, .playPause] }, title: { $0.title }),
                    choiceRow("单击屏幕两边", id: "singleTapSides", keyPath: \.singleTapSides,
                              options: { _ in FilmPlayerTapAction.allCases }, title: { $0.title }),
                ]),
                .init(title: "双击手势", rows: [
                    choiceRow("双击屏幕中间", id: "doubleTapCenter", keyPath: \.doubleTapCenter,
                              options: { _ in [.none, .playPause] }, title: { $0.title }),
                    choiceRow("双击屏幕两边", id: "doubleTapSides", keyPath: \.doubleTapSides,
                              options: { _ in [.none, .playPause, .seek] }, title: { $0.title }),
                ]),
                .init(title: "滑动手势", rows: [
                    switchRow("滑动调节音量", id: "swipeVolume", keyPath: \.swipeVolume),
                    switchRow("滑动调节亮度", id: "swipeBrightness", keyPath: \.swipeBrightness),
                    switchRow("滑动调节进度", id: "swipeSeek", keyPath: \.swipeSeek),
                    switchRow("短剧滑动切集", id: "swipeEpisodes", keyPath: \.swipeEpisodes),
                ]),
                .init(title: "震动反馈", rows: [
                    switchRow("长按屏幕", id: "hapticHold", keyPath: \.hapticHold),
                    switchRow("滑动调节亮度", id: "hapticBrightness", keyPath: \.hapticBrightness),
                    switchRow("滑动调节音量", id: "hapticVolume", keyPath: \.hapticVolume),
                    switchRow("滑动调节进度", id: "hapticSeek", keyPath: \.hapticSeek),
                ]),
            ]
        }
        render()
        if let error = owner.persistenceError {
            guard displayedError != error else { return }
            displayedError = error
            failure(NSError(domain: "FilmPlayerPreferences", code: 1,
                            userInfo: [NSLocalizedDescriptionKey: error]), retry: { [weak self] in
                self?.displayedError = nil
                self?.owner.reload()
                self?.update()
            })
        } else { displayedError = nil }
    }

    private static func rateTitle(_ value: Double) -> String {
        value.formatted(.number.precision(.fractionLength(0...2))) + "X"
    }

    private func choiceRow<Value: Equatable>(_ title: String, id: String,
                                             keyPath: WritableKeyPath<FilmPlayerPreferences, Value>,
                                             options: @escaping (FilmPlayerPreferences) -> [Value],
                                             title label: @escaping (Value) -> String) -> FilmSettingsRow {
        .init(title: title, detail: label(owner.preferences[keyPath: keyPath]), id: "film.player." + id,
              enabled: owner.persistenceError == nil, disclosure: true, action: { [weak self] in
            guard let self else { return }
            navigationController?.pushViewController(FilmPlayerChoiceController(owner: owner, title: title,
                id: id, keyPath: keyPath, options: options, label: label, themeProvider: themeProvider), animated: true)
        })
    }

    private func switchRow(_ title: String, id: String,
                           keyPath: WritableKeyPath<FilmPlayerPreferences, Bool>) -> FilmSettingsRow {
        filmSwitchRow(title, id: "film.player." + id, isOn: owner.preferences[keyPath: keyPath],
                      enabled: owner.persistenceError == nil) { [weak self] value in
            self?.owner.set(keyPath, value)
            self?.update()
        }
    }
}

@MainActor
private final class FilmPlayerChoiceController<Value: Equatable>: FilmSettingsListController {
    private let owner: FilmPlayerPreferencesStore
    private let id: String
    private let keyPath: WritableKeyPath<FilmPlayerPreferences, Value>
    private let options: (FilmPlayerPreferences) -> [Value]
    private let label: (Value) -> String

    init(owner: FilmPlayerPreferencesStore, title: String, id: String,
         keyPath: WritableKeyPath<FilmPlayerPreferences, Value>,
         options: @escaping (FilmPlayerPreferences) -> [Value],
         label: @escaping (Value) -> String, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.id = id; self.keyPath = keyPath
        self.options = options; self.label = label
        super.init(themeProvider: themeProvider)
        pageTitle = title
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.accessibilityIdentifier = "film.player." + id + ".options"
        observe(owner) { [weak self] in self?.update() }
        update()
    }

    override func pageDidFinishAppearing() {
        owner.reload()
        update()
        super.pageDidFinishAppearing()
    }

    private func update() {
        sections = [.init(title: "", rows: options(owner.preferences).enumerated().map { index, value in
            .init(title: label(value), id: "film.player.\(id).option.\(index)",
                  enabled: owner.persistenceError == nil, selected: owner.preferences[keyPath: keyPath] == value,
                  action: { [weak self] in
                guard let self else { return }
                if owner.set(keyPath, value) { navigationController?.popViewController(animated: true) }
                else {
                    failure(NSError(domain: "FilmPlayerPreferences", code: 1,
                        userInfo: [NSLocalizedDescriptionKey: owner.persistenceError ?? "播放器偏好保存失败"]))
                }
            })
        })]
        render()
    }
}
#endif
