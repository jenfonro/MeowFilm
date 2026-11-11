#if os(iOS)
import SwiftUI
import UIKit
import Combine
import MeowUI
import UniformTypeIdentifiers

@MainActor
final class MusicAccountsViewController: MusicSettingsPageViewController<MusicAccountsSettingsContent>, UIDocumentPickerDelegate {
    private let owner: MusicViewModel
    private let state: MusicAccountsSettingsState
    let transfer: MusicAccountTransferState
    private weak var filePicker: UIDocumentPickerViewController?
    private var exportingFile = false
    private weak var transferAlert: UIAlertController?
    private weak var router: (any MusicUIRouting)?
    private weak var accessAlert: UIAlertController?
    private weak var detail: MusicAccountDetailViewController?
    private var dialogAccess: UUID?
    private var dialogPlatform: MusicAccountPlatform?

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore, router: (any MusicUIRouting)?) {
        owner = model
        state = MusicAccountsSettingsState(owner: model)
        transfer = MusicAccountTransferState(owner: model)
        self.router = router
        super.init(appearance: appearance, content: MusicAccountsSettingsContent(
            state: state, transfer: transfer, theme: PageTheme(colors: appearance.pagePalette)
        ))
        pageTitle = "平台账号"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        transfer.confirm = { [weak self] in self?.confirmImport($0) }
        transfer.exported = { [weak self] in self?.saveFile($0) }
        transfer.result = { [weak self] in self?.showTransferResult($0) }
        observe(owner.accessChanges) { [weak self] in
            guard let self else { return }
            invalidateErrorPresentation()
            state.reconcileAccess()
            transfer.reconcileAccess()
            pendingTransferResult = nil
            filePicker?.dismiss(animated: false)
            transferAlert?.dismiss(animated: false)
            filePicker = nil; transferAlert = nil
            if !owner.isAvailable || owner.isClosed || dialogAccess != owner.accountAccessGeneration {
                accessAlert?.dismiss(animated: false)
                closeDetail(animated: false)
            }
        }
        observe(owner.accountChanges) { [weak self] platform in
            guard let self else { return }
            state.accountSessionChanged(platform)
            if dialogPlatform == platform { accessAlert?.dismiss(animated: false) }
        }
        observe(state.$rows.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            self?.closeDetailIfSignedOut()
        }
        observe(state.errorChanges) { [weak self] in self?.showFailure() }
        observe(transfer.$busy.removeDuplicates().map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            self?.showPendingTransferResult()
        }
    }

    override func pageDidBecomeActive() { state.start(); transfer.start() }

    override func pageDidBecomeVisible() { showFailure(); showPendingTransferResult() }

    override func pageDidFinishAppearing() {
        super.pageDidFinishAppearing()
        showPendingTransferResult()
    }

    override func pageDidBecomeInactive() {
        state.close(); transfer.close()
        pendingTransferResult = nil
        filePicker?.dismiss(animated: false); transferAlert?.dismiss(animated: false)
        filePicker = nil; transferAlert = nil
    }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicAccountsSettingsContent(
            state: state, transfer: transfer, theme: theme,
            openAccount: { [weak self] in self?.openAccount($0) },
            importAccounts: { [weak self] in self?.openAccountFile() },
            exportAccounts: { [weak self] in self?.transfer.exportFile() }
        ))
    }

    func openAccountFile() {
        guard presentedViewController == nil, transfer.beginSelection() else { return }
        exportingFile = false
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.json], asCopy: false)
        picker.allowsMultipleSelection = false; picker.delegate = self
        filePicker = picker
        present(picker, animated: true)
    }

    private func saveFile(_ url: URL) {
        guard isPageActive, transfer.available, presentedViewController == nil else {
            transfer.selectionCancelled(); return
        }
        exportingFile = true
        let picker = UIDocumentPickerViewController(forExporting: [url], asCopy: true)
        picker.delegate = self; filePicker = picker
        present(picker, animated: true)
    }

    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard controller === filePicker else { return }
        filePicker = nil
        if exportingFile {
            // Destination belongs to the user. Cleanup touches only our private staging directory.
            transfer.exportFinished(); return
        }
        let selection = transfer.selectionID
        dismissPageModal(controller) { [weak self] in
            guard let self, transfer.selectionID == selection else { return }
            guard isPageActive, transfer.available, let file = urls.first else {
                transfer.selectionCancelled(); return
            }
            transfer.importFile(file)
        }
    }

    func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
        guard controller === filePicker else { return }
        filePicker = nil; transfer.selectionCancelled()
    }

    private func confirmImport(_ plan: MusicAccountImportPlan) {
        guard isPageActive, transfer.available, presentedViewController == nil else {
            transfer.selectionCancelled(); return
        }
        let alert = UIAlertController(title: "已有平台账号", message: plan.conflicts.map(\.title).joined(separator: "、"), preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel) { [weak self] _ in self?.transfer.selectionCancelled() })
        alert.addAction(UIAlertAction(title: "跳过", style: .default) { [weak self, weak alert] _ in
            guard let self, let alert else { return }
            dismissPageModal(alert) { [weak self] in
                guard let self, transfer.plan === plan else { return }
                transfer.commit(.skip)
            }
        })
        alert.addAction(UIAlertAction(title: "覆盖", style: .default) { [weak self, weak alert] _ in
            guard let self, let alert else { return }
            dismissPageModal(alert) { [weak self] in
                guard let self, transfer.plan === plan else { return }
                transfer.commit(.overwrite)
            }
        })
        transferAlert = alert; present(alert, animated: true)
    }

    private func showTransferResult(_ message: String) {
        pendingTransferResult = message
        showPendingTransferResult()
    }

    private var pendingTransferResult: String?
    private func showPendingTransferResult() {
        guard transfer.available, let message = pendingTransferResult, canPresentPageAlert else { return }
        pendingTransferResult = nil
        let alert = UIAlertController(title: "账号导入导出", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "确定", style: .default))
        transferAlert = alert; present(alert, animated: true)
    }

    private func openAccount(_ row: MusicAccountStatus) {
        guard let context = state.context(for: row), presentedViewController == nil else { return }
        switch row.phase {
        case .signedIn, .requiresValidation:
            openDetail(row.platform, context: context)
        default:
            if row.canLogin { openLogin(row, context: context) }
        }
    }

    private func openDetail(_ platform: MusicAccountPlatform, context: MusicAccountSettingsContext) {
        let controller = MusicAccountDetailViewController(
            state: state, platform: platform, appearance: appearance,
            actions: .init(
                perform: { [weak self] in self?.performCurrent($0, platform: platform) },
                logout: { [weak self] in self?.confirmLogout(platform) },
                recommend: { [weak self] kind, title in self?.recommend(platform, kind: kind, title: title) },
                close: { [weak self] in self?.closeDetail(animated: true) }
            )
        )
        detail = controller
        dialogAccess = context.accessGeneration
        present(controller, animated: true)
    }

    private func closeDetail(animated: Bool, completion: (() -> Void)? = nil) {
        guard let detail, detail.presentingViewController != nil, !detail.isBeingDismissed else {
            completion?()
            return
        }
        detail.dismiss(animated: animated, completion: completion)
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
        let alert = UIAlertController(
            title: "退出登录？", message: [platform.title, name].compactMap { $0 }.joined(separator: " · "),
            preferredStyle: .alert
        )
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "退出登录", style: .destructive) { [weak self] _ in
            guard let self, state.isCurrent(context) else { return }
            perform(.logout, context: context)
        })
        dialogAccess = context.accessGeneration; dialogPlatform = platform; accessAlert = alert
        host.present(alert, animated: true)
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
        guard row.canLogin, state.isCurrent(context), let service = owner.accounts,
              presentedViewController == nil else { return }
        present(
            MusicAccountLoginViewController(
                owner: owner, service: service, platform: row.platform, appearance: appearance
            ),
            animated: true
        )
    }

    private func perform(_ action: MusicAccountSettingsAction, context: MusicAccountSettingsContext) {
        state.perform(action, context: context)
        guard case .feedback(_) = action,
              let feedback = state.feedback, let ticket = feedback.confirmation else { return }
        let alert = UIAlertController(
            title: "允许通过HTTP发送酷狗听音记录？",
            message: "HTTP传输会暴露记录、设备标识及CSCC会话信息，可能被窃取或伪造。",
            preferredStyle: .alert
        )
        alert.addAction(UIAlertAction(title: "取消", style: .cancel) { _ in feedback.dismissConfirmation() })
        alert.addAction(UIAlertAction(title: "允许", style: .default) { _ in feedback.confirm(ticket) })
        let host: UIViewController = detail ?? self
        let show = { [weak self] in
            guard let self, isPageVisible, state.isCurrent(context), feedback.confirmation == ticket else { return }
            dialogAccess = context.accessGeneration; dialogPlatform = context.platform; accessAlert = alert
            host.present(alert, animated: true)
        }
        if let previous = host.presentedViewController as? UIAlertController {
            previous.dismiss(animated: true, completion: show)
        } else {
            show()
        }
    }

    private func showFailure() {
        // The floating detail reports the same state errors while it covers this page.
        guard detail == nil, !transfer.busy else { return }
        let access = owner.accountAccessGeneration
        reportError(state.error, retry: { [weak state] in state?.retry(generation: access) })
    }
}

