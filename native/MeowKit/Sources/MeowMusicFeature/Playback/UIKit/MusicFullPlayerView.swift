#if os(iOS)
import UIKit
import Combine
import AVKit
import MeowUI

@MainActor
final class MusicFullPlayerView: MusicPlayerBackgroundView, UIScrollViewDelegate, UITableViewDelegate {
    var onClose: (() -> Void)?
    var onPresent: ((MusicPlaybackDestination) -> Void)? {
        get { controls.onPresent } set { controls.onPresent = newValue }
    }
    var onMore: (() -> Void)? { get { controls.onMore } set { controls.onMore = newValue } }
    var onMode: (() -> Void)? { get { controls.onMode } set { controls.onMode = newValue } }
    var onSources: (() -> Void)? { get { controls.onSources } set { controls.onSources = newValue } }
    var moreAnchor: UIView { controls.moreAnchor }
    var modeAnchor: UIView { controls.modeAnchor }
    private let content: MusicPlayerContentModel
    private let controls: MusicPlaybackControlsView
    private let close = UIButton(type: .system), lyricSwitch = UIButton(type: .system)
    private let route = AVRoutePickerView()
    private let sourceLabel = UILabel()
    private let recordScroll = UIScrollView()
    private let discs = (0..<3).map { _ in MusicRecordDiscView() }
    private let tonearm = MusicTonearmView()
    private let lyricTable = UITableView(frame: .zero, style: .plain)
    private let lyricStatus = UILabel()
    private let previewSeek = UIButton(type: .system), followLyrics = UIButton(type: .system)
    private let lyricBackdrop = UIImageView()
    private let lyricBlur = UIVisualEffectView(effect: UIBlurEffect(style: .regular))
    private let backdropMask = CAGradientLayer()
    private var records = MusicRecordTransition()
    private var lyricSelection = MusicReferenceLyricSelection()
    private var lines: [MusicLyricLine] = []
    private var linesByID: [Int: MusicLyricLine] = [:]
    private var displayedFence: MusicReferencePlayerFence?
    private var displayedActiveLine: Int?
    private var displayedSelectedLine: Int?
    private var followedLine: Int?
    private var applyingLyrics = false
    private var lyricExpanded = false
    private var rendering = false
    private var recordWidth: CGFloat = 0
    private var lyricTranslation: Bool?
    private var subscriptions: [AnyCancellable] = []
    private lazy var lyricDataSource = UITableViewDiffableDataSource<Int, Int>(tableView: lyricTable) {
        [weak self] table, index, id in
        let cell = table.dequeueReusableCell(withIdentifier: MusicLyricCell.reuseIdentifier,
                                             for: index) as! MusicLyricCell
        if let self, let line = self.linesByID[id] { self.configure(cell, line: line) }
        return cell
    }

