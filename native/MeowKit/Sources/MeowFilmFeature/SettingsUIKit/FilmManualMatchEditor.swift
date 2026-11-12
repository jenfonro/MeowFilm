#if os(iOS)
import UIKit
import MeowRuntime
import MeowUI

@MainActor
final class FilmManualMatchEditor: FilmSettingsListController {
    override var avoidsKeyboard: Bool { true }
    private let owner: FilmPlaybackSettingsModel
    private let original: FilmManualMatch?
    private let id: String
    private var kind: String
    private let target: UITextField, name: UITextField, sourceKey: UITextField, detailID: UITextField
    private let share: UITextField, password: UITextField, season: UITextField
    private var numericDetailID: Bool
    private var provider: FilmPanProvider?
    private var enabled: Bool
    private var autoDisable: Bool
    private var task: Task<Void, Never>?

    init(owner: FilmPlaybackSettingsModel, item: FilmManualMatch?, themeProvider: any PageThemeProviding) {
        self.owner = owner; original = item
        id = item?.id ?? UUID().uuidString.lowercased(); kind = item?.kind ?? "site"
        enabled = item?.enabled ?? true; autoDisable = item?.autoDisable ?? true
        target = filmTextField("影片身份", id: "film.manual.target", value: item?.targetID ?? "")
        name = filmTextField("名称", id: "film.manual.name", value: item?.name ?? "")
        sourceKey = filmTextField("来源身份", id: "film.manual.source", value: item?.value["sourceKey"]?.filmText ?? "")
        detailID = filmTextField("站点详情 ID", id: "film.manual.detail", value: item?.value["detailId"]?.filmScalar ?? "")
        share = filmTextField("分享地址", id: "film.manual.share",
                              value: item?.value["share"]?.filmObject?["url"]?.filmText ?? "")
        password = filmTextField("提取码", id: "film.manual.password",
                                 value: item?.value["share"]?.filmObject?["password"]?.filmText ?? "")
        season = filmTextField("季线索", id: "film.manual.season",
                               value: item?.value["seasonHint"]?.filmScalar ?? "")
        numericDetailID = item?.value["detailId"]?.filmNumber != nil
        provider = item?.value["provider"]?.filmText.flatMap(FilmPanProvider.init(rawValue:))
        super.init(themeProvider: themeProvider)
        pageTitle = item == nil ? "添加人工匹配" : "编辑人工匹配"
        share.textContentType = .none; password.textContentType = .none
        season.keyboardType = .numberPad
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        pageHeader.setTrailing([PageHeaderTextButton(title: "保存") { [weak self] in self?.save() }])
        update()
    }

    private func update() {
        var rows: [FilmSettingsRow] = [
            field("影片身份", target), field("名称", name),
            .init(title: "类型", detail: kind == "site" ? "站点" : "分享", id: "film.manual.kind",
                  enabled: task == nil, disclosure: true, action: { [weak self] in self?.chooseKind() }),
        ]
        if owner.playbackSnapshot?() != nil || owner.library?.history.isEmpty == false {
            rows.insert(.init(title: "选择影片", id: "film.manual.choose-target", enabled: task == nil,
                              disclosure: true, action: { [weak self] in self?.chooseTarget() }), at: 1)
        }
        if kind == "site" {
            rows += [field("来源身份", sourceKey)]
            if !siteChoices.isEmpty {
                rows.append(.init(title: "选择站点详情", id: "film.manual.choose-source", enabled: task == nil,
                                  disclosure: true, action: { [weak self] in self?.chooseSource() }))
            }
            rows += [
                field("站点详情 ID", detailID),
                .init(title: "详情 ID 类型", detail: numericDetailID ? "数字" : "文本",
                      id: "film.manual.detail-type", enabled: task == nil, disclosure: true,
                      action: { [weak self] in self?.chooseDetailType() }),
            ]
        } else {
            rows += [
                .init(title: "网盘", detail: provider?.title ?? "未选择", id: "film.manual.provider",
                      enabled: task == nil, disclosure: true, action: { [weak self] in self?.chooseProvider() }),
                field("分享地址", share), field("提取码", password),
            ]
        }
        rows += [field("季线索", season),
            filmSwitchRow("启用", id: "film.manual.enabled", isOn: enabled, enabled: task == nil) { [weak self] in self?.enabled = $0 },
            filmSwitchRow("连续失败自动停用", id: "film.manual.auto-disable", isOn: autoDisable, enabled: task == nil) { [weak self] in self?.autoDisable = $0 },
        ]
        if let original {
            rows.append(.init(title: "失败次数", detail: "\(original.failureCount)", id: "film.manual.failures"))
            rows.append(.init(title: "删除", id: "film.manual.remove", enabled: task == nil, destructive: true,
                              action: { [weak self] in self?.remove() }))
        }
        sections = [.init(title: "", rows: rows)]
        render()
    }

    private func field(_ title: String, _ field: UITextField) -> FilmSettingsRow {
        let label = UILabel()
        label.text = title; label.font = .preferredFont(forTextStyle: .subheadline)
        label.textColor = UIColor { UIColor(SettingsTheme(dark: $0.userInterfaceStyle == .dark).muted) }
        field.textColor = UIColor(theme.settings.ink); field.isEnabled = task == nil
        let stack = UIStackView(arrangedSubviews: [label, field])
        stack.axis = .vertical; stack.spacing = 8
        return .init(title: title, id: "", content: stack)
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
        let alert = UIAlertController(title: title, message: nil, preferredStyle: .actionSheet)
        for (name, action) in choices { alert.addAction(UIAlertAction(title: name, style: .default) { _ in action() }) }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.popoverPresentationController?.sourceView = table
        alert.popoverPresentationController?.sourceRect = CGRect(x: table.bounds.midX, y: table.bounds.midY, width: 1, height: 1)
        present(alert, animated: true)
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
        do {
            let value = try item()
            task = Task { [weak self] in
                guard let self else { return }
                defer { task = nil; update() }
                do { try await owner.saveManual(value); navigationController?.popViewController(animated: true) }
                catch { failure(error, retry: { [weak self] in self?.save() }) }
            }
            update()
        } catch { failure(error) }
    }

    private func remove() {
        confirm("删除人工匹配", message: original?.name) { [weak self] in
            guard let self, task == nil else { return }
            task = Task {
                defer { self.task = nil; self.update() }
                do { try await self.owner.removeManual(id: self.id); self.navigationController?.popViewController(animated: true) }
                catch { self.failure(error, retry: { [weak self] in self?.remove() }) }
            }
            update()
        }
    }
}
#endif
