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
    private var catalogSource: MusicHomeSourceSelection?
    private var savingCatalogSource = false
    /// Latest read/save; an older or other-scope completion never publishes.
    private var catalogSourceRequest: UUID?
    /// Same NativeMusicService behind the public browse port; no new owner.
    private var homeBrowser: (any MusicHomeBrowsing)? { model.publicBrowser as? any MusicHomeBrowsing }

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
        super.init(
            themeProvider: appearance,
            content: MusicSettingsContent(theme: PageTheme(palette: appearance.pageColorway))
        )
        pageTitle = "设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        // Delivered on the next main-loop turn, after the store has committed the value.
        observe(appearance.$preferences.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            guard let self, renderedPreference != preferenceValue else { return }
            render()
        }
        observe(model.accessChanges) { [weak self] in
            guard let self else { return }
            // A new access scope never shows the previous scope's value.
            catalogSource = nil; savingCatalogSource = false; catalogSourceRequest = nil
            render()
            if isPageVisible { readCatalogSource() }
        }
    }

    public override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        readCatalogSource()
    }

    public override func pageDidBecomeInactive() {
        super.pageDidBecomeInactive()
        // Page tasks are cancelled; the next visit reads the durable value again.
        savingCatalogSource = false; catalogSourceRequest = nil
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
            catalogSource: catalogSource, savingCatalogSource: savingCatalogSource,
            open: { [weak self] in self?.open($0) },
            resetUserData: { [weak self] in self?.confirmUserDataReset() },
            selectCatalogSource: { [weak self] in self?.saveCatalogSource($0) }))
    }

    /// Reads the durable choice each time the page is shown.
    private func readCatalogSource() {
        guard let homeBrowser, !savingCatalogSource else { return }
        let request = UUID(), access = model.accountAccessGeneration
        catalogSourceRequest = request
        runPageTask { [weak self] in
            guard let self, model.isAvailable, !model.isClosed else { return }
            let result: Result<MusicHomeSourceSelection, Error>
            do { result = .success(try await homeBrowser.homeSourceSelection()) }
            catch { result = .failure(error) }
            guard catalogSourceRequest == request, model.accountAccessGeneration == access else { return }
            switch result {
            case .success(let value): catalogSource = value; render()
            case .failure(let error):
                catalogSource = nil; render()
                let failure = MusicServiceError.sanitized(error)
                if failure != .cancelled { showError(failure.message(operation: "读取数据源")) }
            }
        }
    }

    private func saveCatalogSource(_ platform: MusicPublicBrowsePlatform) {
        guard let homeBrowser, let current = catalogSource, current.selected != platform,
              !savingCatalogSource, model.isAvailable, !model.isClosed else { return }
        let request = UUID(), access = model.accountAccessGeneration
        catalogSourceRequest = request
        savingCatalogSource = true; render()
        runPageTask { [weak self] in
            let result: Result<MusicHomeSourceSelection, Error>
            do { result = .success(try await homeBrowser.selectHomeSource(platform, ifRevision: current.revision)) }
            catch { result = .failure(error) }
            guard let self, catalogSourceRequest == request, model.accountAccessGeneration == access else { return }
            switch result {
            case .success(let value):
                savingCatalogSource = false; catalogSource = value; render()
            case .failure(let error):
                // Conflicts and failures show the durable value, not the attempted one.
                let reread = try? await homeBrowser.homeSourceSelection()
                guard catalogSourceRequest == request, model.accountAccessGeneration == access else { return }
                savingCatalogSource = false; catalogSource = reread; render()
                let failure = MusicServiceError.sanitized(error)
                if failure != .cancelled { showError(failure.message(operation: "保存数据源")) }
            }
        }
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