    init(content: MusicPlayerContentModel) {
        self.content = content
        controls = MusicPlaybackControlsView(content: content, layout: .full)
        super.init(style: .full, palette: content.appearance.effectivePalette)
        accessibilityIdentifier = "music.player.page"
        for item in [recordScroll, tonearm, lyricBackdrop, lyricTable, controls,
                     close, lyricSwitch, route, sourceLabel, previewSeek, followLyrics] { addSubview(item) }
        close.setImage(UIImage(systemName: "chevron.left"), for: .normal)
        close.accessibilityLabel = "返回"
        close.accessibilityIdentifier = "music.player.close"
        close.addAction(UIAction { [weak self] _ in self?.onClose?() }, for: .touchUpInside)
        lyricSwitch.setImage(PageIcon.image(.translation), for: .normal)
        lyricSwitch.accessibilityLabel = "展开或收起歌词"
        lyricSwitch.addAction(UIAction { [weak self] _ in self?.toggleLyrics() }, for: .touchUpInside)
        route.prioritizesVideoDevices = false
        route.accessibilityLabel = "音频输出"
        sourceLabel.font = .systemFont(ofSize: 12)
        sourceLabel.textAlignment = .center
        recordScroll.isPagingEnabled = true
        recordScroll.showsHorizontalScrollIndicator = false
        recordScroll.delegate = self
        for disc in discs { recordScroll.addSubview(disc) }
        lyricTable.backgroundColor = .clear
        lyricTable.separatorStyle = .none
        lyricTable.rowHeight = UITableView.automaticDimension
        lyricTable.estimatedRowHeight = 64
        lyricTable.delegate = self
        lyricTable.register(MusicLyricCell.self, forCellReuseIdentifier: MusicLyricCell.reuseIdentifier)
        lyricTable.accessibilityIdentifier = "music.player.lyrics"
        lyricStatus.textAlignment = .center
        lyricBackdrop.contentMode = .scaleAspectFill
        lyricBackdrop.clipsToBounds = true
        lyricBackdrop.alpha = 0.24
        lyricBackdrop.isUserInteractionEnabled = false
        lyricBackdrop.accessibilityElementsHidden = true
        lyricBackdrop.addSubview(lyricBlur)
        backdropMask.colors = [UIColor.clear.cgColor, UIColor.black.cgColor,
                               UIColor.black.cgColor, UIColor.clear.cgColor]
        backdropMask.locations = [0, 0.16, 0.82, 1]
        lyricBackdrop.layer.mask = backdropMask
        previewSeek.setTitle("播放此句", for: .normal)
        previewSeek.addAction(UIAction { [weak self] _ in self?.confirmLyric() }, for: .touchUpInside)
        followLyrics.setTitle("跟随播放", for: .normal)
        followLyrics.addAction(UIAction { [weak self] _ in
            self?.lyricSelection.cancel()
            self?.followedLine = nil
            self?.updateLyricClock()
        }, for: .touchUpInside)
        subscriptions = [
            content.playbackChanges.sink { [weak self] in self?.reconcilePlayback() },
            content.queueChanges.sink { [weak self] in self?.synchronizeRecords(); self?.updateSource() },
            content.metadataChanges.sink { [weak self] in self?.updateLyrics() },
            content.artworkChanges.sink { [weak self] in self?.updateArtwork() },
            content.appearanceChanges.sink { [weak self] in self?.updateAppearance() },
            content.clockChanges.sink { [weak self] in
                guard let self, self.rendering else { return }
                if self.lyricExpanded { self.updateLyricClock() }
                self.updateRotation()
            }
        ]
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (view: MusicFullPlayerView, _: UITraitCollection) in
            view.refreshVisibleLyrics()
            view.setNeedsLayout()
        }
        reconcilePlayback()
        updateLyrics()
        updateArtwork()
        updateAppearance()
        updateVisibility()
    }

    func prioritizeEdgeGesture(_ edge: UIScreenEdgePanGestureRecognizer) {
        recordScroll.panGestureRecognizer.require(toFail: edge)
        lyricTable.panGestureRecognizer.require(toFail: edge)
    }

    func setRendering(_ rendering: Bool) {
        guard self.rendering != rendering else { return }
        self.rendering = rendering
        controls.setRendering(rendering)
        updateLyricClock()
        updateRotation()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let width = bounds.width, top = safeAreaInsets.top, bottom = bounds.height - safeAreaInsets.bottom
        close.frame = CGRect(x: 12, y: top + 4, width: 44, height: 44)
        lyricSwitch.frame = CGRect(x: width - 60, y: top + 4, width: 44, height: 44)
        route.frame = CGRect(x: width / 2 - 22, y: top + 4, width: 44, height: 44)
        sourceLabel.frame = CGRect(x: 64, y: top + 48, width: max(0, width - 128), height: 20)
        controls.frame = CGRect(x: 0, y: bottom - controls.fullHeight, width: width, height: controls.fullHeight)
        let recordTop = top + 76, available = max(0, controls.frame.minY - recordTop - 12)
        recordScroll.frame = CGRect(x: 0, y: recordTop, width: width, height: available)
        recordScroll.contentSize = CGSize(width: width * 3, height: available)
        let size = max(0, min(width * 0.84, available * 0.92))
        for index in discs.indices {
            let page = index == records.center ? 1 : index == records.neighbor(.previous) ? 0 : 2
            discs[index].frame = CGRect(x: CGFloat(page) * width + (width - size) / 2,
                                       y: (available - size) / 2, width: size, height: size)
        }
        let arm = CGRect(x: width * 0.57, y: recordTop + 3, width: size * 0.3, height: size * 0.65)
        tonearm.bounds = CGRect(origin: .zero, size: arm.size)
        tonearm.center = CGPoint(x: arm.minX + arm.width * tonearm.layer.anchorPoint.x,
                                y: arm.minY + arm.height * tonearm.layer.anchorPoint.y)
        if recordWidth != width {
            recordWidth = width
            records.cancel()
            synchronizeRecords()
            recordScroll.setContentOffset(CGPoint(x: width, y: 0), animated: false)
        }
        lyricTable.frame = recordScroll.frame.insetBy(dx: 20, dy: 0)
        lyricTable.contentInset = UIEdgeInsets(top: max(0, available / 2 - 32), left: 0,
                                              bottom: max(0, available / 2 - 32), right: 0)
        lyricBackdrop.frame = lyricTable.frame
        lyricBlur.frame = lyricBackdrop.bounds
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        backdropMask.frame = lyricBackdrop.bounds
        CATransaction.commit()
        previewSeek.frame = CGRect(x: width - 124, y: lyricTable.frame.midY - 22, width: 104, height: 44)
        followLyrics.frame = CGRect(x: width - 124, y: lyricTable.frame.maxY - 48, width: 104, height: 44)
        controls.layoutIfNeeded()
    }

    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        guard let id = lyricDataSource.itemIdentifier(for: indexPath) else { return }
        lyricSelection.begin(content.fence, selected: id)
        updateLyricClock()
    }

    func tableView(_ tableView: UITableView, didEndDisplaying cell: UITableViewCell, forRowAt indexPath: IndexPath) {
        (cell as? MusicLyricCell)?.updateTime(content.clock.position, playing: false, rate: content.clock.rate, active: false)
    }

    func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        if scrollView === lyricTable {
            lyricSelection.begin(content.fence, selected: centeredLyric(), dragging: true,
                                 playbackLine: content.model.currentLyricIndex)
            updateLyricClock()
        } else if scrollView === recordScroll { _ = records.begin(content.fence) }
    }

    func scrollViewDidScroll(_ scrollView: UIScrollView) {
        guard scrollView === lyricTable, lyricSelection.isDragging, let id = centeredLyric() else { return }
        lyricSelection.preview(id)
        updateLyricClock()
    }

    func scrollViewWillEndDragging(_ scrollView: UIScrollView, withVelocity velocity: CGPoint,
                                  targetContentOffset: UnsafeMutablePointer<CGPoint>) {
        guard scrollView === lyricTable else { return }
        let point = CGPoint(x: 1, y: targetContentOffset.pointee.y + lyricTable.bounds.height / 2)
        guard let index = lyricTable.indexPathForRow(at: point) else { return }
        targetContentOffset.pointee.y = lyricTable.rectForRow(at: index).midY - lyricTable.bounds.height / 2
    }

    func scrollViewDidEndDecelerating(_ scrollView: UIScrollView) { finishScroll(scrollView) }
    func scrollViewDidEndDragging(_ scrollView: UIScrollView, willDecelerate decelerate: Bool) {
        if !decelerate { finishScroll(scrollView) }
    }
    func scrollViewDidEndScrollingAnimation(_ scrollView: UIScrollView) {
        if scrollView === recordScroll, records.phase == .returning, let token = records.token {
            _ = records.finish(token, currentID: content.model.current?.id)
            synchronizeRecords()
        }
    }

    private var activeLine: Int? {
        content.model.currentLyricIndex.flatMap {
            content.model.lyricLines.indices.contains($0) ? content.model.lyricLines[$0].id : nil
        }
    }

    private func configure(_ cell: MusicLyricCell, line: MusicLyricLine) {
        cell.configure(line: line, selected: line.id == lyricSelection.selected, active: line.id == activeLine,
                       position: content.clock.position, rate: content.clock.rate,
                       playing: rendering && lyricExpanded && content.clock.playing,
                       preferences: content.appearance.preferences, theme: content.controlTheme)
    }

    private func updateLyrics() {
        let next = content.model.lyricLines
        lyricStatus.text = next.isEmpty ? (content.model.lyricState == "disabled" ? "歌词已关闭" :
            content.model.lyricState == "loading" ? "歌词加载中" : "暂无歌词") : nil
        lyricTable.backgroundView = lyricStatus.text == nil ? nil : lyricStatus
        guard next != lines else { return }
        let previous = linesByID
        lines = next
        linesByID = Dictionary(uniqueKeysWithValues: next.map { ($0.id, $0) })
        var snapshot = NSDiffableDataSourceSnapshot<Int, Int>()
        snapshot.appendSections([0])
        snapshot.appendItems(next.map(\.id))
        snapshot.reconfigureItems(next.filter { previous[$0.id] != nil && previous[$0.id] != $0 }.map(\.id))
        applyingLyrics = true
        followedLine = nil
        lyricDataSource.apply(snapshot, animatingDifferences: false) { [weak self] in
            self?.applyingLyrics = false
            self?.updateLyricClock()
        }
    }

    private func updateLyricClock() {
        updatePreviewControls()
        guard !applyingLyrics else { return }
        let active = activeLine, selected = lyricSelection.selected, clock = content.clock
        let changed = Set([displayedActiveLine, active, displayedSelectedLine, selected].compactMap { $0 })
        let changedState = active != displayedActiveLine || selected != displayedSelectedLine
        var resized = false
        for index in lyricTable.indexPathsForVisibleRows ?? [] {
            guard let id = lyricDataSource.itemIdentifier(for: index),
                  let cell = lyricTable.cellForRow(at: index) as? MusicLyricCell else { continue }
            if changedState && changed.contains(id) {
                resized = cell.updateState(selected: id == selected, active: id == active, position: clock.position,
                    playing: rendering && lyricExpanded && clock.playing, rate: clock.rate) || resized
            } else if id == active {
                cell.updateTime(clock.position, playing: rendering && lyricExpanded && clock.playing,
                                rate: clock.rate, active: true)
            }
        }
        displayedActiveLine = active
        displayedSelectedLine = selected
        if resized { lyricTable.performBatchUpdates(nil) }
        guard rendering, lyricExpanded, !lyricSelection.isManual, !lyricTable.isDragging, !lyricTable.isDecelerating,
              let active, followedLine != active, let index = lyricDataSource.indexPath(for: active) else { return }
        followedLine = active
        lyricTable.scrollToRow(at: index, at: .middle, animated: !UIAccessibility.isReduceMotionEnabled)
    }

    private func refreshVisibleLyrics() {
        for index in lyricTable.indexPathsForVisibleRows ?? [] {
            guard let id = lyricDataSource.itemIdentifier(for: index), let line = linesByID[id],
                  let cell = lyricTable.cellForRow(at: index) as? MusicLyricCell else { continue }
            configure(cell, line: line)
        }
        if !applyingLyrics { lyricTable.performBatchUpdates(nil) }
    }

    private func updatePreviewControls() {
        previewSeek.isHidden = !lyricExpanded || !lyricSelection.isManual
        followLyrics.isHidden = previewSeek.isHidden
        previewSeek.isEnabled = lyricSelection.target(lines: content.model.lyricLines, current: content.fence,
            permitted: content.isAvailable, duration: content.clock.duration) != nil
    }

    private func centeredLyric() -> Int? {
        let point = CGPoint(x: 1, y: lyricTable.contentOffset.y + lyricTable.bounds.height / 2)
        return lyricTable.indexPathForRow(at: point).flatMap { lyricDataSource.itemIdentifier(for: $0) }
    }

    private func confirmLyric() {
        guard let target = lyricSelection.target(lines: content.model.lyricLines, current: content.fence,
            permitted: content.isAvailable, duration: content.clock.duration) else { return }
        _ = lyricSelection.confirm(accepted: content.seek(to: target, expected: content.fence),
                                   current: content.fence, permitted: content.isAvailable)
        updateLyricClock()
    }

    private func toggleLyrics() {
        lyricExpanded.toggle()
        followedLine = nil
        updateVisibility()
        updateLyricClock()
        updateRotation()
    }

    private func updateVisibility() {
        recordScroll.isHidden = lyricExpanded
        tonearm.isHidden = lyricExpanded
        lyricTable.isHidden = !lyricExpanded
        lyricBackdrop.isHidden = !content.appearance.preferences.showsLyricBackground(expanded: lyricExpanded)
        updatePreviewControls()
    }

    private func reconcilePlayback() {
        if displayedFence != content.fence {
            displayedFence = content.fence
            lyricSelection.cancel()
            followedLine = nil
            updateLyricClock()
        }
        if !records.accepts(content.fence) {
            records.cancel()
            recordScroll.setContentOffset(CGPoint(x: recordScroll.bounds.width, y: 0), animated: false)
        }
        synchronizeRecords()
        updateSource()
        updateRotation()
    }

    private func synchronizeRecords() {
        records.synchronize(current: content.model.current, previous: content.model.recordPreview(.previous),
                            next: content.model.recordPreview(.next))
        let theme = PageTheme(palette: content.appearance.effectivePalette, dark: false)
        for slot in records.slots { discs[slot.id].configure(theme: theme, image: content.artwork(for: slot.entry)) }
        recordScroll.isScrollEnabled = content.model.canNavigate && !records.busy
        updateRotation()
    }

    private func finishScroll(_ scrollView: UIScrollView) {
        if scrollView === lyricTable {
            if let id = centeredLyric() { lyricSelection.end(id) }
            updateLyricClock()
            return
        }
        guard scrollView === recordScroll, recordScroll.bounds.width > 0, records.accepts(content.fence),
              !records.busy else { return }
        let page = Int((recordScroll.contentOffset.x / recordScroll.bounds.width).rounded())
        if page == 1 { records.cancel(); synchronizeRecords(); return }
        let direction: MusicDirection = page < 1 ? .previous : .next
        guard let token = records.request(direction, fence: content.fence) else { return }
        recordScroll.isScrollEnabled = false
        let accepted = content.model.requestNavigation(direction, onSelection: { [weak self] entry in
            guard let self else { return }
            if self.records.selected(entry, token: token, current: self.content.fence) { self.synchronizeRecords() }
        }, onCompletion: { [weak self] in
            guard let self, self.records.token == token else { return }
            if self.records.finish(token, currentID: self.content.model.current?.id) {
                self.synchronizeRecords()
                self.setNeedsLayout()
                self.layoutIfNeeded()
                self.recordScroll.setContentOffset(CGPoint(x: self.recordScroll.bounds.width, y: 0), animated: false)
            } else if !self.records.accepts(self.content.fence) {
                self.records.cancel()
                self.synchronizeRecords()
                self.recordScroll.setContentOffset(CGPoint(x: self.recordScroll.bounds.width, y: 0), animated: false)
            } else { self.returnRecordToCenter() }
        })
        if !accepted { returnRecordToCenter() }
    }

    private func returnRecordToCenter() {
        guard let token = records.returnToOrigin() else { return }
        let point = CGPoint(x: recordScroll.bounds.width, y: 0)
        let animated = rendering && !UIAccessibility.isReduceMotionEnabled && recordScroll.contentOffset != point
        recordScroll.setContentOffset(point, animated: animated)
        if !animated { _ = records.finish(token, currentID: content.model.current?.id); synchronizeRecords() }
    }

    private func updateArtwork() {
        lyricBackdrop.image = content.artwork
        synchronizeRecords()
    }

    private func updateAppearance() {
        apply(palette: content.appearance.effectivePalette,
              animated: rendering && window != nil && content.appearance.animatesPaletteChanges)
        let theme = content.controlTheme
        for button in [close, lyricSwitch, previewSeek, followLyrics] { button.tintColor = UIColor(theme.ink) }
        sourceLabel.textColor = UIColor(theme.muted)
        lyricStatus.textColor = UIColor(theme.muted)
        route.tintColor = UIColor(theme.ink)
        route.activeTintColor = UIColor(theme.accent)
        updateVisibility()
        synchronizeRecords()
        if lyricTranslation != content.appearance.preferences.lyricTranslation {
            lyricTranslation = content.appearance.preferences.lyricTranslation
            refreshVisibleLyrics()
        }
    }

    private func updateSource() {
        let model = content.model
        sourceLabel.text = MusicReferencePlayerLabels.source(
            fm: model.isFMActive ? MusicFMModeChoice.title(for: model.activeHomeFM) : nil,
            context: model.current?.sourceTitle,
            collection: model.collections.first { $0.id == model.state.sourceCollectionID }?.title,
            track: model.displayTrack?.title ?? "")
    }

    private func updateRotation() {
        let current = records.phase == .settling
            ? records.direction.map { records.neighbor($0) } ?? records.center : records.center
        for index in discs.indices {
            discs[index].setRotating(index == current && rendering && !lyricExpanded &&
                                    content.clock.playing && !UIAccessibility.isReduceMotionEnabled)
        }
        tonearm.setPlaying(content.clock.playing)
    }
}
#endif
