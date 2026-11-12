#if os(iOS)
import SwiftUI
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
        store.$preferences.removeDuplicates().sink { [weak self] in self?.value = $0 }
            .store(in: &subscriptions)
        store.$persistenceError.removeDuplicates().sink { [weak self] in self?.error = $0 }
            .store(in: &subscriptions)
    }

    func setMode(_ value: MusicColorMode) { store.set(\.colorMode, value) }
    func selectPalette(_ value: PagePalette) { store.selectFixedPalette(value) }
}

@MainActor
public final class MusicAppearanceSettingsViewController: HostingPageViewController<MusicAppearanceSettingsContent> {
    private let state: MusicAppearanceSettingsState

    public init(store: MusicAppearancePreferencesStore) {
        state = MusicAppearanceSettingsState(store: store)
        super.init(themeProvider: store, content: MusicAppearanceSettingsContent(
            state: state, theme: PageTheme(colors: store.pagePalette)
        ))
        pageTitle = "偏好设置"
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        view.tintColor = UIColor(theme.settings.accent)
        setContent(MusicAppearanceSettingsContent(state: state, theme: theme, openColors: { [weak self] in
            self?.showColors()
        }))
    }

    func showColors() {
        navigationController?.pushViewController(MusicColorSettingsViewController(state: state), animated: true)
    }
}

@MainActor
public struct MusicAppearanceSettingsContent: View {
    @ObservedObject var state: MusicAppearanceSettingsState
    let theme: PageTheme
    var openColors: () -> Void = {}

    public var body: some View {
        List {
            Section {
                Button(action: openColors) {
                    MusicSettingsLinkRow(theme: theme) {
                        HStack {
                            Text("配色设置")
                            Spacer(minLength: 8)
                            Text(state.value.colorMode == .fixed ? state.value.colorPalette.title : state.value.colorMode.title)
                                .foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
                        }
                    }
                }
                .accessibilityIdentifier("settings.colors")
                .modifier(MusicSettingsRowStyle(theme: theme))
            }
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }
}

@MainActor
final class MusicColorSettingsViewController: MusicSettingsPageViewController<MusicColorSettingsContent> {
    private let state: MusicAppearanceSettingsState

    init(state: MusicAppearanceSettingsState) {
        self.state = state
        super.init(appearance: state.store, content: MusicColorSettingsContent(
            state: state, theme: PageTheme(colors: state.store.pagePalette)
        ))
        pageTitle = "配色设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(state.$error.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            self?.reportError(self?.state.error)
        }
    }
    override func pageDidBecomeVisible() { reportError(state.error) }
    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicColorSettingsContent(state: state, theme: theme, customize: { [weak self] in
            self?.showCustomColor()
        }))
    }

    func showCustomColor() {
        guard canPresentPageAlert else { return }
        let picker = MusicCustomColorPickerController(store: appearance)
        let navigation = UINavigationController(rootViewController: picker)
        navigation.modalPresentationStyle = .pageSheet
        present(navigation, animated: true)
    }
}

@MainActor
struct MusicColorSettingsContent: View {
    @ObservedObject var state: MusicAppearanceSettingsState
    let theme: PageTheme
    var customize: () -> Void = {}

    var body: some View {
        List {
            Section {
                Picker("主题模式", selection: Binding(get: { state.value.colorMode }, set: state.setMode)) {
                    ForEach(MusicColorMode.allCases, id: \.rawValue) { mode in
                        Text(mode.title).tag(mode)
                    }
                }
                .pickerStyle(.menu)
                .accessibilityIdentifier("settings.colors.mode")
                .modifier(MusicSettingsRowStyle(theme: theme))
            }
            if state.value.colorMode == .fixed {
                Section("固定配色") {
                    ForEach(PageColorway.allCases, id: \.rawValue) { palette in
                        option(palette.title, selected: state.value.colorPalette == .preset(palette),
                               id: "music.settings.palette." + palette.rawValue) {
                            state.selectPalette(.preset(palette))
                        }
                    }
                    option("自定义颜色", selected: isCustom, id: "settings.colors.custom", action: customize)
                }
                .headerProminence(.increased)
            }
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }

    private var isCustom: Bool {
        if case .custom = state.value.colorPalette { return true }
        return false
    }

    private func option(_ title: String, selected: Bool, id: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Text(title).fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 8)
                if selected {
                    Image(systemName: "checkmark")
                        .foregroundStyle(Color(uiColor: UIColor(theme.settings.accent)))
                        .accessibilityHidden(true)
                }
            }
            .frame(minHeight: 44)
            .contentShape(Rectangle())
        }
        .accessibilityAddTraits(selected ? .isSelected : [])
        .accessibilityIdentifier(id)
        .modifier(MusicSettingsRowStyle(theme: theme))
    }
}

/// The native picker is embedded in a phone sheet with explicit commit/cancel.
/// Its preview lives only in the appearance owner, never in UserDefaults.
@MainActor
final class MusicCustomColorPickerController: UIColorPickerViewController, UIColorPickerViewControllerDelegate {
    private let store: MusicAppearancePreferencesStore
    private var finished = false

    init(store: MusicAppearancePreferencesStore) {
        self.store = store
        super.init()
        selectedColor = UIColor(store.preferences.colorPalette.baseColor)
        supportsAlpha = false
        delegate = self
        title = "自定义颜色"
        navigationItem.leftBarButtonItem = UIBarButtonItem(title: "取消", style: .plain,
            target: self, action: #selector(cancel))
        navigationItem.rightBarButtonItem = UIBarButtonItem(title: "完成", style: .done,
            target: self, action: #selector(commit))
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
        guard store.selectFixedPalette(.custom(rgb)) else {
            let alert = UIAlertController(title: "错误", message: store.persistenceError, preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "确定", style: .default))
            present(alert, animated: true)
            return
        }
        finished = true; store.endColorPreview()
        dismiss(animated: true)
    }
}
#endif
