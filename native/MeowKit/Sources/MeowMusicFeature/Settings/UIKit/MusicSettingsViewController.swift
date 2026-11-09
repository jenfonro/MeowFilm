#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicSettingsViewController: HostingPageViewController<MusicSettingsContent> {
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore
    private weak var router: (any MusicUIRouting)?
    private let resetUserData: (@MainActor () async throws -> Void)?
    private var requestingReset = false
    private var renderedTheme: PageTheme?
    private var renderedPreference: String?
    private let sourceSettings: MusicHomeSourceSettingsModel

    public init(
        model: MusicViewModel,
        appearance: MusicAppearancePreferencesStore,
        router: any MusicUIRouting,
        resetUserData: (@MainActor () async throws -> Void)? = nil
    ) {
        self.model = model
        self.appearance = appearance
        self.router = router
        self.resetUserData = resetUserData
        sourceSettings = MusicHomeSourceSettingsModel(model: model)
        super.init(
            themeProvider: appearance,
            content: MusicSettingsContent(theme: PageTheme(palette: appearance.pageColorway))
        )
        pageTitle = "设置"
        sourceSettings.onError = { [weak self] in self?.showError($0) }
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        // Delivered on the next main-loop turn, after the store has committed the value.
        observe(appearance.$preferences.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            guard let self, renderedPreference != preferenceValue else { return }
            render()
        }
        observe(sourceSettings) { [weak self] in self?.render() }
    }

    public override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        sourceSettings.activate()
    }

    public override func pageDidBecomeHidden() {
        super.pageDidBecomeHidden()
        sourceSettings.deactivate()
    }

    public override func pageDidBecomeInactive() {
        super.pageDidBecomeInactive()
        sourceSettings.deactivate()
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, renderedTheme != theme else { return }
        renderedTheme = theme
        render()
    }

    private func render() {
        guard isViewLoaded else { return }
        let value = preferenceValue
        renderedPreference = value
        setContent(MusicSettingsContent(theme: theme, preferenceValue: value,
            canResetUserData: resetUserData != nil, requestingReset: requestingReset,
            catalogSource: sourceSettings.selection, savingCatalogSource: sourceSettings.isSaving,
            open: { [weak self] in self?.open($0) },
            resetUserData: { [weak self] in self?.confirmUserDataReset() },
            selectCatalogSource: { [weak self] in self?.sourceSettings.select($0) }))
    }

    func confirmUserDataReset() {
        guard resetUserData != nil, !requestingReset, canPresentPageAlert else { return }
        let alert = UIAlertController(title: "清除本机用户数据？",
            message: "将删除本机登录凭据、设备身份和密钥、本地歌单与收藏、历史、导入音源、偏好及缓存，无法撤销。云端数据和 App 外的原始文件不受影响。\n\n确认后需从多任务列表关闭 App，再重新打开才能完成清除。",
            preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "清除并等待重启", style: .destructive) { [weak self, weak alert] _ in
            alert?.dismiss(animated: true) { self?.requestUserDataReset() }
        })
        present(alert, animated: true)
    }

    func requestUserDataReset() {
        guard let resetUserData, !requestingReset else { return }
        requestingReset = true; render()
        // The App owns this operation; page disappearance must not cancel a
        // persisted reset request or permit a second business session.
        Task { [weak self] in
            do { try await resetUserData() }
            catch {
                self?.requestingReset = false; self?.render()
                self?.showError("无法记录清除请求，请重试")
            }
        }
    }

    private var preferenceValue: String {
        let preferences = appearance.preferences
        return preferences.colorMode == .fixed ? preferences.colorPalette.title : preferences.colorMode.title
    }

    private func open(_ destination: MusicSettingsDestination) {
        let controller: UIViewController
        switch destination {
        case .aggregation:
            controller = MusicAccountsViewController(model: model, appearance: appearance, router: router)
        case .sources:
            controller = MusicSourceManagementViewController(model: model, appearance: appearance)
        case .exclusions:
            controller = MusicSourceExclusionsViewController(model: model, appearance: appearance)
        case .search:
            controller = MusicSearchSettingsViewController(appearance: appearance)
        case .playback:
            controller = MusicPlaybackSettingsViewController(model: model, appearance: appearance)
        case .appearance:
            controller = MusicAppearanceSettingsViewController(store: appearance)
        case .metadata:
            controller = MusicMetadataSettingsViewController(model: model, appearance: appearance)
        }
        navigationController?.pushViewController(controller, animated: true)
    }
}
#endif
