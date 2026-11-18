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
        let value = label(owner.preferences[keyPath: keyPath])
        let button = FilmPlayerSettingsMenuButton(title: title, value: value,
                                                 enabled: owner.persistenceError == nil)
        button.accessibilityIdentifier = "film.player." + id + ".menu"
        button.showsMenuAsPrimaryAction = true
        button.menu = UIMenu(options: .singleSelection, children: options(owner.preferences).enumerated().map { index, value in
            UIAction(title: label(value), identifier: .init("film.player.\(id).option.\(index)"),
                     attributes: owner.persistenceError == nil ? [] : .disabled,
                     state: owner.preferences[keyPath: keyPath] == value ? .on : .off) { [weak self] _ in
                guard let self else { return }
                owner.set(keyPath, value)
                update()
            }
        })
        return .init(title: title, detail: value, id: "film.player." + id,
                     enabled: button.isEnabled, content: button)
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
private final class FilmPlayerSettingsMenuButton: UIButton {
    init(title: String, value: String, enabled: Bool) {
        super.init(frame: .zero)
        isEnabled = enabled
        accessibilityLabel = title; accessibilityValue = value
        let heading = UILabel(), current = UILabel()
        heading.text = title; current.text = value
        heading.font = .preferredFont(forTextStyle: .body)
        current.font = .preferredFont(forTextStyle: .subheadline)
        heading.textColor = UIColor {
            let colors = SettingsTheme(dark: $0.userInterfaceStyle == .dark)
            return UIColor(enabled ? colors.ink : colors.muted)
        }
        current.textColor = UIColor { UIColor(SettingsTheme(dark: $0.userInterfaceStyle == .dark).muted) }
        for label in [heading, current] {
            label.adjustsFontForContentSizeCategory = true
            label.numberOfLines = 0
        }
        current.textAlignment = .right
        current.setContentHuggingPriority(.defaultHigh, for: .horizontal)
        current.setContentCompressionResistancePriority(.init(751), for: .horizontal)
        let content = UIStackView(arrangedSubviews: [heading, current])
        content.alignment = .center; content.spacing = 12
        content.isUserInteractionEnabled = false; content.accessibilityElementsHidden = true
        content.translatesAutoresizingMaskIntoConstraints = false
        addSubview(content)
        NSLayoutConstraint.activate([
            content.topAnchor.constraint(equalTo: topAnchor),
            content.bottomAnchor.constraint(equalTo: bottomAnchor),
            content.leadingAnchor.constraint(equalTo: leadingAnchor),
            content.trailingAnchor.constraint(equalTo: trailingAnchor),
            current.widthAnchor.constraint(lessThanOrEqualTo: content.widthAnchor, multiplier: 0.6),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        bounds.insetBy(dx: -SettingsVisualMetrics.rowHorizontalPadding,
                       dy: -SettingsVisualMetrics.rowVerticalPadding).contains(point)
    }
}
#endif
