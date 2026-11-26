#if os(iOS)
import UIKit
import MeowRuntime
import MeowUI

@MainActor
final class FilmManualMatchEditor: SettingsFormViewController {
    private let owner: FilmPlaybackSettingsModel
    private let original: FilmManualMatch?
    private let id: String
    private var kind: String
    private let siteFields = UIStackView(), shareFields = UIStackView()
    private lazy var target = addTextField("影片身份", id: "film.manual.target", value: original?.targetID ?? "")
    private lazy var name = addTextField("名称", id: "film.manual.name", value: original?.name ?? "")
    private lazy var sourceKey = addTextField("来源身份", id: "film.manual.source",
        value: original?.value["sourceKey"]?.filmText ?? "", in: siteFields)
    private lazy var detailID = addTextField("站点详情 ID", id: "film.manual.detail",
        value: original?.value["detailId"]?.filmScalar ?? "", in: siteFields)
    private lazy var share = addTextField("分享地址", id: "film.manual.share",
        value: original?.value["share"]?.filmObject?["url"]?.filmText ?? "", in: shareFields)
    private lazy var password = addTextField("提取码", id: "film.manual.password",
        value: original?.value["share"]?.filmObject?["password"]?.filmText ?? "", in: shareFields)
    private lazy var season = addTextField("季线索", id: "film.manual.season",
        value: original?.value["seasonHint"]?.filmScalar ?? "")
    private lazy var kindButton = filmOption("", id: "film.manual.kind", disclosure: true) { [weak self] in self?.chooseKind() }
    private lazy var detailTypeButton = filmOption("", id: "film.manual.detail-type", disclosure: true) { [weak self] in self?.chooseDetailType() }
    private lazy var providerButton = filmOption("", id: "film.manual.provider", disclosure: true) { [weak self] in self?.chooseProvider() }
    private lazy var saveButton = PageHeaderTextButton(title: "保存") { [weak self] in self?.save() }
    private var controls: [UIControl] = []
    private var numericDetailID: Bool
    private var provider: FilmPanProvider?
    private var enabled: Bool
    private var autoDisable: Bool
    private var task: Task<Void, Never>?

    init(owner: FilmPlaybackSettingsModel, item: FilmManualMatch?, themeProvider: any PageThemeProviding) {
        self.owner = owner; original = item
        id = item?.id ?? UUID().uuidString.lowercased(); kind = item?.kind ?? "site"
        enabled = item?.enabled ?? true; autoDisable = item?.autoDisable ?? true
        numericDetailID = item?.value["detailId"]?.filmNumber != nil
        provider = item?.value["provider"]?.filmText.flatMap(FilmPanProvider.init(rawValue:))
        super.init(themeProvider: themeProvider)
        pageTitle = item == nil ? "添加人工匹配" : "编辑人工匹配"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.manual.editor"
        form.accessibilityIdentifier = "film.manual.form"
        pageHeader.setTrailing([saveButton])
        form.addSection(filmOptionRow([kindButton]))
        controls.append(kindButton)
        _ = target; _ = name
        if owner.playbackSnapshot?() != nil || owner.library?.history.isEmpty == false {
            let choose = filmOption("选择影片", id: "film.manual.choose-target", disclosure: true) { [weak self] in self?.chooseTarget() }
            form.addSection(filmOptionRow([choose])); controls.append(choose)
        }
        siteFields.axis = .vertical; shareFields.axis = .vertical
        _ = sourceKey
        if !siteChoices.isEmpty {
            let choose = filmOption("选择站点详情", id: "film.manual.choose-source", disclosure: true) { [weak self] in self?.chooseSource() }
            siteFields.addArrangedSubview(filmOptionRow([choose])); controls.append(choose)
        }
        _ = detailID; siteFields.addArrangedSubview(filmOptionRow([detailTypeButton]))
        form.addSection(siteFields)
        shareFields.addArrangedSubview(filmOptionRow([providerButton]))
        _ = share; _ = password; form.addSection(shareFields)
        share.keyboardType = .URL; share.textContentType = .none; password.textContentType = .none
        _ = season; season.keyboardType = .numberPad
        let enabled = ManagementFormView.check("启用", value: self.enabled) { [weak self] in self?.enabled = $0 }
        enabled.accessibilityIdentifier = "film.manual.enabled"
        let autoDisable = ManagementFormView.check("连续失败自动停用", value: self.autoDisable) { [weak self] in self?.autoDisable = $0 }
        autoDisable.accessibilityIdentifier = "film.manual.auto-disable"
        form.addSection(filmOptionRow([enabled, autoDisable]))
        controls += [detailTypeButton, providerButton, enabled, autoDisable, target, name, sourceKey, detailID, share, password, season]
        if let original {
            let failures = UILabel()
            failures.text = "失败次数：\(original.failureCount)"; failures.numberOfLines = 0
            failures.font = .preferredFont(forTextStyle: .subheadline); failures.adjustsFontForContentSizeCategory = true
            failures.accessibilityIdentifier = "film.manual.failures"
            registerMutedLabel(failures)
            form.addSection(filmOptionRow([failures]))
            let remove = filmOption("删除", id: "film.manual.remove", destructive: true) { [weak self] in self?.remove() }
            form.addSection(filmOptionRow([remove])); controls.append(remove)
        }
        update(); applyTheme()
    }

