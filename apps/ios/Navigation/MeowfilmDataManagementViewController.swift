import UIKit
import Combine
import UniformTypeIdentifiers
import MeowDebug
import MeowStorage
import MeowUI

@MainActor
final class MeowfilmDataManagementViewController: SettingsListViewController, UIDocumentPickerDelegate {
    private enum ActivePicker {
        case importing
        case exporting(URL)
    }

    private let transferProvider: @MainActor () -> MeowfilmSettingsTransfer?
    private let resetUserData: (@MainActor () async throws -> Void)?
    private weak var filePicker: UIDocumentPickerViewController?
    private var activePicker: ActivePicker?
    private var requestingReset = false

    init(
        themeProvider: any PageThemeProviding,
        transferProvider: @escaping @MainActor () -> MeowfilmSettingsTransfer?,
        resetUserData: (@MainActor () async throws -> Void)? = nil
    ) {
        self.transferProvider = transferProvider
        self.resetUserData = resetUserData
        super.init(themeProvider: themeProvider)
        pageTitle = "数据管理"
    }

    private var transfer: MeowfilmSettingsTransfer? { transferProvider() }
    private var isTransferBusy: Bool { activePicker != nil || (transfer?.isBusy ?? false) }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.accessibilityIdentifier = "settings.data.list"
        if let transfer {
            observe(transfer.$isBusy.removeDuplicates().dropFirst().map { _ in () }.eraseToAnyPublisher(), whenVisible: true) { [weak self] in
                self?.renderSections()
            }
        }
        renderSections()
    }

    override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        renderSections()
    }

    override func pageDidBecomeInactive() {
        clearActivePicker()
        if let picker = filePicker {
            filePicker = nil
            dismissPageModal(picker, animated: false)
        }
        super.pageDidBecomeInactive()
    }

    private func renderSections() {
        guard isViewLoaded else { return }
        let canTransfer = transfer != nil && !isTransferBusy && !requestingReset
        var transferRows: [SettingsListRow] = [
            SettingsListRow(
                id: "settings.data.import",
                title: "导入全部数据",
                enabled: canTransfer,
                action: { [weak self] in self?.openImportPicker() }
            ),
            SettingsListRow(
                id: "settings.data.export",
                title: "导出全部数据",
                enabled: canTransfer,
                action: { [weak self] in self?.startExport() }
            ),
        ]
        if isTransferBusy {
            transferRows.append(SettingsListRow(
                id: "settings.data.busy",
                title: "正在处理数据…",
                accessory: .loading(accessibilityLabel: "正在处理数据"),
                enabled: false
            ))
        }

        var sections: [SettingsListSection] = [
            SettingsListSection(id: "transfer", rows: transferRows),
        ]

        if resetUserData != nil {
            sections.append(SettingsListSection(id: "danger", rows: [
                SettingsListRow(
                    id: "settings.clearUserData",
                    title: requestingReset ? "正在记录清除请求…" : "清空所有数据",
                    destructive: true,
                    enabled: !requestingReset && !isTransferBusy,
                    action: { [weak self] in _ = self?.confirmUserDataReset() }
                ),
            ]))
        }
        applySections(sections)
    }

    func openImportPicker() {
        guard transfer != nil else {
            enqueueError("数据服务暂不可用")
            return
        }
        guard canPresentPageAlert, !isTransferBusy, !requestingReset else { return }
        activePicker = .importing
        renderSections()

        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.json], asCopy: false)
        picker.allowsMultipleSelection = false
        picker.delegate = self
        filePicker = picker
        if !presentPageModal(picker) {
            filePicker = nil
            activePicker = nil
            renderSections()
        }
    }

    func startExport() {
        guard let transfer else {
            enqueueError("数据服务暂不可用")
            return
        }
        guard !isTransferBusy, !requestingReset else { return }
        runPageTask(key: "data.export", onFinally: { [weak self] in
            self?.renderSections()
        }) { [weak self] in
            let bytes = try await transfer.exportData()
            guard let self, self.isPageActive else { return }
            self.clearActivePicker()
            let root = AppTempDirectory.rootURL()
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            let url = root.appendingPathComponent("meowfilm-settings.json", isDirectory: false)
            try bytes.write(to: url, options: [.atomic])
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            self.presentExportPicker(url)
        }
        renderSections()
    }

    private func presentExportPicker(_ url: URL) {
        guard isPageActive, canPresentPageAlert else {
            try? FileManager.default.removeItem(at: url)
            return
        }
        activePicker = .exporting(url)
        renderSections()
        let picker = UIDocumentPickerViewController(forExporting: [url], asCopy: true)
        picker.delegate = self
        filePicker = picker
        if !presentPageModal(picker) {
            filePicker = nil
            clearActivePicker()
            renderSections()
        }
    }

    private func importSelectedFile(_ url: URL) {
        guard let transfer else {
            activePicker = nil
            renderSections()
            enqueueError("数据服务暂不可用")
            return
        }
        runPageTask(key: "data.import", onFinally: { [weak self] in
            self?.activePicker = nil
            self?.renderSections()
        }) { [weak self] in
            let bytes = try await ImportedFileReader.read(url)
            try await transfer.importData(bytes)
            guard let self, self.isPageActive else { return }
            self.enqueueNotice(title: "数据管理", message: "已导入全部设置数据")
        }
        renderSections()
    }

    private func clearActivePicker() {
        if case .exporting(let url) = activePicker {
            try? FileManager.default.removeItem(at: url)
        }
        activePicker = nil
    }

    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard controller === filePicker, let mode = activePicker else { return }
        filePicker = nil
        switch mode {
        case .exporting:
            dismissPageModal(controller) { [weak self] in
                guard let self else { return }
                self.clearActivePicker()
                self.renderSections()
                self.enqueueNotice(title: "数据管理", message: "已导出全部设置数据")
            }
        case .importing:
            dismissPageModal(controller) { [weak self] in
                guard let self else { return }
                guard self.isPageActive, let file = urls.first else {
                    self.clearActivePicker()
                    self.renderSections()
                    return
                }
                self.importSelectedFile(file)
            }
        }
    }

    func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
        guard controller === filePicker else { return }
        filePicker = nil
        clearActivePicker()
        renderSections()
    }

    @discardableResult
    func confirmUserDataReset() -> Bool {
        guard resetUserData != nil, !requestingReset, !isTransferBusy, canPresentPageAlert else { return false }
        return presentPageConfirmation(
            title: "清空所有数据？",
            message: "将删除本机登录凭据、设备身份和密钥、本地歌单、收藏与书架、历史与阅读进度、导入音源与书源、偏好及缓存，无法撤销。云端数据和 App 外的原始文件不受影响。\n\n确认后需从多任务列表关闭 App，再重新打开才能完成清除。",
            confirmTitle: "清除并等待重启",
            cancelTitle: "取消",
            destructive: true,
            onConfirm: { [weak self] in self?.requestUserDataReset() }
        ) != nil
    }

    func requestUserDataReset() {
        guard let resetUserData, !requestingReset else { return }
        requestingReset = true
        renderSections()
        Task { [weak self] in
            do { try await resetUserData() }
            catch {
                self?.requestingReset = false
                self?.renderSections()
                self?.enqueueError("无法记录清除请求，请重试")
            }
        }
    }
}

