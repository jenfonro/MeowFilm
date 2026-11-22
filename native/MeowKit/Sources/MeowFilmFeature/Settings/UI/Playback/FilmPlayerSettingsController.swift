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
        observe(owner, whenVisible: true) { [weak self] in self?.update() }
        update()
    }

    override func pageDidBecomeActive() {
        let owner = self.owner
        runPageTask { [weak self] in
            await owner.reload()
            self?.update()
        }
    }

    private func update() {
        switch area {
        case .settings:
            sections = [.init(title: "", rows: [
                choiceRow("播放核心", id: "engine", keyPath: \.engine, options: { _ in PlaybackVideoEngine.allCases },
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
                Task { await self?.owner.reload() }
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
        let value = label(owner.preferences[keyPath: keyPath])
        let menu = UIMenu(options: .singleSelection, children: options(owner.preferences).enumerated().map { index, option in
            UIAction(title: label(option), identifier: .init("film.player.\(id).option.\(index)"),
                     attributes: owner.persistenceError == nil ? [] : .disabled,
                     state: owner.preferences[keyPath: keyPath] == option ? .on : .off) { [weak self] _ in
                guard let self else { return }
                let owner = self.owner
                Task { [weak self] in
                    await owner.set(keyPath, option)
                    self?.update()
                }
            }
        })
        return FilmSettingsRow(
            title: title,
            id: "film.player." + id,
            enabled: owner.persistenceError == nil,
            menu: (title: value, menu: menu)
        )
    }

    private func switchRow(_ title: String, id: String,
                           keyPath: WritableKeyPath<FilmPlayerPreferences, Bool>) -> FilmSettingsRow {
        filmSwitchRow(title, id: "film.player." + id, isOn: owner.preferences[keyPath: keyPath],
                      enabled: owner.persistenceError == nil) { [weak self] value in
            guard let self else { return }
            let owner = self.owner
            Task { [weak self] in
                await owner.set(keyPath, value)
                self?.update()
            }
        }
    }
}
#endif
