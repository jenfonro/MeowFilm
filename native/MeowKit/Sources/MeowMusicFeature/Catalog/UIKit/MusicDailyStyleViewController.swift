#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicDailyStyleViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    private let page: MusicPublicBrowseViewModel
    private let source: MusicRemoteQueueSource
    private let table = UITableView(frame: .zero, style: .insetGrouped)
    private var categories: [MusicDailyStyleCategory] = []
    private var category = ""
    private var choices: [String: Set<String>] = [:]
    private var working = false

    public init(
        page: MusicPublicBrowseViewModel,
        source: MusicRemoteQueueSource,
        appearance: MusicAppearancePreferencesStore
    ) {
        self.page = page
        self.source = source
        super.init(appearance: appearance)
        title = "选择推荐风格"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.dataSource = self
        table.delegate = self
        view.addSubview(table)
        table.pinEdges(to: view)
        navigationItem.leftBarButtonItem = UIBarButtonItem(
            barButtonSystemItem: .close,
            target: self,
            action: #selector(close)
        )
        navigationItem.rightBarButtonItem = UIBarButtonItem(
            title: "完成",
            style: .done,
            target: self,
            action: #selector(save)
        )
    }

    public override func pageDidBecomeActive() {
        if categories.isEmpty { load() }
    }

    public func numberOfSections(in tableView: UITableView) -> Int { 2 }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        section == 0 ? categories.count : selectedCategory?.tags.count ?? 0
    }

    public func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        section == 0 ? "分类" : selectedCategory?.name
    }

    public func tableView(
        _ tableView: UITableView,
        cellForRowAt indexPath: IndexPath
    ) -> UITableViewCell {
        let cell = UITableViewCell(style: .default, reuseIdentifier: nil)
        cell.backgroundColor = UIColor(theme.surface).withAlphaComponent(0.58)
        cell.textLabel?.textColor = UIColor(theme.ink)
        if indexPath.section == 0 {
            let value = categories[indexPath.row]
            cell.textLabel?.text = value.name
            cell.accessoryType = value.id == category ? .checkmark : .none
        } else {
            let value = selectedCategory!.tags[indexPath.row]
            cell.textLabel?.text = value.name
            cell.accessoryType = choices[category, default: []].contains(value.id)
                ? .checkmark : .none
            cell.accessibilityIdentifier = "music.dailyStyle.tag.\(value.id)"
        }
        return cell
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        if indexPath.section == 0 {
            category = categories[indexPath.row].id
            table.reloadData()
            return
        }
        guard let selectedCategory else { return }
        let tag = selectedCategory.tags[indexPath.row]
        var values = choices[category, default: []]
        if values.contains(tag.id) {
            values.remove(tag.id)
        } else if selectedCategory.maximum == 1 {
            values = [tag.id]
        } else if values.count < selectedCategory.maximum {
            values.insert(tag.id)
        } else {
            showError("最多选择 \(selectedCategory.maximum) 项")
            return
        }
        choices[category] = values
        table.reloadRows(at: [indexPath], with: .none)
    }

    private var selectedCategory: MusicDailyStyleCategory? {
        categories.first { $0.id == category }
    }

    private func load() {
        guard isPageActive else { return }
        guard let editor = source.dailyStyle else {
            showError(MusicServiceError.unsupported.localizedDescription)
            return
        }
        runPageTask { [weak self] in
            guard let self else { return }
            guard !Task.isCancelled, self.isPageActive, !self.working,
                  self.page.remoteSource?.id == self.source.id else { return }
            self.working = true
            defer {
                self.working = false
                if Task.isCancelled, self.isPageActive, self.categories.isEmpty { self.load() }
            }
            do {
                try await self.source.validate()
                let values = try await editor.categories()
                try await self.source.validate()
                guard !Task.isCancelled, self.isPageActive,
                      self.page.remoteSource?.id == self.source.id else { return }
                self.categories = values
                self.category = values.first {
                    $0.id == self.page.dailyStyle?.category
                }?.id ?? values.first?.id ?? ""
                if let value = self.page.dailyStyle {
                    self.choices[value.category] = Set(value.tags.map(\.id))
                }
                self.table.reloadData()
            } catch {
                if !Task.isCancelled { self.showError(MusicServiceError.sanitized(error).localizedDescription) }
            }
        }
    }

    @objc private func save() {
        guard isPageActive, let selectedCategory,
              page.remoteSource?.id == source.id else { return }
        let selected = choices[category, default: []]
        guard !selected.isEmpty else { return }
        let selection = MusicDailyStyleSelection(
            category: category,
            tags: selectedCategory.tags.filter { selected.contains($0.id) }
        )
        runPageTask { [weak self] in
            guard let self else { return }
            guard !Task.isCancelled, self.isPageActive, !self.working,
                  self.page.remoteSource?.id == self.source.id else { return }
            self.working = true
            defer { self.working = false }
            do {
                try await self.source.validate()
                guard !Task.isCancelled, self.page.remoteSource?.id == self.source.id else { return }
                _ = try await self.page.saveDailyStyle(selection)
                if !Task.isCancelled, self.isPageActive { self.dismiss(animated: true) }
            } catch {
                if !Task.isCancelled { self.showError(MusicServiceError.sanitized(error).localizedDescription) }
            }
        }
    }

    @objc private func close() {
        dismiss(animated: true)
    }

}

#endif
