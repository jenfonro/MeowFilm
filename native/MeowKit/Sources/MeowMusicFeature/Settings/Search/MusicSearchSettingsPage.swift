#if os(iOS)
import UIKit
import Combine
import MeowUI

enum MusicSearchSettingsKind: String, CaseIterable {
    case songs, playlists

    var platformTitle: String { self == .songs ? "歌曲搜索平台设置" : "歌单搜索平台设置" }
    var advancedTitle: String { self == .songs ? "歌曲搜索高级设置" : "歌单搜索高级设置" }
}

@MainActor
final class MusicSearchSettingsState: ObservableObject {
    @Published private(set) var value: MusicSearchPreferences
    @Published private(set) var error: String?
    private let store: MusicSearchPreferencesStore
    private var subscriptions: Set<AnyCancellable> = []

    init(store: MusicSearchPreferencesStore) {
        self.store = store
        value = store.preferences
        error = store.persistenceError
        store.$preferences.dropFirst().removeDuplicates().sink { [weak self] in self?.value = $0 }
            .store(in: &subscriptions)
        store.$persistenceError.dropFirst().removeDuplicates().sink { [weak self] in self?.error = $0 }
            .store(in: &subscriptions)
    }

    func settings(_ kind: MusicSearchSettingsKind) -> MusicSearchPreferences.Kind {
        kind == .songs ? value.songs : value.playlists
    }

    @discardableResult
    func setPlatforms(_ platforms: [MusicPublicBrowsePlatform], kind: MusicSearchSettingsKind) async -> Bool {
        var value = store.preferences
        if kind == .songs { value.songs.platforms = platforms }
        else { value.playlists.platforms = platforms }
        return await store.update(value)
    }

    @discardableResult
    func setSearchPlaylists(_ enabled: Bool) async -> Bool {
        var value = store.preferences
        value.searchPlaylists = enabled
        return await store.update(value)
    }

    @discardableResult
    func setAdvanced(_ kind: MusicSearchSettingsKind,
                     field: WritableKeyPath<MusicSearchPreferences.Kind, Int>, to number: Int) async -> Bool {
        var value = store.preferences
        if kind == .songs { value.songs[keyPath: field] = number }
        else { value.playlists[keyPath: field] = number }
        return await store.update(value)
    }

    func reload() async {
        await store.reload()
        value = store.preferences
    }
}

@MainActor
final class MusicSearchSettingsViewController: SettingsListViewController {
    let state: MusicSearchSettingsState
    private let appearance: MusicAppearancePreferencesStore
    weak var platformWindow: MusicSearchPlatformsViewController?
    private var steppers: [String: MusicSearchStepperRowView] = [:]

    init(appearance: MusicAppearancePreferencesStore, store: MusicSearchPreferencesStore) {
        self.appearance = appearance
        state = MusicSearchSettingsState(store: store)
        super.init(themeProvider: appearance)
        pageTitle = "搜索设置"
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
        syncErrorSource("search.preferences", message: state.error)
    }

    override func pageDidBecomeInactive() {
        platformWindow?.dismiss(animated: false)
        super.pageDidBecomeInactive()
    }

    private func render() {
        guard isViewLoaded else { return }
        var sections: [SettingsListSection] = [
            SettingsListSection(id: "general", rows: [
                SettingsListRow(
                    id: "music.search.searchPlaylists",
                    title: "搜索歌曲同时搜索歌单",
                    accessory: .toggle(isOn: state.value.searchPlaylists, enabled: true) { [weak self] value in
                        guard let self else { return }
                        let state = self.state
                        self.runPageTask(key: "searchPlaylists") { [weak self] in
                            if !(await state.setSearchPlaylists(value)) {
                                await state.reload()
                                self?.render()
                            }
                        }
                    }
                )
            ]),
            SettingsListSection(id: "platforms", rows: MusicSearchSettingsKind.allCases.map { kind in
                SettingsListRow(
                    id: "music.search.\(kind.rawValue).platforms",
                    title: kind.platformTitle,
                    accessory: .disclosure,
                    action: { [weak self] in self?.openPlatforms(kind) }
                )
            }),
        ]
        for kind in MusicSearchSettingsKind.allCases {
            let rows = [
                stepperRow("单次请求页数", kind: kind, field: \.pages, name: "pages", maximum: Int.max),
                stepperRow("平台并发数", kind: kind, field: \.concurrency, name: "concurrency", maximum: Int.max),
                stepperRow("结果缓存（分钟）", kind: kind, field: \.cacheMinutes, name: "cacheMinutes",
                           maximum: MusicSearchPreferences.maximumCacheMinutes),
            ]
            sections.append(SettingsListSection(
                id: "music.search.\(kind.rawValue).advanced",
                title: kind.advancedTitle,
                rows: rows
            ))
        }
        applySections(sections)
    }

    private func stepperRow(
        _ title: String,
        kind: MusicSearchSettingsKind,
        field: WritableKeyPath<MusicSearchPreferences.Kind, Int>,
        name: String,
        maximum: Int
    ) -> SettingsListRow {
        let id = "music.search.\(kind.rawValue).\(name)"
        let view: MusicSearchStepperRowView
        if let existing = steppers[id] {
            view = existing
        } else {
            let created = MusicSearchStepperRowView(title: title, identifier: id, maximum: maximum) { [weak self] next in
                guard let self else { return }
                let state = self.state
                self.runPageTask(key: id) { [weak self] in
                    if !(await state.setAdvanced(kind, field: field, to: next)) {
                        await state.reload()
                        self?.render()
                    }
                }
            }
            steppers[id] = created
            view = created
        }
        view.update(value: state.settings(kind)[keyPath: field], theme: theme)
        return SettingsListRow(id: id, title: title, embeddedContent: view)
    }

