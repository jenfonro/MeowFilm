#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
final class MusicAppearanceSettingsState: ObservableObject {
    @Published private(set) var value: MusicAppearancePreferences
    @Published private(set) var error: String?
    let store: MusicAppearancePreferencesStore
    private var subscriptions: Set<AnyCancellable> = []

    init(store: MusicAppearancePreferencesStore) {
        self.store = store
        value = store.preferences
        error = store.persistenceError
        store.$preferences.dropFirst().removeDuplicates().sink { [weak self] in self?.value = $0 }
            .store(in: &subscriptions)
        store.$persistenceError.dropFirst().removeDuplicates().sink { [weak self] in self?.error = $0 }
            .store(in: &subscriptions)
    }

    func setMode(_ value: MusicColorMode) { Task { await store.set(\.colorMode, value) } }
    func selectPalette(_ value: PagePalette) { Task { await store.selectFixedPalette(value) } }
}

@MainActor
public final class MusicAppearanceSettingsViewController: SettingsListViewController {
    private let state: MusicAppearanceSettingsState

    public init(store: MusicAppearancePreferencesStore) {
        state = MusicAppearanceSettingsState(store: store)
        super.init(themeProvider: store)
        pageTitle = "偏好设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        observeDistinct(state.$value.eraseToAnyPublisher(), immediateOnMain: false, whenVisible: true) { [weak self] _ in
            self?.render()
        }
        render()
    }

    private func render() {
        guard isViewLoaded else { return }
        let summary = state.value.colorMode == .fixed ? state.value.colorPalette.title : state.value.colorMode.title
        applySections([
            SettingsListSection(id: "main", rows: [
                SettingsListRow(
                    id: "settings.colors",
                    title: "配色设置",
                    accessory: .value(summary, disclosure: true),
                    action: { [weak self] in self?.showColors() }
                )
            ])
        ])
    }

    func showColors() {
        navigationController?.pushViewController(MusicColorSettingsViewController(state: state), animated: true)
    }
}

@MainActor
final class MusicColorSettingsViewController: SettingsListViewController {
    private let state: MusicAppearanceSettingsState
    private let appearance: MusicAppearancePreferencesStore

    init(state: MusicAppearanceSettingsState) {
        self.state = state
        self.appearance = state.store
        super.init(themeProvider: state.store)
        pageTitle = "配色设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observeDistinct(state.$value.eraseToAnyPublisher(), immediateOnMain: false, whenVisible: true) { [weak self] _ in
            self?.render()
        }
        observeDistinct(state.$error.eraseToAnyPublisher(), immediateOnMain: false, whenVisible: true) { [weak self] _ in
            self?.syncError()
        }
        render()
    }

    override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        syncError()
    }

    private func syncError() {
        syncErrorSource("appearance", message: state.error)
    }

    private func render() {
        guard isViewLoaded else { return }
        let modeMenu = UIMenu(options: .singleSelection, children: MusicColorMode.allCases.map { mode in
            UIAction(title: mode.title, state: state.value.colorMode == mode ? .on : .off) { [weak state] _ in
                state?.setMode(mode)
            }
        })
        var sections: [SettingsListSection] = [
            SettingsListSection(id: "mode", rows: [
                SettingsListRow(
                    id: "settings.colors.mode",
                    title: "主题模式",
                    accessory: .menu(title: state.value.colorMode.title, menu: modeMenu)
                )
            ])
        ]
        if state.value.colorMode == .fixed {
            let isCustom: Bool = {
                if case .custom = state.value.colorPalette { return true }
                return false
            }()
            var paletteRows: [SettingsListRow] = PageColorway.allCases.map { palette in
                SettingsListRow(
                    id: "music.settings.palette." + palette.rawValue,
                    title: palette.title,
                    accessory: state.value.colorPalette == .preset(palette) ? .checkmark : .none,
                    action: { [weak state] in state?.selectPalette(.preset(palette)) }
                )
            }
            paletteRows.append(SettingsListRow(
                id: "settings.colors.custom",
                title: "自定义颜色",
                accessory: isCustom ? .checkmark : .none,
                action: { [weak self] in self?.showCustomColor() }
            ))
            sections.append(SettingsListSection(id: "palettes", title: "固定配色", rows: paletteRows))
        }
        applySections(sections)
    }

    func showCustomColor() {
        guard canPresentPageAlert else { return }
        let picker = MusicCustomColorPickerController(store: appearance)
        let navigation = UINavigationController(rootViewController: picker)
        let barAppearance = UINavigationBarAppearance()
        barAppearance.configureWithDefaultBackground()
        let regularTitle: [NSAttributedString.Key: Any] = [
            .font: UIFont.systemFont(ofSize: 17, weight: .regular)
        ]
        barAppearance.titleTextAttributes = regularTitle
        let buttonAppearance = UIBarButtonItemAppearance(style: .plain)
        buttonAppearance.normal.titleTextAttributes = regularTitle
        buttonAppearance.highlighted.titleTextAttributes = regularTitle
        buttonAppearance.disabled.titleTextAttributes = regularTitle
        barAppearance.buttonAppearance = buttonAppearance
        barAppearance.doneButtonAppearance = buttonAppearance
        navigation.navigationBar.standardAppearance = barAppearance
        navigation.navigationBar.scrollEdgeAppearance = barAppearance
        navigation.navigationBar.compactAppearance = barAppearance
        navigation.modalPresentationStyle = .pageSheet
        present(navigation, animated: true)
    }
}

