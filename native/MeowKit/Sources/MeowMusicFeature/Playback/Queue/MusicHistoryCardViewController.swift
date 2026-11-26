#if os(iOS)
import UIKit
import MeowUI

/// Archived rows are display-only. Resume/delete use the service-issued
/// detail token, never a fabricated per-row playback reference.
@MainActor
final class MusicHistoryCardViewController: MusicPageViewController,
    UITableViewDelegate
{
    var isSelectedPage: (() -> Bool)?
    private let model: MusicViewModel
    private var summary: MusicPlayedPlaylist
    private let table = UITableView(frame: .zero, style: .plain)
    private let heading = UILabel(), source = UILabel(), count = UILabel(), emptyLabel = UILabel()
    private let resume = UIButton(type: .system), remove = UIButton(type: .system)
    private let fade = CAGradientLayer()
    private var detail: MusicHistoryDetail?
    private var displayed: MusicReferenceHistoryDisplayIdentity?
    private var command = UUID(), readRequest = UUID()
    private var reading = false
    private var needsRead = true
    private var error: String?
    private var dataSource: UITableViewDiffableDataSource<Int, String>!
    private var rowProjection = IndexedDiffableProjection<Int, String, MusicHistoryDisplayEntry>()
    private var missing = false

    init(model: MusicViewModel, summary: MusicPlayedPlaylist, appearance: MusicAppearancePreferencesStore) {
        self.model = model; self.summary = summary
        super.init(appearance: appearance)
    }

    override func loadView() { view = UIView() }

    override var usesPageHeader: Bool { false }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.layer.cornerRadius = 20
        view.layer.borderWidth = 1
        view.clipsToBounds = true
        view.isOpaque = true
        table.backgroundColor = .clear
        table.separatorStyle = .none
        table.rowHeight = 64
        table.contentInsetAdjustmentBehavior = .never
        table.isDirectionalLockEnabled = true
        table.delegate = self
        table.register(MusicQueueSongCell.self, forCellReuseIdentifier: MusicQueueSongCell.reuseIdentifier)
        dataSource = UITableViewDiffableDataSource(tableView: table) { [weak self] view, path, id in
            guard let self, let row = self.rowProjection[id] else { return nil }
            let cell = view.dequeueReusableCell(withIdentifier: MusicQueueSongCell.reuseIdentifier,
                                                for: path) as! MusicQueueSongCell
            self.configure(cell, row: row)
            return cell
        }
        heading.font = .systemFont(ofSize: 15)
        heading.lineBreakMode = .byTruncatingTail
        source.font = .systemFont(ofSize: 11)
        count.font = .systemFont(ofSize: 12)
        remove.setImage(PageIcon.image(.remove), for: .normal)
        remove.accessibilityLabel = "删除这份已播歌单"
        resume.addAction(UIAction { [weak self] _ in self?.continuePlaying() }, for: .touchUpInside)
        remove.addAction(UIAction { [weak self] _ in self?.confirmRemoval() }, for: .touchUpInside)
        for item in [table, source, heading, count, resume, remove] { view.addSubview(item) }
        view.layer.addSublayer(fade)
        observe(model.queueChanges, whenVisible: true) { [weak self] in self?.updateVisiblePlayback() }
        observe(model.historyChanges, whenVisible: true) { [weak self] in self?.reconcileSummary() }
        renderRows(); renderState()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let width = view.bounds.width
        let countWidth = count.sizeThatFits(CGSize(width: width, height: 24)).width
        let titleWidth = max(0, width - 68 - countWidth)
        source.frame = CGRect(x: 12, y: 12, width: max(0, width - 68), height: 18)
        heading.frame = CGRect(x: 12, y: 32, width: titleWidth, height: 24)
        count.frame = CGRect(x: heading.frame.maxX, y: 32, width: countWidth, height: 24)
        remove.frame = CGRect(x: width - 54, y: 16, width: 44, height: 44)
        resume.frame = CGRect(x: 10, y: 64, width: max(0, width - 20), height: 44)
        table.frame = CGRect(x: 10, y: 114, width: max(0, width - 20),
                             height: max(0, view.bounds.height - 114))
        CATransaction.begin(); CATransaction.setDisableActions(true)
        fade.frame = CGRect(x: 10, y: max(114, view.bounds.height - 24),
                            width: max(0, width - 20), height: 24)
        CATransaction.commit()
    }

    override func pageDidBecomeActive() { reconcileSummary() }
    override func pageDidBecomeVisible() { renderState() }

    func selectionDidChange() { if isPageActive { renderState() } }

    override func pageDidBecomeInactive() {
        invalidateDetail()
        needsRead = false
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        applyChromeTheme()
        updateVisiblePlayback(force: true)
    }

    func update(summary: MusicPlayedPlaylist) {
        let previous = identity
        self.summary = summary
        if previous != identity || displayed != nil && displayed != identity {
            invalidateDetail()
            needsRead = true
        }
        if isPageActive, needsRead { read() }
        if isViewLoaded { renderState() }
    }

    private func reconcileSummary() {
        guard !model.isClosed,
              let summary = model.playedPlaylists.first(where: { $0.id == self.summary.id }) else {
            invalidateDetail()
            missing = true; needsRead = false
            renderRows(); renderState()
            return
        }
        if missing { missing = false; needsRead = true }
        update(summary: summary)
    }

    private func configure(_ cell: MusicQueueSongCell, row: MusicHistoryDisplayEntry) {
        let active = row.displayIdentity?.matches(model.current?.displayIdentity) == true
        cell.row.configure(title: row.title, artist: row.artist, index: row.index, active: active,
                           favorite: nil, removable: false, reorderable: false, theme: theme)
        cell.accessibilityLabel = [row.title, row.artist].filter { !$0.isEmpty }.joined(separator: "，")
        cell.accessibilityTraits = active ? [.staticText, .selected] : .staticText
    }

    private var identity: MusicReferenceHistoryDisplayIdentity {
        .init(summary)
    }

    private var permitted: Bool {
        isPageActive && isSelectedPage?() == true && !model.isClosed &&
            displayed == identity && detail != nil && !reading && !missing
    }

    private func read(force: Bool = false) {
        guard isPageActive, !model.isClosed, !missing, !reading else { return }
        guard force || error == nil && (detail == nil || displayed != identity) else { return }
        let own = UUID(), expected = identity
        reading = true; needsRead = false; readRequest = own; command = UUID()
        error = nil
        clearErrorSource("read")
        renderState()
        let model = model
        runPageTask(key: "read") { [weak self, model] in
            defer {
                if let self, self.readRequest == own {
                    self.reading = false
                    self.renderState()
                }
            }
            do {
                let value = try await model.historyDetail(id: expected.id)
                guard !Task.isCancelled, let self, self.readRequest == own, self.isPageActive, !self.missing,
                      self.identity == expected else { return }
                self.detail = value; self.displayed = expected
                self.renderRows()
            } catch {
                if !Task.isCancelled, let self, self.readRequest == own, self.isPageActive, self.identity == expected {
                    self.error = MusicServiceError.sanitized(error).localizedDescription
                }
            }
        }
    }

    private func invalidateDetail() {
        command = UUID(); readRequest = UUID()
        cancelPageTask(key: "read"); reading = false
        detail = nil; displayed = nil; error = nil
        clearErrorSource("read")
        if isViewLoaded { renderRows() }
    }

    private func continuePlaying() {
        guard permitted, let detail else { return }
        let expectedCommand = command
        runPageTask { [weak self] in
            guard let self, self.permitted, self.command == expectedCommand,
                  self.detail?.summary.id == detail.summary.id else { return }
            await self.model.restoreHistory(detail: detail)
        }
    }

    private func confirmRemoval() {
        guard permitted, let detail else { return }
        let expectedCommand = command
        presentPageConfirmation(title: "删除这份已播歌单？", confirmTitle: "确认删除", destructive: true, onConfirm: { [weak self] in
            self?.runPageTask { [weak self] in
                guard let self, self.permitted, self.command == expectedCommand,
                      self.detail?.summary.id == detail.summary.id else { return }
                await self.model.removeHistory(detail: detail)
            }
        })
    }

    private func renderRows() {
        guard dataSource != nil else { return }
        let update = rowProjection.update(section: 0, items: detail?.entries ?? [], id: \.id)
        dataSource.applyIfNeeded(update, animatingDifferences: false)
    }

    private func updateVisiblePlayback(force: Bool = false) {
        guard dataSource != nil else { return }
        for path in table.indexPathsForVisibleRows ?? [] {
            guard let id = dataSource.itemIdentifier(for: path), let row = rowProjection[id],
                  let cell = table.cellForRow(at: path) as? MusicQueueSongCell else { continue }
            let active = row.displayIdentity?.matches(model.current?.displayIdentity) == true
            if force || cell.accessibilityTraits.contains(.selected) != active { configure(cell, row: row) }
        }
    }

    private func applyChromeTheme() {
        resume.configuration?.background.backgroundColor = UIColor(theme.surface)
        resume.configuration?.baseForegroundColor = UIColor(theme.ink)
        heading.textColor = UIColor(theme.ink)
        count.textColor = UIColor(theme.muted)
        source.textColor = UIColor(theme.muted)
        remove.tintColor = UIColor(theme.muted)
        emptyLabel.textColor = UIColor(theme.muted)
        view.backgroundColor = UIColor(theme.bottom)
        view.layer.borderColor = UIColor(theme.border).withAlphaComponent(0.45).cgColor
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        fade.colors = [UIColor(theme.bottom).withAlphaComponent(0).cgColor, UIColor(theme.bottom).cgColor]
        CATransaction.commit()
    }

    private func renderState() {
        guard isViewLoaded else { return }
        heading.text = missing ? nil : detail?.summary.title ?? summary.title
        count.text = missing ? nil : " · \(detail?.entries.count ?? summary.count) 首"
        source.text = missing ? nil : detail?.sourceLabel ?? "已播歌单"
        let selected = detail?.selectedIndex.flatMap { index in detail?.entries.first { $0.index == index }?.title }
        var configuration = UIButton.Configuration.plain()
        configuration.title = "继续播放" + (selected.map { "：" + $0 } ?? "")
        configuration.image = PageIcon.image(.miniResume, size: 18)
        configuration.imagePadding = 8
        configuration.contentInsets = .init(top: 6, leading: 8, bottom: 6, trailing: 8)
        configuration.background.backgroundColor = UIColor(theme.surface)
        configuration.background.cornerRadius = 12
        configuration.baseForegroundColor = UIColor(theme.ink)
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var attributes = $0; attributes.font = .systemFont(ofSize: 12); return attributes
        }
        resume.configuration = configuration
        resume.isHidden = detail == nil || missing
        remove.isHidden = detail == nil || missing
        resume.contentHorizontalAlignment = .leading
        resume.titleLabel?.lineBreakMode = .byTruncatingTail
        applyChromeTheme()
        emptyLabel.text = reading && detail == nil ? "正在读取已播歌单" : nil
        emptyLabel.textAlignment = .center
        table.backgroundView = emptyLabel.text == nil ? nil : emptyLabel
        view.setNeedsLayout()
        guard isSelectedPage?() == true else { return }
        let expected = identity
        syncErrorSource("read", message: error, token: readRequest, retry: { [weak self] in
            guard let self, self.identity == expected else { return }
            self.read(force: true)
        })
    }

}
#endif