@MainActor
struct MusicAccountsSettingsContent: View {
    @ObservedObject var state: MusicAccountsSettingsState
    @ObservedObject var transfer: MusicAccountTransferState
    let theme: PageTheme
    var openAccount: (MusicAccountStatus) -> Void = { _ in }
    var importAccounts: () -> Void = {}
    var exportAccounts: () -> Void = {}

    var body: some View {
        List {
            Section {
                if state.accounts == nil {
                    Text("账号服务不可用")
                        .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                        .modifier(MusicSettingsRowStyle(theme: theme))
                }
                ForEach(MusicAccountPlatform.allCases) { platform in
                    let summary = state.rows.first { $0.id == platform }
                    Button {
                        if let summary { openAccount(summary.account) }
                    } label: {
                        MusicPlatformAccountRow(
                            platform: platform, summary: summary, theme: theme,
                            loading: state.pendingAccounts.contains(platform) || summary == nil && state.busy
                        )
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("music.accounts." + platform.rawValue)
                    .disabled(summary == nil || !state.available || transfer.busy || state.pendingAccounts.contains(platform))
                    .modifier(MusicSettingsRowStyle(theme: theme))
                }
            }
            Section {
                Button("导入账号", action: importAccounts)
                    .accessibilityIdentifier("music.accounts.import")
                    .modifier(MusicSettingsRowStyle(theme: theme))
                Button("导出账号", action: exportAccounts)
                    .accessibilityIdentifier("music.accounts.export")
                    .modifier(MusicSettingsRowStyle(theme: theme))
                if transfer.busy { ProgressView().accessibilityLabel("账号导入导出中") }
            }
            .disabled(!state.available || transfer.service == nil || transfer.busy)
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }
}

private struct MusicPlatformAccountRow: View {
    let platform: MusicAccountPlatform
    let summary: MusicAccountSettingsSummary?
    let theme: PageTheme
    let loading: Bool

    var body: some View {
        let detail = summary?.detail(at: Date()) ?? .init(status: "不可用")
        HStack(spacing: 12) {
            MusicPlatformLogo(assetName: platform.settingsLogoName, size: 36, theme: theme)
            VStack(alignment: .leading, spacing: 6) {
                Text(platform.title)
                    .font(.body.weight(.medium))
                    .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
                if loading {
                    ProgressView().accessibilityLabel("读取账号")
                } else {
                    accountDetail(detail)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            if let account = summary?.account {
                if account.phase == .signedOut, account.canLogin {
                    Text("登录")
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color(uiColor: UIColor(theme.accent)))
                        .frame(minWidth: 44, minHeight: 44)
                        .fixedSize(horizontal: true, vertical: false)
                } else if account.phase != .unavailable {
                    Image(systemName: "chevron.right")
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                        .frame(width: 16)
                        .accessibilityHidden(true)
                }
            }
        }
        .frame(minHeight: 56)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }

    @ViewBuilder
    private func accountDetail(_ detail: MusicAccountSettingsDetail) -> some View {
        let status = Text(detail.status)
            .foregroundColor(detail.isNonVIP ? theme.settingsDanger : Color(uiColor: UIColor(theme.muted)))
        Group {
            if let name = detail.name {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 4) {
                        Text(name)
                        Text("·").accessibilityHidden(true)
                        status
                    }
                    .lineLimit(1)
                    Text("\(name) · \(status)")
                        .fixedSize(horizontal: false, vertical: true)
                }
            } else {
                status
            }
        }
        .font(.subheadline)
        .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
    }
}
#endif