extension MeowfilmDataManagementViewController: DebugPageInspectable, DebugPageActionable {
    var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "settings.general",
            moduleId: "settings",
            route: "settings.general.data",
            title: pageTitle ?? "数据管理",
            isBusy: isTransferBusy || requestingReset,
            settingsDomain: "general",
            settingsSection: "data"
        )
    }

    func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        var rows: [DebugContentRow] = [
            .init(index: 0, id: "import", title: "导入全部数据", actions: ["open"]),
            .init(index: 1, id: "export", title: "导出全部数据", actions: ["open"]),
        ]
        if resetUserData != nil {
            rows.append(.init(
                index: 2,
                id: "clear_user_data",
                title: requestingReset ? "正在记录清除请求…" : "清空所有数据",
                actions: ["open"]
            ))
        }
        return DebugContentSnapshot(
            target: "settings.general",
            kind: "settings",
            loading: isTransferBusy || requestingReset,
            rows: Array(rows.prefix(maxItems)),
            settings: .object([
                "domain": .string("general"),
                "section": .string("data"),
                "transferAvailable": .bool(transfer != nil),
                "transferBusy": .bool(isTransferBusy),
                "requestingReset": .bool(requestingReset),
                "resetAvailable": .bool(resetUserData != nil),
            ])
        )
    }

    func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "list.item.invoke":
            let id = request.params["id"]?.stringValue
            let idx = request.params["index"]?.intValue
            switch (id, idx) {
            case ("import", _), (nil, 0):
                openImportPicker()
                return .object(["opened": .string("picker.import_settings")])
            case ("export", _), (nil, 1):
                startExport()
                return .object(["started": .string("export_settings")])
            case ("clear_user_data", _), (nil, 2):
                guard confirmUserDataReset() else {
                    throw DebugAPIError.invalidState(
                        "Unable to present reset confirmation alert (canPresentPageAlert=\(canPresentPageAlert), isPageVisible=\(isPageVisible), hasWindow=\(view.window != nil), presented=\(presentedViewController != nil))"
                    )
                }
                return .object(["opened": .string("dialog.reset_confirm")])
            default:
                throw DebugAPIError.invalidArgument("Unknown data management item")
            }

        case "setting.action":
            let cmd = request.params["command"]?.stringValue ?? ""
            switch cmd {
            case "export_settings":
                guard let transfer else { throw DebugAPIError.invalidState("Settings transfer unavailable") }
                let path = request.params["path"]?.stringValue ??
                    AppTempDirectory.rootURL().appendingPathComponent("meowfilm-settings.json").path
                let saved = try await transfer.exportToFile(path: path)
                renderSections()
                return .object(["executed": .bool(true), "command": .string(cmd), "path": .string(saved.path)])
            case "import_settings":
                guard let transfer else { throw DebugAPIError.invalidState("Settings transfer unavailable") }
                guard let path = request.params["path"]?.stringValue, !path.isEmpty else {
                    throw DebugAPIError.invalidArgument("import_settings requires 'path'")
                }
                try await transfer.importFromFile(path: path)
                renderSections()
                return .object(["executed": .bool(true), "command": .string(cmd)])
            case "reset_user_data":
                guard request.params["confirm"]?.stringValue == "RESET_ALL_USER_DATA" else {
                    throw DebugAPIError.invalidState("reset_user_data requires confirm == 'RESET_ALL_USER_DATA'")
                }
                requestUserDataReset()
                return .object(["requested": .bool(true)])
            default:
                throw DebugAPIError.invalidArgument("Unsupported setting.action command: \(cmd)")
            }

        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MeowfilmDataManagementViewController")
        }
    }
}
