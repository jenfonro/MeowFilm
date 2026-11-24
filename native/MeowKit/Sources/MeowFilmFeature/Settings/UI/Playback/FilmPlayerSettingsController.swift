#if os(iOS)
import UIKit
import MeowContracts
import MeowUI

@MainActor
final class FilmPlayerSettingsController: SettingsListViewController {
    override var avoidsKeyboard: Bool { false }
    enum Area { case settings, controls }
    let owner: FilmPlayerPreferencesStore
    private let area: Area

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
        reloadOwner()
    }

    private func reloadOwner() {
        let owner = self.owner
        runPageTask(key: "reload") { [weak self] in
            await owner.reload()
            self?.update()
        }
    }

    func update() {
        guard isViewLoaded else { return }
        switch area {
        case .settings:
            applySections([SettingsListSection(id: "settings", rows: [
                choiceRow("播放核心", id: "engine", keyPath: \.engine, options: { _ in PlaybackVideoEngine.allCases },
                          title: { $0.title }),
                SettingsListRow(id: "film.player.controls", title: "播放控制", accessory: .disclosure, action: { [weak self] in
                    guard let self else { return }
                    navigationController?.pushViewController(
                        FilmPlayerSettingsController(owner: owner, area: .controls, themeProvider: themeProvider),
                        animated: true
                    )
                }),
            ])])
        case .controls:
            applySections([
                SettingsListSection(id: "controls", title: "播放控制", rows: [
                    choiceRow("默认倍速", id: "defaultRate", keyPath: \.defaultRate,
                              options: { FilmPlayerPreferences.defaultRates(for: $0.engine) }, title: Self.rateTitle),
                    choiceRow("长按倍速", id: "holdRate", keyPath: \.holdRate,
                              options: { FilmPlayerPreferences.holdRates(for: $0.engine) }, title: Self.rateTitle),
                    choiceRow("快进/快退步长", id: "seekStep", keyPath: \.seekStep,
                              options: { _ in FilmPlayerPreferences.seekSteps }, title: { "\(Int($0)) 秒" }),
                    switchRow("控制中心显示", id: "showsControlCenter", keyPath: \.showsControlCenter),
                    switchRow("自动识别片头片尾", id: "recognizesChapters", keyPath: \.recognizesChapters),
                ]),
                SettingsListSection(id: "singleTap", title: "单击手势", rows: [
                    choiceRow("单击屏幕中间", id: "singleTapCenter", keyPath: \.singleTapCenter,
                              options: { _ in [.none, .toggleControls, .playPause] }, title: { $0.title }),
                    choiceRow("单击屏幕两边", id: "singleTapSides", keyPath: \.singleTapSides,
                              options: { _ in FilmPlayerTapAction.allCases }, title: { $0.title }),
                ]),
                SettingsListSection(id: "doubleTap", title: "双击手势", rows: [
                    choiceRow("双击屏幕中间", id: "doubleTapCenter", keyPath: \.doubleTapCenter,
                              options: { _ in [.none, .playPause] }, title: { $0.title }),
                    choiceRow("双击屏幕两边", id: "doubleTapSides", keyPath: \.doubleTapSides,
                              options: { _ in [.none, .playPause, .seek] }, title: { $0.title }),
                ]),
                SettingsListSection(id: "swipe", title: "滑动手势", rows: [
                    switchRow("滑动调节音量", id: "swipeVolume", keyPath: \.swipeVolume),
                    switchRow("滑动调节亮度", id: "swipeBrightness", keyPath: \.swipeBrightness),
                    switchRow("滑动调节进度", id: "swipeSeek", keyPath: \.swipeSeek),
                    switchRow("短剧滑动切集", id: "swipeEpisodes", keyPath: \.swipeEpisodes),
                ]),
                SettingsListSection(id: "haptic", title: "震动反馈", rows: [
                    switchRow("长按屏幕", id: "hapticHold", keyPath: \.hapticHold),
                    switchRow("滑动调节亮度", id: "hapticBrightness", keyPath: \.hapticBrightness),
                    switchRow("滑动调节音量", id: "hapticVolume", keyPath: \.hapticVolume),
                    switchRow("滑动调节进度", id: "hapticSeek", keyPath: \.hapticSeek),
                ]),
            ])
        }
        syncErrorSource("persistence", message: owner.persistenceError, retry: { [weak self] in
            self?.reloadOwner()
        })
    }

    private static func rateTitle(_ value: Double) -> String {
        value.formatted(.number.precision(.fractionLength(0...2))) + "X"
    }

    private func choiceRow<Value: Equatable>(_ title: String, id: String,
                                             keyPath: WritableKeyPath<FilmPlayerPreferences, Value>,
                                             options: @escaping (FilmPlayerPreferences) -> [Value],
                                             title label: @escaping (Value) -> String) -> SettingsListRow {
        let value = label(owner.preferences[keyPath: keyPath])
        let enabled = owner.persistenceError == nil
        let menu = UIMenu(options: .singleSelection, children: options(owner.preferences).enumerated().map { index, option in
            UIAction(title: label(option), identifier: .init("film.player.\(id).option.\(index)"),
                     attributes: enabled ? [] : .disabled,
                     state: owner.preferences[keyPath: keyPath] == option ? .on : .off) { [weak self] _ in
                guard let self else { return }
                let owner = self.owner
                Task { [weak self] in
                    await owner.set(keyPath, option)
                    self?.update()
                }
            }
        })
        return SettingsListRow(
            id: "film.player." + id,
            title: title,
            accessory: .menu(title: value, menu: menu, enabled: enabled),
            enabled: enabled
        )
    }

    private func switchRow(_ title: String, id: String,
                           keyPath: WritableKeyPath<FilmPlayerPreferences, Bool>) -> SettingsListRow {
        let enabled = owner.persistenceError == nil
        return SettingsListRow(
            id: "film.player." + id,
            title: title,
            accessory: .toggle(isOn: owner.preferences[keyPath: keyPath], enabled: enabled) { [weak self] value in
                guard let self else { return }
                let owner = self.owner
                Task { [weak self] in
                    await owner.set(keyPath, value)
                    self?.update()
                }
            },
            enabled: enabled
        )
    }
}
#endif
