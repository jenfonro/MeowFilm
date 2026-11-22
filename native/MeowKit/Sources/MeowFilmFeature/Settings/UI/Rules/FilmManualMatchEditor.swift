#if os(iOS)
import UIKit
import MeowRuntime
import MeowUI

@MainActor
final class FilmManualMatchEditor: FilmSettingsFormController {
    private let owner: FilmPlaybackSettingsModel
    private let original: FilmManualMatch?
    private let id: String
    private var kind: String
    private let siteFields = UIStackView(), shareFields = UIStackView()
    private lazy var target = text("影片身份", id: "film.manual.target", value: original?.targetID ?? "")
    private lazy var name = text("名称", id: "film.manual.name", value: original?.name ?? "")
    private lazy var sourceKey = text("来源身份", id: "film.manual.source",
        value: original?.value["sourceKey"]?.filmText ?? "", in: siteFields)
    private lazy var detailID = text("站点详情 ID", id: "film.manual.detail",
        value: original?.value["detailId"]?.filmScalar ?? "", in: siteFields)
    private lazy var share = text("分享地址", id: "film.manual.share",
        value: original?.value["share"]?.filmObject?["url"]?.filmText ?? "", in: shareFields)
    private lazy var password = text("提取码", id: "film.manual.password",
        value: original?.value["share"]?.filmObject?["password"]?.filmText ?? "", in: shareFields)
    private lazy var season = text("季线索", id: "film.manual.season",
        value: original?.value["seasonHint"]?.filmScalar ?? "")
    private lazy var kindButton = option("", id: "film.manual.kind", disclosure: true) { [weak self] in self?.chooseKind() }
    private lazy var detailTypeButton = option("", id: "film.manual.detail-type", disclosure: true) { [weak self] in self?.chooseDetailType() }
    private lazy var providerButton = option("", id: "film.manual.provider", disclosure: true) { [weak self] in self?.chooseProvider() }
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
        form.addSection(optionRow([kindButton]))
        controls.append(kindButton)
        _ = target; _ = name
        if owner.playbackSnapshot?() != nil || owner.library?.history.isEmpty == false {
            let choose = option("选择影片", id: "film.manual.choose-target", disclosure: true) { [weak self] in self?.chooseTarget() }
            form.addSection(optionRow([choose])); controls.append(choose)
        }
        siteFields.axis = .vertical; shareFields.axis = .vertical
        _ = sourceKey
        if !siteChoices.isEmpty {
            let choose = option("选择站点详情", id: "film.manual.choose-source", disclosure: true) { [weak self] in self?.chooseSource() }
            siteFields.addArrangedSubview(optionRow([choose])); controls.append(choose)
        }
        _ = detailID; siteFields.addArrangedSubview(optionRow([detailTypeButton]))
        form.addSection(siteFields)
        shareFields.addArrangedSubview(optionRow([providerButton]))
        _ = share; _ = password; form.addSection(shareFields)
        share.keyboardType = .URL; share.textContentType = .none; password.textContentType = .none
        _ = season; season.keyboardType = .numberPad
        let enabled = ManagementFormView.check("启用", value: self.enabled) { [weak self] in self?.enabled = $0 }
        enabled.accessibilityIdentifier = "film.manual.enabled"
        let autoDisable = ManagementFormView.check("连续失败自动停用", value: self.autoDisable) { [weak self] in self?.autoDisable = $0 }
        autoDisable.accessibilityIdentifier = "film.manual.auto-disable"
        form.addSection(optionRow([enabled, autoDisable]))
        controls += [detailTypeButton, providerButton, enabled, autoDisable, target, name, sourceKey, detailID, share, password, season]
        if let original {
            let failures = UILabel()
            failures.text = "失败次数：\(original.failureCount)"; failures.numberOfLines = 0
            failures.font = .preferredFont(forTextStyle: .subheadline); failures.adjustsFontForContentSizeCategory = true
            failures.accessibilityIdentifier = "film.manual.failures"
            registerMutedLabel(failures)
            form.addSection(optionRow([failures]))
            let remove = option("删除", id: "film.manual.remove", destructive: true) { [weak self] in self?.remove() }
            form.addSection(optionRow([remove])); controls.append(remove)
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
            identities.insert(Data(snapshot.target.id.utf8))
            choices.append((snapshot.target.title, { [weak self] in
                self?.target.text = snapshot.target.id
                self?.name.text = snapshot.target.title
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
    private var siteChoices: [(String, FilmPlaybackSource)] {
        guard let snapshot = owner.playbackSnapshot?() else { return [] }
        // These are real detail identities, not an installation list masquerading
        // as content or a pan candidate supplied with an invented Cat identity.
        return snapshot.sources.compactMap { row in
            guard row.source.value["engine"] != .string("pan"), row.source.detailID != nil else { return nil }
            return ("\(row.source.name) · \(row.title)", row.source)
        }
    }
    private func chooseSource() {
        choose("站点详情", choices: siteChoices.map { title, source in
            (title, { [weak self] in
                self?.sourceKey.text = source.key
                self?.detailID.text = source.detailID?.filmScalar
                self?.numericDetailID = source.detailID?.filmNumber != nil
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
        guard canPresentPageAlert else { return }
        presentPageActionSheet(
            title: title,
            sourceView: form,
            actions: choices.map { name, action in PageAlertAction(title: name, handler: action) }
        )
    }

    private func item() throws -> FilmManualMatch {
        guard let target = target.text?.trimmingCharacters(in: .whitespacesAndNewlines), !target.isEmpty else {
            throw FilmPlaybackError(code: "INVALID_REQUEST")
        }
        var value = original ?? FilmManualMatch(targetID: target)
        value.value["targetId"] = .string(target)
        value.value["title"] = .string(name.text ?? "")
        value.value["name"] = .string(name.text ?? "")
        value.value["kind"] = .string(kind)
        value.value["id"] = .string(id)
        value.value["enabled"] = .bool(enabled); value.value["autoDisable"] = .bool(autoDisable)
        value.value["seasonHint"] = nil
        if let text = season.text, !text.isEmpty {
            guard let number = Int(text), number >= 0 else { throw FilmPlaybackError(code: "INVALID_REQUEST") }
            value.value["seasonHint"] = .number(Double(number))
        }
        if kind == "site" {
            guard let key = sourceKey.text, !key.isEmpty, let detail = detailID.text else {
                throw FilmPlaybackError(code: "INVALID_REQUEST")
            }
            value.value["sourceKey"] = .string(key)
            if numericDetailID {
                guard let number = Int64(detail), abs(Double(number)) <= 9_007_199_254_740_991 else {
                    throw FilmPlaybackError(code: "INVALID_REQUEST")
                }
                value.value["detailId"] = .number(Double(number))
            } else { value.value["detailId"] = .string(detail) }
            value.value["share"] = nil; value.value["provider"] = nil
        } else {
            guard let provider, let url = share.text, !url.isEmpty else { throw FilmPlaybackError(code: "INVALID_REQUEST") }
            value.value["provider"] = .string(provider.rawValue)
            value.value["share"] = .object(["url": .string(url), "password": .string(password.text ?? "")])
            value.value["sourceKey"] = nil; value.value["detailId"] = nil
        }
        return value
    }

    private func save() {
        guard task == nil else { return }
        view.endEditing(true)
        do {
            let value = try item()
            let owner = self.owner
            task = Task { [weak self] in
                defer { self?.task = nil; self?.update() }
                do { try await owner.saveManual(value); self?.navigationController?.popViewController(animated: true) }
                catch { self?.failure(error, retry: { [weak self] in self?.save() }) }
            }
            update()
        } catch { failure(error) }
    }

    private func remove() {
        confirm("删除人工匹配", message: original?.name) { [weak self] in
            guard let self, task == nil else { return }
            let owner = self.owner, id = self.id
            task = Task { [weak self] in
                defer { self?.task = nil; self?.update() }
                do { try await owner.removeManual(id: id); self?.navigationController?.popViewController(animated: true) }
                catch { self?.failure(error, retry: { [weak self] in self?.remove() }) }
            }
            update()
        }
    }
}
#endif
