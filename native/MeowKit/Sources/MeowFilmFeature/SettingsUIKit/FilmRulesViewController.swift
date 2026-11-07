#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmRulesViewController: FilmSettingsListController {
    let owner: FilmRulesDraft
    init(owner: FilmRulesDraft, themeProvider: any PageThemeProviding) {
        self.owner = owner; super.init(themeProvider: themeProvider); pageTitle = "魔法规则"
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.rules"
        observe(owner) { [weak self] in self?.render() }
        render()
    }
    override func pageDidBecomeActive() {
        runPageTask { [weak self] in
            guard let self else { return }
            do { try await owner.load(); render() }
            catch { failure(error, retry: { [weak self] in self?.pageDidBecomeActive() }) }
        }
    }
    override func render() {
        sections = [.init(title: "", rows: FilmRuleSection.allCases.map { section in
            .init(title: section.settingsTitle, id: "film.rules.\(section.rawValue)",
                  enabled: owner.draft != nil && !owner.busy, disclosure: true, action: { [weak self] in
                guard let self else { return }
                navigationController?.pushViewController(FilmRuleSectionController(owner: owner, section: section,
                    themeProvider: themeProvider), animated: true)
            }) })]
        if owner.draft == nil || owner.busy {
            sections.append(.init(title: "", rows: [.init(title: "加载中", id: "film.rules.busy")]))
        }
        super.render()
    }
}

@MainActor
final class FilmRuleSectionController: FilmSettingsListController {
    let owner: FilmRulesDraft
    let section: FilmRuleSection
    init(owner: FilmRulesDraft, section: FilmRuleSection, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.section = section
        super.init(themeProvider: themeProvider); pageTitle = section.settingsTitle
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        observe(owner) { [weak self] in self?.render() }; render()
    }
    override func viewWillAppear(_ animated: Bool) { super.viewWillAppear(animated); render() }
    override func render() {
        sections = section.settingsGroups.map { group in
            var rows = owner.rules(group).enumerated().map { index, rule in
                FilmSettingsRow(title: rule.pattern.isEmpty ? "空规则" : rule.pattern,
                    detail: group.isClean ? nil : "flags: \(rule.flags)  replace: \(rule.replace)",
                    id: "film.rules.\(group.rawValue).\(index)", enabled: !owner.busy, disclosure: true,
                    action: { [weak self] in self?.edit(group, index: index) })
            }
            if rows.isEmpty { rows.append(.init(title: "无规则", id: "film.rules.\(group.rawValue).empty")) }
            rows.append(.init(title: "添加规则", id: "film.rules.\(group.rawValue).add", enabled: !owner.busy,
                              action: { [weak self] in self?.edit(group, index: nil) }))
            return .init(title: group.title, rows: rows)
        }
        sections.append(.init(title: "", rows: [
            .init(title: "测试", id: "film.rules.test", enabled: !owner.busy, disclosure: true, action: { [weak self] in
                guard let self else { return }
                navigationController?.pushViewController(FilmRuleTestController(owner: owner, section: section,
                    themeProvider: themeProvider), animated: true)
            }),
            .init(title: "恢复默认", id: "film.rules.restore", enabled: !owner.busy, action: { [weak self] in
                guard let self else { return }
                confirm("恢复\(section.settingsTitle)默认规则？") { [weak self] in self?.restore() }
            }),
        ]))
        super.render()
    }
    private func edit(_ group: FilmRuleGroup, index: Int?) {
        navigationController?.pushViewController(FilmRuleEditorController(owner: owner, group: group,
            index: index, themeProvider: themeProvider), animated: true)
    }
    private func restore() {
        Task { [weak self] in
            guard let self else { return }
            do { try await owner.restore(section); render(); notice("已恢复默认") }
            catch { failure(error, retry: { [weak self] in self?.restore() }) }
        }
    }
}
#endif
