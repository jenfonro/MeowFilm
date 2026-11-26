#if os(iOS)
import UIKit
import Combine
import UniformTypeIdentifiers
import MeowUI

@MainActor
final class MusicAccountsViewController: SettingsListViewController, UIDocumentPickerDelegate {
    let owner: MusicViewModel
    let state: MusicAccountsSettingsState
    let transfer: MusicAccountTransferState
    private let appearance: MusicAppearancePreferencesStore
    private weak var filePicker: UIDocumentPickerViewController?
    private var exportingFile = false
    private weak var router: (any MusicUIRouting)?
    private weak var detail: MusicAccountDetailViewController?
    private var dialogPlatform: MusicAccountPlatform?
    private var isDestructiveSyncAlertPresented = false

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore, router: (any MusicUIRouting)?) {
        owner = model
        self.appearance = appearance
        state = MusicAccountsSettingsState(owner: model)
        transfer = MusicAccountTransferState(owner: model)
        self.router = router
        super.init(themeProvider: appearance)
        pageTitle = "平台账号"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.accessibilityIdentifier = "music.accounts.list"
        state.onNeedsDestructiveConfirmation = { [weak self] in self?.confirmDestructiveSync() }
        transfer.confirm = { [weak self] in self?.confirmImport($0) }
        transfer.exported = { [weak self] in self?.saveFile($0) }
        transfer.result = { [weak self] in self?.showTransferResult($0) }

        observe(owner.accountChanges) { [weak self] platform in
            guard let self else { return }
            state.accountSessionChanged(platform)
            if dialogPlatform == platform || isDestructiveSyncAlertPresented { dismissPresentedAlert() }
            render()
        }
        var changePublishers: [AnyPublisher<Void, Never>] = [
            state.$rows.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$busy.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$syncActionBusy.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$pendingAccounts.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            transfer.$busy.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ]
        if let sync = state.sync {
            changePublishers.append(contentsOf: [
                sync.$state.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                sync.$busy.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            ])
        }
        let uiChanges = Publishers.MergeMany(changePublishers)
        observe(uiChanges.eraseToAnyPublisher(), whenVisible: true) { [weak self] in
            guard let self else { return }
            closeDetailIfSignedOut()
            render()
        }
        observe(state.errorChanges) { [weak self] in self?.showFailure() }
        render()
    }

    override func pageDidBecomeActive() {
        super.pageDidBecomeActive()
        state.start()
        transfer.start()
        render()
    }

    override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        showFailure()
    }

    override func pageDidBecomeInactive() {
        state.close()
        transfer.close()
        dismissPresentedOverlays()
        super.pageDidBecomeInactive()
    }

    private func dismissPresentedAlert() {
        dialogPlatform = nil
        isDestructiveSyncAlertPresented = false
        let host: UIViewController = detail ?? self
        if let alert = host.presentedViewController as? UIAlertController {
            alert.dismissSelf(animated: false)
        }
    }

    private func dismissPresentedOverlays() {
        if let picker = filePicker {
            filePicker = nil
            dismissPageModal(picker, animated: false)
        }
        dismissPresentedAlert()
        closeDetail(animated: false)
    }

    private func render() {
        guard isViewLoaded else { return }
        let now = Date()
        var accountRows: [SettingsListRow] = []
        if state.accounts == nil {
            accountRows.append(SettingsListRow(
                id: "music.accounts.unavailable",
                title: "账号服务不可用",
                enabled: false
            ))
        }
        for platform in MusicAccountPlatform.allCases {
            let summary = state.rows.first { $0.id == platform }
            let loading = state.pendingAccounts.contains(platform) || (summary == nil && state.busy)
            let detail = summary?.detail(at: now) ?? MusicAccountSettingsDetail(status: "不可用")
            let detailText: String?
            let detailDanger: Bool
            if loading {
                detailText = "读取账号"
                detailDanger = false
            } else if let name = detail.name {
                detailText = "\(name) · \(detail.status)"
                detailDanger = detail.isNonVIP
            } else {
                detailText = detail.status
                detailDanger = detail.isNonVIP
            }

            let accessory: SettingsListRow.Accessory
            if loading {
                accessory = .loading(accessibilityLabel: "读取账号")
            } else if let account = summary?.account {
                if account.phase == .signedOut, account.canLogin {
                    accessory = .hint("登录")
                } else if account.phase != .unavailable {
                    accessory = .disclosure
                } else {
                    accessory = .none
                }
            } else {
                accessory = .none
            }

            let canInteract = summary != nil && state.isActive && !transfer.busy && !state.pendingAccounts.contains(platform)
            accountRows.append(SettingsListRow(
                id: "music.accounts.\(platform.rawValue)",
                title: platform.title,
                detail: detailText,
                detailDestructive: detailDanger,
                leadingIcon: .badge(assetName: platform.settingsLogoName),
                accessory: accessory,
                enabled: canInteract,
                accessibilityLabel: platform.title,
                accessibilityValue: detailText,
                action: { [weak self, summary] in
                    guard let summary else { return }
                    self?.openAccount(summary.account)
                }
            ))
        }

        let transferEnabled = state.isActive && transfer.service != nil && !transfer.busy
        var transferRows: [SettingsListRow] = [
            SettingsListRow(
                id: "music.accounts.import",
                title: "导入账号",
                enabled: transferEnabled,
                action: { [weak self] in self?.openAccountFile() }
            ),
            SettingsListRow(
                id: "music.accounts.export",
                title: "导出账号",
                enabled: transferEnabled,
                action: { [weak self] in self?.transfer.exportFile() }
            ),
        ]
        if transfer.busy {
            transferRows.append(SettingsListRow(
                id: "music.accounts.transfer.busy",
                title: "账号导入导出中",
                accessory: .loading(accessibilityLabel: "账号导入导出中"),
                enabled: false
            ))
        }

        var sections: [SettingsListSection] = [
            SettingsListSection(id: "accounts", rows: accountRows),
        ]
        if let sync = state.sync, sync.state.phase == .blocked {
            var syncRows: [SettingsListRow] = []
            if state.needsDestructiveConfirmation {
                syncRows.append(SettingsListRow(
                    id: "music.accounts.sync.confirmDestructive",
                    title: "继续同步",
                    detail: state.destructiveConfirmationSummary,
                    detailDestructive: true,
                    enabled: state.canSync,
                    action: { [weak self] in self?.confirmDestructiveSync() }
                ))
            }
            for platform in MusicAccountPlatform.allCases where state.canResetSync(for: platform) {
                syncRows.append(SettingsListRow(
                    id: "music.accounts.sync.reset.\(platform.rawValue)",
                    title: "重置\(platform.title)同步状态",
                    detail: state.syncBlockedIssue(for: platform)?.title ?? "同步待处理",
                    detailDestructive: true,
                    enabled: state.canExecuteSyncReset(for: platform),
                    action: { [weak self] in self?.confirmResetSync(platform) }
                ))
            }
            if !syncRows.isEmpty {
                sections.append(SettingsListSection(id: "sync", title: "歌单同步", rows: syncRows))
            }
        }
        sections.append(SettingsListSection(id: "transfer", rows: transferRows))
        applySections(sections)
    }

    func openAccountFile() {
        guard canPresentPageAlert, transfer.beginSelection() else { return }
        exportingFile = false
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.json], asCopy: false)
        picker.allowsMultipleSelection = false
        picker.delegate = self
        filePicker = picker
        if !presentPageModal(picker) {
            filePicker = nil
            transfer.selectionCancelled()
        }
    }

    private func saveFile(_ url: URL) {
        guard isPageActive, transfer.canTransfer else {
            transfer.selectionCancelled()
            return
        }
        exportingFile = true
        let picker = UIDocumentPickerViewController(forExporting: [url], asCopy: true)
        picker.delegate = self
        filePicker = picker
        if !presentPageModal(picker) {
            filePicker = nil
            transfer.selectionCancelled()
        }
    }

    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard controller === filePicker else { return }
        if exportingFile {
            filePicker = nil
            dismissPageModal(controller) { [weak self] in
                // Destination belongs to the user. Cleanup touches only our private staging directory.
                self?.transfer.exportFinished()
            }
            return
        }
        let selection = transfer.selectionID
        dismissPageModal(controller) { [weak self] in
            guard let self, controller === filePicker else { return }
            filePicker = nil
            guard transfer.selectionID == selection else { return }
            guard isPageActive, transfer.canTransfer, let file = urls.first else {
                transfer.selectionCancelled()
                return
            }
            transfer.importFile(file)
        }
    }

    func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
        guard controller === filePicker else { return }
        filePicker = nil
        transfer.selectionCancelled()
    }

    private func confirmImport(_ plan: MusicAccountImportPlan) {
        guard isPageActive, transfer.canTransfer else {
            transfer.selectionCancelled()
            return
        }
        let alert = presentPageAlert(
            title: "已有平台账号",
            message: plan.conflicts.map(\.title).joined(separator: "、"),
            actions: [
                PageAlertAction(title: "取消", style: .cancel) { [weak self] in
                    self?.transfer.selectionCancelled()
                },
                PageAlertAction(title: "跳过", style: .default) { [weak self] in
                    guard let self, transfer.plan === plan else { return }
                    transfer.commit(.skip)
                },
                PageAlertAction(title: "覆盖", style: .default) { [weak self] in
                    guard let self, transfer.plan === plan else { return }
                    transfer.commit(.overwrite)
                },
            ]
        )
        if alert == nil { transfer.selectionCancelled() }
    }

    private func showTransferResult(_ message: String) {
        guard transfer.canTransfer else { return }
        enqueueNotice(title: "账号导入导出", message: message)
    }

    func openAccount(_ row: MusicAccountStatus) {
        guard let context = state.context(for: row) else { return }
        switch row.phase {
        case .signedIn, .requiresValidation:
            openDetail(row.platform)
        default:
            if row.canLogin { openLogin(row, context: context) }
        }
    }

    private func openDetail(_ platform: MusicAccountPlatform) {
        let controller = MusicAccountDetailViewController(
            state: state,
            platform: platform,
            appearance: appearance,
            artworkImages: owner.artworkImages,
            actions: MusicAccountDetailActions(
                perform: { [weak self] in self?.performCurrent($0, platform: platform) },
                logout: { [weak self] in self?.confirmLogout(platform) },
                recommend: { [weak self] kind, title in self?.recommend(platform, kind: kind, title: title) },
                confirmDestructiveSync: { [weak self] in self?.confirmDestructiveSync() },
                resetSync: { [weak self] in self?.confirmResetSync(platform) },
                close: { [weak self] in self?.closeDetail(animated: true) }
            )
        )
        detail = controller
        if !presentPageModal(controller) { detail = nil }
    }

    private func closeDetail(animated: Bool, completion: (() -> Void)? = nil) {
        guard let detail else {
            completion?()
            return
        }
        detail.dismissSelf(animated: animated, completion: completion)
    }

    private func closeDetailIfSignedOut() {
        guard let platform = detail?.platform, !state.pendingAccounts.contains(platform) else { return }
        let phase = state.rows.first { $0.id == platform }?.account.phase
        if phase != .signedIn, phase != .requiresValidation { closeDetail(animated: true) }
    }

    /// Detail actions always rebuild their context from the current row, never from the opening snapshot.
    private func performCurrent(_ action: MusicAccountSettingsAction, platform: MusicAccountPlatform) {
        guard let row = state.accounts?.rows.first(where: { $0.platform == platform }),
              let context = state.context(for: row) else { return }
        perform(action, context: context)
    }

    private func confirmLogout(_ platform: MusicAccountPlatform) {
        guard let host = detail, host.presentedViewController == nil,
              let row = state.accounts?.rows.first(where: { $0.platform == platform }),
              let context = state.context(for: row) else { return }
        let name = MusicAccountSettingsSummary(account: row).detail(at: Date()).name
        dialogPlatform = platform
        let presented = host.presentPageConfirmation(
            title: "退出登录？",
            message: [platform.title, name].compactMap { $0 }.joined(separator: " · "),
            confirmTitle: "退出登录",
            cancelTitle: "取消",
            destructive: true,
            onConfirm: { [weak self] in
                guard let self else { return }
                dialogPlatform = nil
                guard state.isCurrent(context) else { return }
                perform(.logout, context: context)
            },
            onCancel: { [weak self] in
                self?.dialogPlatform = nil
            }
        )
        if presented == nil { dialogPlatform = nil }
    }

    private func confirmDestructiveSync() {
        guard isPageActive, state.isActive, state.needsDestructiveConfirmation else { return }
        let host: UIViewController = detail ?? self
        guard host.presentedViewController == nil else { return }
        dialogPlatform = detail?.platform
        let presented = host.presentPageConfirmation(
            title: "确认继续同步？",
            message: state.destructiveConfirmationSummary,
            confirmTitle: "继续同步",
            cancelTitle: "取消",
            destructive: true,
            onConfirm: { [weak self] in
                self?.isDestructiveSyncAlertPresented = false
                self?.dialogPlatform = nil
                self?.state.confirmDestructiveSynchronize()
            },
            onCancel: { [weak self] in
                self?.isDestructiveSyncAlertPresented = false
                self?.dialogPlatform = nil
            }
        )
        isDestructiveSyncAlertPresented = presented != nil
        if presented == nil { dialogPlatform = nil }
    }

    private func confirmResetSync(_ platform: MusicAccountPlatform) {
        guard isPageActive, state.isActive, state.canExecuteSyncReset(for: platform) else { return }
        let host: UIViewController = detail ?? self
        guard host.presentedViewController == nil else { return }
        let reason = state.syncBlockedIssue(for: platform)?.title
        dialogPlatform = platform
        let presented = host.presentPageConfirmation(
            title: "重置同步状态？",
            message: [platform.title, reason].compactMap { $0 }.joined(separator: " · "),
            confirmTitle: "重置",
            cancelTitle: "取消",
            destructive: true,
            onConfirm: { [weak self] in
                self?.dialogPlatform = nil
                self?.state.resetSyncAccount(platform)
            },
            onCancel: { [weak self] in
                self?.dialogPlatform = nil
            }
        )
        if presented == nil { dialogPlatform = nil }
    }

    private func recommend(_ platform: MusicAccountPlatform, kind: String, title: String) {
        guard let row = state.accounts?.rows.first(where: { $0.platform == platform }),
              row.phase == .signedIn, let context = state.context(for: row) else { return }
        closeDetail(animated: true) { [weak self] in
            guard let self, state.isCurrent(context) else { return }
            router?.showPublicBrowse(.accountRecommendation(platform, kind: kind, title: title), title: title)
        }
    }

    private func openLogin(_ row: MusicAccountStatus, context: MusicAccountSettingsContext) {
        guard row.canLogin, state.isCurrent(context), let service = owner.accounts else { return }
        presentPageModal(
            MusicAccountLoginViewController(
                owner: owner, service: service, platform: row.platform, appearance: appearance
            )
        )
    }

    private func perform(_ action: MusicAccountSettingsAction, context: MusicAccountSettingsContext) {
        state.perform(action, context: context)
        guard case .feedback(_) = action,
              let feedback = state.feedback, let ticket = feedback.confirmation else { return }
        let host: UIViewController = detail ?? self
        let show = { [weak self, weak host] in
            guard let self, let host, isPageVisible, state.isCurrent(context),
                  feedback.confirmation == ticket else { return }
            dialogPlatform = context.platform
            host.presentPageAlert(
                title: "允许通过HTTP发送酷狗听音记录？",
                message: "HTTP传输会暴露记录、设备标识及CSCC会话信息，可能被窃取或伪造。",
                actions: [
                    PageAlertAction(title: "取消", style: .cancel) { feedback.dismissConfirmation() },
                    PageAlertAction(title: "允许", style: .default) { feedback.confirm(ticket) },
                ]
            )
        }
        if let previous = host.presentedViewController as? UIAlertController {
            previous.dismissSelf(animated: true, completion: show)
        } else {
            show()
        }
    }

    private func showFailure() {
        // The floating detail reports the same state errors while it covers this page.
        guard let error = state.error, !error.isEmpty else {
            clearErrorSource("accounts")
            return
        }
        guard detail == nil, !transfer.busy else { return }
        syncErrorSource("accounts", message: error, retry: { [weak state] in
            state?.retry()
        })
    }
}
#endif