    private func openPlatforms(_ kind: MusicSearchSettingsKind) {
        let controller = MusicSearchPlatformsViewController(state: state, kind: kind, appearance: appearance)
        platformWindow = controller
        if !presentPageModal(controller) { platformWindow = nil }
    }
}

@MainActor
private final class MusicSearchStepperRowView: UIView, PageThemeable {
    private let titleText: String
    private let maximum: Int
    private let onChange: @MainActor (Int) -> Void
    private var currentValue = 1

    private let container = UIStackView()
    private let titleLabel = UILabel()
    private let capsule = StepperCapsuleView()
    private let decreaseButton = UIButton(type: .system)
    private let valueLabel = UILabel()
    private let increaseButton = UIButton(type: .system)
    private let leftDivider = SettingsDividerView(axis: .vertical)
    private let rightDivider = SettingsDividerView(axis: .vertical)

    init(title: String, identifier: String, maximum: Int, onChange: @escaping @MainActor (Int) -> Void) {
        self.titleText = title
        self.maximum = maximum
        self.onChange = onChange
        super.init(frame: .zero)

        titleLabel.text = title
        titleLabel.font = .preferredFont(forTextStyle: .body)
        titleLabel.adjustsFontForContentSizeCategory = true
        titleLabel.numberOfLines = 0
        titleLabel.accessibilityIdentifier = "\(identifier).label"
        titleLabel.setContentCompressionResistancePriority(.required, for: .vertical)

        decreaseButton.setImage(UIImage(systemName: "minus",
            withConfiguration: UIImage.SymbolConfiguration(pointSize: 13, weight: .regular)), for: .normal)
        decreaseButton.accessibilityLabel = "减少\(title)"
        decreaseButton.accessibilityIdentifier = "\(identifier).decrease"
        decreaseButton.addAction(UIAction { [weak self] _ in self?.step(-1) }, for: .touchUpInside)

        increaseButton.setImage(UIImage(systemName: "plus",
            withConfiguration: UIImage.SymbolConfiguration(pointSize: 13, weight: .regular)), for: .normal)
        increaseButton.accessibilityLabel = "增加\(title)"
        increaseButton.accessibilityIdentifier = "\(identifier).increase"
        increaseButton.addAction(UIAction { [weak self] _ in self?.step(1) }, for: .touchUpInside)

        valueLabel.font = .monospacedDigitSystemFont(ofSize: 16, weight: .regular)
        valueLabel.adjustsFontForContentSizeCategory = true
        valueLabel.textAlignment = .center
        valueLabel.accessibilityIdentifier = "\(identifier).value"

        for button in [decreaseButton, increaseButton] {
            button.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                button.widthAnchor.constraint(equalToConstant: 40),
                button.heightAnchor.constraint(greaterThanOrEqualToConstant: 36),
            ])
        }
        valueLabel.widthAnchor.constraint(greaterThanOrEqualToConstant: 38).isActive = true

        capsule.axis = .horizontal
        capsule.alignment = .fill
        capsule.spacing = 0
        capsule.layer.borderWidth = 1
        capsule.clipsToBounds = true
        capsule.translatesAutoresizingMaskIntoConstraints = false
        for child in [decreaseButton, leftDivider, valueLabel, rightDivider, increaseButton] as [UIView] {
            capsule.addArrangedSubview(child)
        }
        NSLayoutConstraint.activate([
            capsule.heightAnchor.constraint(greaterThanOrEqualToConstant: 36),
        ])
        capsule.setContentHuggingPriority(.required, for: .horizontal)
        capsule.setContentCompressionResistancePriority(.required, for: .horizontal)

        container.axis = .horizontal
        container.alignment = .center
        container.spacing = 12
        container.addArrangedSubview(titleLabel)
        container.addArrangedSubview(capsule)
        container.translatesAutoresizingMaskIntoConstraints = false
        addSubview(container)
        container.pinEdges(to: self)
        accessibilityIdentifier = identifier
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func layoutSubviews() {
        super.layoutSubviews()
        let vertical = traitCollection.preferredContentSizeCategory.isAccessibilityCategory
        container.axis = vertical ? .vertical : .horizontal
        container.alignment = vertical ? .leading : .center
    }

    func update(value: Int, theme: PageTheme) {
        currentValue = value
        valueLabel.text = String(value)
        valueLabel.accessibilityLabel = titleText + "，" + String(value)
        decreaseButton.isEnabled = value > 1
        decreaseButton.accessibilityValue = "\(value)"
        increaseButton.isEnabled = value < maximum
        increaseButton.accessibilityValue = "\(value)"
        applyTheme(theme)
    }

    func applyTheme(_ theme: PageTheme) {
        let settings = theme.settings
        titleLabel.textColor = UIColor(settings.ink)
        valueLabel.textColor = UIColor(settings.ink)
        decreaseButton.tintColor = UIColor(decreaseButton.isEnabled ? settings.ink : settings.muted)
        increaseButton.tintColor = UIColor(increaseButton.isEnabled ? settings.ink : settings.muted)
        capsule.backgroundColor = UIColor(settings.input)
        capsule.layer.borderColor = UIColor(settings.inputBorder).cgColor
        leftDivider.applyTheme(theme)
        rightDivider.applyTheme(theme)
    }

    private func step(_ delta: Int) {
        let next = min(maximum, max(1, currentValue + delta))
        guard next != currentValue else { return }
        onChange(next)
    }
}

@MainActor
private final class StepperCapsuleView: UIStackView {
    override func layoutSubviews() {
        super.layoutSubviews()
        layer.cornerRadius = bounds.height / 2
    }
}
#endif