/// The native picker is embedded in a phone sheet with explicit commit/cancel.
/// Its preview lives only in the appearance owner, never in UserDefaults.
@MainActor
final class MusicCustomColorPickerController: UIColorPickerViewController, UIColorPickerViewControllerDelegate {
    private let store: MusicAppearancePreferencesStore
    private var finished = false
    private var committing = false

    init(store: MusicAppearancePreferencesStore) {
        self.store = store
        super.init()
        selectedColor = UIColor(store.preferences.colorPalette.baseColor)
        supportsAlpha = false
        delegate = self
        title = "自定义颜色"
        let regularButton: [NSAttributedString.Key: Any] = [
            .font: UIFont.systemFont(ofSize: 17, weight: .regular)
        ]
        let cancelItem = UIBarButtonItem(title: "取消", style: .plain,
            target: self, action: #selector(cancel))
        let doneItem = UIBarButtonItem(title: "完成", style: .plain,
            target: self, action: #selector(commit))
        for item in [cancelItem, doneItem] {
            for state: UIControl.State in [.normal, .highlighted, .disabled] {
                item.setTitleTextAttributes(regularButton, for: state)
            }
        }
        navigationItem.leftBarButtonItem = cancelItem
        navigationItem.rightBarButtonItem = doneItem
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        preview()
    }
    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        if !finished { store.endColorPreview() }
    }
    func colorPickerViewController(_ viewController: UIColorPickerViewController,
                                   didSelect color: UIColor, continuously: Bool) { preview() }
    func colorPickerViewControllerDidFinish(_ viewController: UIColorPickerViewController) { cancel() }

    private var rgb: UInt32 {
        var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0, alpha: CGFloat = 0
        selectedColor.getRed(&r, green: &g, blue: &b, alpha: &alpha)
        func byte(_ value: CGFloat) -> UInt32 { UInt32((min(1, max(0, value)) * 255).rounded()) }
        return byte(r) << 16 | byte(g) << 8 | byte(b)
    }
    private func preview() { store.previewFixedColor(rgb) }
    @objc func cancel() {
        finished = true; store.endColorPreview()
        dismiss(animated: true)
    }
    @objc func commit() {
        guard !committing else { return }
        committing = true
        let selected = rgb
        let store = self.store
        Task { [weak self] in
            let saved = await store.selectFixedPalette(.custom(selected))
            guard let self else { return }
            self.committing = false
            guard saved else {
                self.presentPageAlert(title: "错误", message: store.persistenceError,
                                      actions: [PageAlertAction(title: "确定", style: .default)])
                return
            }
            self.finished = true; store.endColorPreview()
            self.dismiss(animated: true)
        }
    }
}
#endif