    private func update() {
        siteFields.isHidden = kind != "site"; shareFields.isHidden = kind == "site"
        kindButton.configuration?.title = "类型：\(kind == "site" ? "站点" : "分享")"
        kindButton.accessibilityLabel = "类型"; kindButton.accessibilityValue = kind == "site" ? "站点" : "分享"
        detailTypeButton.configuration?.title = "详情 ID 类型：\(numericDetailID ? "数字" : "文本")"
        detailTypeButton.accessibilityLabel = "详情 ID 类型"; detailTypeButton.accessibilityValue = numericDetailID ? "数字" : "文本"
        providerButton.configuration?.title = "网盘：\(provider?.title ?? "未选择")"
        providerButton.accessibilityLabel = "网盘"; providerButton.accessibilityValue = provider?.title ?? "未选择"
        for control in controls { control.isEnabled = task == nil }
        saveButton.isEnabled = task == nil
    }

    private func chooseKind() {
        choose("类型", choices: [("站点", { [weak self] in self?.kind = "site"; self?.update() }),
                                  ("分享", { [weak self] in self?.kind = "share"; self?.update() })])
    }
    private func chooseProvider() {
        choose("网盘", choices: FilmPanProvider.allCases.map { provider in
            (provider.title, { [weak self] in self?.provider = provider; self?.update() })
        })
    }
    private func chooseTarget() {
        var choices: [(String, () -> Void)] = []
        var identities = Set<Data>()
        if let snapshot = owner.playbackSnapshot?() {
            identities.insert(Data(snapshot.targetID.utf8))
            choices.append((snapshot.work.title, { [weak self] in
                self?.target.text = snapshot.targetID
                self?.name.text = snapshot.work.title
            }))
        }
        for item in owner.library?.history ?? [] where identities.insert(Data(item.targetID.utf8)).inserted {
            choices.append((item.title, { [weak self] in
                self?.target.text = item.targetID
                self?.name.text = item.title
            }))
        }
        choose("影片", choices: choices)
    }
    private var siteChoices: [(title: String, sourceKey: String, detailID: String, isNumeric: Bool)] {
        guard let snapshot = owner.playbackSnapshot?() else { return [] }
        return snapshot.sourceOptions.compactMap { row in
            guard row.kind == "site",
                  let decoded = try? RuntimeJSON.decode(Data(row.id.utf8)),
                  let tuple = decoded.filmArray, tuple.count == 2,
                  let sourceKey = tuple[0].filmText, !sourceKey.isEmpty,
                  let detailScalar = tuple[1].filmScalar, !detailScalar.isEmpty else { return nil }
            return (row.displayTitle, sourceKey, detailScalar, tuple[1].filmNumber != nil)
        }
    }
    private func chooseSource() {
        choose("站点详情", choices: siteChoices.map { choice in
            (choice.title, { [weak self] in
                self?.sourceKey.text = choice.sourceKey
                self?.detailID.text = choice.detailID
                self?.numericDetailID = choice.isNumeric
                self?.update()
            })
        })
    }
    private func chooseDetailType() {
        choose("详情 ID 类型", choices: [
            ("文本", { [weak self] in self?.numericDetailID = false; self?.update() }),
            ("数字", { [weak self] in self?.numericDetailID = true; self?.update() }),
        ])
    }
    private func choose(_ title: String, choices: [(String, () -> Void)]) {
        guard canPresentPageAlert, !choices.isEmpty else { return }
        presentPageActionSheet(
            title: title,
            sourceView: form,
            actions: choices.map { name, action in PageAlertAction(title: name, handler: action) }
        )
    }

    private func item() throws -> FilmManualMatch {
        try FilmManualMatchDraft(
            id: id,
            kind: kind,
            targetID: target.text ?? "",
            name: name.text ?? "",
            sourceKey: sourceKey.text ?? "",
            detailID: detailID.text ?? "",
            numericDetailID: numericDetailID,
            provider: provider,
            shareURL: share.text ?? "",
            sharePassword: password.text ?? "",
            seasonText: season.text ?? "",
            enabled: enabled,
            autoDisable: autoDisable
        ).build(from: original)
    }

    private func save() {
        guard task == nil else { return }
        view.endEditing(true)
        do {
            let value = try item()
            let owner = self.owner
            performMutation(retry: { [weak self] in self?.save() }) {
                try await owner.saveManual(value)
            }
        } catch { enqueueError(error.localizedDescription) }
    }

    private func remove() {
        guard canPresentPageAlert else { return }
        presentPageConfirmation(title: "删除人工匹配", message: original?.name, confirmTitle: "确认", destructive: true) { [weak self] in
            guard let self, task == nil else { return }
            let owner = self.owner, id = self.id
            self.performMutation(retry: { [weak self] in self?.remove() }) {
                try await owner.removeManual(id: id)
            }
        }
    }

    private func performMutation(retry: @escaping @MainActor () -> Void,
                                 operation: @escaping @MainActor () async throws -> Void) {
        task = Task { [weak self] in
            defer { self?.task = nil; self?.update() }
            do {
                try await operation()
                if let self, self.isPageActive, self.navigationController?.topViewController === self {
                    self.navigationController?.popViewController(animated: true)
                }
            } catch {
                self?.enqueueError(error.localizedDescription, retry: retry)
            }
        }
        update()
    }
}
#endif
