#if os(iOS)
import UIKit
import MeowUI

/// Archived rows are display-only. Resume/delete use the service-issued
/// detail token, never a fabricated per-row playback reference.
@MainActor
final class MusicHistoryCardViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    var isSelectedPage: (() -> Bool)?
    private let model: MusicViewModel
    private var summary: MusicPlayedPlaylist
    private let table = UITableView(frame: .zero, style: .plain)
    private let heading = UILabel(), source = UILabel(), status = UILabel()
    private let resume = UIButton(type: .system), remove = UIButton(type: .system)
    private let retry = UIButton(type: .system)
    private var detail: MusicHistoryDetail?
    private var displayed: MusicReferenceHistoryDisplayIdentity?
    private var command = UUID(), readRequest = UUID()
    private var reading = false
    private var needsRead = false
    private var error: String?

    init(model: MusicViewModel, summary: MusicPlayedPlaylist, appearance: MusicAppearancePreferencesStore) {
        self.model = model; self.summary = summary
        super.init(appearance: appearance)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.layer.cornerRadius = 20
        table.backgroundColor = .clear
        table.separatorStyle = .none
        table.dataSource = self; table.delegate = self
        table.register(UITableViewCell.self, forCellReuseIdentifier: "History")
        heading.font = .systemFont(ofSize: 15, weight: .semibold)
        source.font = .systemFont(ofSize: 11)
        status.font = .systemFont(ofSize: 13); status.numberOfLines = 0
        remove.setImage(PageIcon.image(.remove), for: .normal)
        remove.accessibilityLabel = "删除这份已播歌单"
        resume.addAction(UIAction { [weak self] _ in self?.continuePlaying() }, for: .touchUpInside)
        remove.addAction(UIAction { [weak self] _ in self?.confirmRemoval() }, for: .touchUpInside)
        retry.setTitle("重试", for: .normal)
        retry.addAction(UIAction { [weak self] _ in self?.read() }, for: .touchUpInside)
        for item in [table, source, heading, status, retry, resume, remove] { view.addSubview(item) }
        observe(model) { [weak self] in
            guard let self else { return }
            self.update(summary: self.summary)
            self.table.reloadData()
        }
        render()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let width = view.bounds.width
        source.frame = CGRect(x: 16, y: 12, width: width - 80, height: 18)
        heading.frame = CGRect(x: 16, y: 32, width: width - 80, height: 24)
        remove.frame = CGRect(x: width - 60, y: 16, width: 44, height: 44)
        resume.frame = CGRect(x: 16, y: 64, width: width - 32, height: 44)
        let height: CGFloat = error == nil ? 0 : 60
        status.frame = CGRect(x: 16, y: 108, width: width - 110, height: height)
        retry.frame = CGRect(x: width - 92, y: 116, width: 76, height: 44)
        table.frame = CGRect(x: 0, y: 112 + height, width: width,
                             height: max(0, view.bounds.height - 112 - height))
    }

    override func pageDidBecomeActive() { update(summary: summary); read() }

    override func pageDidBecomeInactive() {
        command = UUID()
        readRequest = UUID()
        needsRead = detail == nil || displayed != identity
    }

    override func applyTheme() {
        super.applyTheme()
        if isViewLoaded { render() }
    }

    func update(summary: MusicPlayedPlaylist) {
        self.summary = summary
        if displayed != nil, displayed != identity {
            command = UUID(); readRequest = UUID(); detail = nil; displayed = nil
            needsRead = true
        }
        if isPageActive, needsRead { read() }
        if isViewLoaded { render() }
    }

    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        detail?.entries.count ?? 0
    }

    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let row = detail!.entries[indexPath.row]
        let active = row.displayIdentity?.matches(model.current?.displayIdentity) == true
        let cell = tableView.dequeueReusableCell(withIdentifier: "History", for: indexPath)
        var content = cell.defaultContentConfiguration()
        content.text = "\(row.index + 1). \(row.title)"
        content.secondaryText = row.artist
        content.textProperties.color = UIColor(active ? theme.accent : theme.ink)
        content.secondaryTextProperties.color = UIColor(theme.muted)
        cell.contentConfiguration = content
        cell.backgroundColor = active ? UIColor(theme.accent).withAlphaComponent(0.06) : .clear
        cell.selectionStyle = .none
        cell.accessibilityTraits = active ? [.staticText, .selected] : .staticText
        return cell
    }

    private var identity: MusicReferenceHistoryDisplayIdentity {
        .init(summary, access: model.accountAccessGeneration)
    }

    private var permitted: Bool {
        isPageActive && isSelectedPage?() == true && model.isAvailable && !model.isClosed &&
            displayed == identity && detail != nil && !reading
    }

    private func read() {
        guard isPageActive, model.isAvailable, !model.isClosed else { return }
        guard !reading else { needsRead = true; return }
        guard detail == nil || displayed != identity || error != nil else { return }
        let own = UUID(), expected = identity
        runPageTask { [weak self] in
            guard let self else { return }
            guard !Task.isCancelled, self.isPageActive, !self.reading,
                  self.model.isAvailable, !self.model.isClosed,
                  self.identity == expected else { return }
            self.reading = true
            self.needsRead = false
            self.readRequest = own
            self.command = UUID()
            self.error = nil
            self.render()
            defer {
                self.reading = false
                self.render()
                if self.needsRead, self.isPageActive { self.needsRead = false; self.read() }
            }
            do {
                let value = try await self.model.historyDetail(id: expected.id)
                guard !Task.isCancelled, self.readRequest == own, self.isPageActive,
                      self.identity == expected else { return }
                self.detail = value; self.displayed = expected
            } catch {
                if !Task.isCancelled, self.readRequest == own, self.isPageActive, self.identity == expected {
                    self.error = MusicServiceError.sanitized(error).localizedDescription
                }
            }
        }
    }

    private func intent(_ detail: MusicHistoryDetail) -> MusicReferenceHistoryIntent {
        .init(access: model.accountAccessGeneration, request: command, token: detail.actionToken)
    }

    private func admits(_ intent: MusicReferenceHistoryIntent) -> Bool {
        intent.admits(access: model.accountAccessGeneration, request: command,
                      token: detail?.actionToken, permitted: permitted)
    }

    private func continuePlaying() {
        guard permitted, let detail else { return }
        let captured = intent(detail)
        runPageTask { [weak self] in
            guard let self, self.admits(captured) else { return }
            await self.model.restoreHistory(detail: detail)
        }
    }

    private func confirmRemoval() {
        guard permitted, let detail else { return }
        let captured = intent(detail)
        let alert = UIAlertController(title: "删除这份已播歌单？", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "确认删除", style: .destructive) { [weak self] _ in
            self?.runPageTask {
                guard let self, self.admits(captured) else { return }
                await self.model.removeHistory(detail: detail)
            }
        })
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        present(alert, animated: true)
    }

    private func render() {
        guard isViewLoaded else { return }
        heading.text = "\(detail?.summary.title ?? summary.title) · \(detail?.entries.count ?? summary.count) 首"
        source.text = detail?.sourceLabel ?? "已播歌单"
        let selected = detail?.selectedIndex.flatMap { index in detail?.entries.first { $0.index == index }?.title }
        resume.setTitle("继续播放：" + (selected ?? "播放列表"), for: .normal)
        heading.textColor = UIColor(theme.ink)
        source.textColor = UIColor(theme.muted); status.textColor = UIColor(theme.muted)
        for button in [resume, remove, retry] { button.tintColor = UIColor(theme.ink) }
        status.text = error
        retry.isHidden = error == nil
        table.reloadData()
        let label = UILabel()
        label.text = reading && detail == nil ? "正在读取已播歌单" : nil
        label.textAlignment = .center; label.textColor = UIColor(theme.muted)
        table.backgroundView = label.text == nil ? nil : label
        view.setNeedsLayout()
    }
}
#endif
