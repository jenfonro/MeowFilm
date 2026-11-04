#if os(iOS)
import UIKit
import Combine
import AVKit
import CoreImage
import MeowUI

@MainActor
final class MusicFullPlayerView: MusicPlayerBackgroundView, UIScrollViewDelegate, UITableViewDelegate,
    UIGestureRecognizerDelegate
{
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
    private lazy var close: PageHeaderButton = {
        let button = PageHeaderButton(image: PageIcon.image(.playerDown, size: 25),
                                      accessibilityLabel: "收起播放器") { [weak self] in self?.onClose?() }
        button.accessibilityIdentifier = "music.player.close"
        return button
    }()
    private let route = AVRoutePickerView()
    private let sourceLabel = UILabel()
    private let recordScroll = UIScrollView()
    private let discs = (0..<3).map { _ in MusicRecordDiscView() }
    private let tonearm = MusicTonearmView()
    private let lyricPreview = UIButton(type: .custom)
    private let lyricPreviewLabels = (0..<3).map { _ in UILabel() }
    private let lyricTable = UITableView(frame: .zero, style: .plain)
    private let lyricStatus = UILabel()
    private let previewTime = UILabel()
    private let previewRule = UIView()
    private let previewSeek = UIButton(type: .system)
    private let followLyrics = UIButton(type: .system)
    private let translationToggle = UIButton(type: .system)
    private let backgroundToggle = UIButton(type: .system)
    private let lyricBackdrop = UIImageView()
    private let backdropMask = CAGradientLayer()
    private let lyricMask = CAGradientLayer()
    private let blurContext = CIContext(options: [.cacheIntermediates: false])
    private lazy var contentTap: UITapGestureRecognizer = {
        let value = UITapGestureRecognizer(target: self, action: #selector(contentTapped))
        value.cancelsTouchesInView = false
        value.delegate = self
        return value
    }()

    private var records = MusicRecordTransition()
    private var lyricSelection = MusicReferenceLyricSelection()
    private var lines: [MusicLyricLine] = []
    private var linesByID: [Int: MusicLyricLine] = [:]
    private var displayedFence: MusicReferencePlayerFence?
    private var followedLine: Int?
    private var applyingLyrics = false
    private var lyricExpanded = false
    private var rendering = false
    private var recordWidth: CGFloat = 0
    private var lyricTranslation: Bool?
    private var lyricTheme: PageTheme?
    private var lyricViewportSize = CGSize.zero
    private var previewWidth: CGFloat = 0
    private var previewIdentity: [Int?] = []
    private var markerDragging = false
    private var contentFrame = CGRect.zero
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
        for item in [lyricBackdrop, recordScroll, tonearm, lyricPreview, lyricTable, controls,
                     translationToggle, backgroundToggle, close, route, sourceLabel,
                     previewRule, previewTime, previewSeek, followLyrics] {
            addSubview(item)
        }

        route.prioritizesVideoDevices = false
        route.accessibilityLabel = "音频输出"
        route.accessibilityIdentifier = "music.player.audioOutput"
        sourceLabel.font = .systemFont(ofSize: 14, weight: .medium)
        sourceLabel.textAlignment = .center
        sourceLabel.lineBreakMode = .byTruncatingTail

        recordScroll.isPagingEnabled = true
        recordScroll.clipsToBounds = false
        recordScroll.showsHorizontalScrollIndicator = false
        recordScroll.showsVerticalScrollIndicator = false
        recordScroll.delegate = self
        for disc in discs { recordScroll.addSubview(disc) }

        for label in lyricPreviewLabels {
            label.textAlignment = .center
            label.lineBreakMode = .byTruncatingTail
            label.numberOfLines = 1
            lyricPreview.addSubview(label)
        }
        lyricPreview.accessibilityLabel = "展开完整歌词"
        lyricPreview.accessibilityIdentifier = "music.player.lyricToggle"
        lyricPreview.addAction(UIAction { [weak self] _ in self?.toggleLyrics() }, for: .touchUpInside)

        lyricTable.backgroundColor = .clear
        lyricTable.separatorStyle = .none
        lyricTable.rowHeight = UITableView.automaticDimension
        lyricTable.estimatedRowHeight = 46
        lyricTable.delegate = self
        lyricTable.allowsSelection = false
        lyricTable.bounces = false
        lyricTable.isDirectionalLockEnabled = true
        lyricTable.decelerationRate = .fast
        lyricTable.showsVerticalScrollIndicator = false
        lyricTable.contentInsetAdjustmentBehavior = .never
        lyricTable.register(MusicLyricCell.self, forCellReuseIdentifier: MusicLyricCell.reuseIdentifier)
        lyricTable.accessibilityIdentifier = "music.player.lyrics"
        lyricTable.layer.mask = lyricMask
        lyricStatus.textAlignment = .center
        lyricStatus.isAccessibilityElement = true

        lyricBackdrop.contentMode = .scaleAspectFill
        lyricBackdrop.clipsToBounds = true
        lyricBackdrop.alpha = 0.24
        lyricBackdrop.isUserInteractionEnabled = false
        lyricBackdrop.accessibilityElementsHidden = true
        backdropMask.colors = [UIColor.clear.cgColor, UIColor.black.cgColor,
                               UIColor.black.cgColor, UIColor.clear.cgColor]
        backdropMask.locations = [0, 0.16, 0.82, 1]
        lyricBackdrop.layer.mask = backdropMask
        lyricMask.colors = [UIColor.clear.cgColor, UIColor.black.cgColor,
                            UIColor.black.cgColor, UIColor.clear.cgColor]
        lyricMask.locations = [0, 0.18, 0.82, 1]

        previewTime.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular)
        previewTime.textAlignment = .center
        previewTime.accessibilityElementsHidden = true
        previewRule.isUserInteractionEnabled = false
        previewRule.accessibilityElementsHidden = true
        previewSeek.setImage(PageIcon.image(.lyricPlay, size: 20), for: .normal)
        previewSeek.accessibilityIdentifier = "music.player.lyricSeek"
        previewSeek.addAction(UIAction { [weak self] _ in self?.confirmLyric() }, for: .touchUpInside)
        followLyrics.setImage(UIImage(systemName: "scope"), for: .normal)
        followLyrics.accessibilityLabel = "跟随播放"
        followLyrics.accessibilityIdentifier = "music.player.lyricFollow"
        followLyrics.addAction(UIAction { [weak self] _ in self?.resumeLyricFollowing() }, for: .touchUpInside)
        translationToggle.setImage(PageIcon.image(.translation, size: 22), for: .normal)
        translationToggle.accessibilityIdentifier = "music.player.lyricTranslation"
        translationToggle.addAction(UIAction { [weak content] _ in
            guard let content else { return }
            content.appearance.set(\.lyricTranslation, !content.appearance.preferences.lyricTranslation)
        }, for: .touchUpInside)
        backgroundToggle.setImage(PageIcon.image(.image, size: 21), for: .normal)
        backgroundToggle.accessibilityIdentifier = "music.player.lyricBackground"
        backgroundToggle.addAction(UIAction { [weak content] _ in
            guard let content else { return }
            content.appearance.set(\.lyricBackground, !content.appearance.preferences.lyricBackground)
        }, for: .touchUpInside)

        addGestureRecognizer(contentTap)
        updateAccessibilityAction()
        subscriptions = [
            content.playbackChanges.sink { [weak self] in self?.reconcilePlayback() },
            content.queueChanges.sink { [weak self] in self?.synchronizeRecords(); self?.updateSource() },
            content.metadataChanges.sink { [weak self] in self?.updateLyrics() },
            content.artworkChanges.sink { [weak self] in self?.updateArtwork() },
            content.appearanceChanges.sink { [weak self] in self?.updateAppearance() },
            content.clockChanges.sink { [weak self] in
                guard let self, self.rendering else { return }
                if self.lyricExpanded { self.updateLyricClock() } else { self.updateLyricPreview() }
                self.updateRotation()
            }
        ]
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (view: MusicFullPlayerView, _: UITraitCollection) in
            view.controls.updateFonts(compatibleWith: view.traitCollection)
            view.refreshVisibleLyrics()
            view.updateLyricPreview(force: true)
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
        contentTap.require(toFail: edge)
    }

    func setRendering(_ rendering: Bool) {
        guard self.rendering != rendering else { return }
        self.rendering = rendering
        controls.setRendering(rendering)
        updateLyricClock()
        updateLyricPreview(force: true)
        updateRotation()
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        if window != nil {
            controls.updateFonts(compatibleWith: traitCollection)
            setNeedsLayout()
        }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let width = bounds.width
        let top = safeAreaInsets.top
        let safeBottom = safeAreaInsets.bottom
        let bottom = bounds.height - safeBottom
        let safeHeight = max(0, bottom - top)
        let shortHeight = bounds.height <= 650
        let veryShortHeight = bounds.height <= 480
        let headerHeight = veryShortHeight ? 52 :
            shortHeight ? 56 : min(72, max(56, safeHeight * 0.0768))
        let headerButton = min(50, max(44, width * 0.1125))
        let headerInset = width * 0.05
        let headerLeading = max(headerInset, safeAreaInsets.left + 8)
        let headerTrailing = max(headerInset, safeAreaInsets.right + 8)
        if previewWidth != width {
            previewWidth = width
            updateLyricPreview(force: true)
        }
        let headerY = top + (headerHeight - headerButton) / 2
        close.frame = CGRect(x: headerLeading, y: headerY, width: headerButton, height: headerButton)
        route.frame = CGRect(x: width - headerTrailing - headerButton, y: headerY,
                             width: headerButton, height: headerButton)
        sourceLabel.frame = CGRect(x: close.frame.maxX + 12, y: top,
                                   width: max(0, route.frame.minX - close.frame.maxX - 24),
                                   height: headerHeight)

        controls.setFullViewport(viewportHeight: bounds.height, safeHeight: safeHeight)
        let controlsHeight = controls.fullHeight
        controls.frame = CGRect(x: 0, y: bottom - controlsHeight,
                                width: width, height: controlsHeight)
        contentFrame = CGRect(x: 0, y: top + headerHeight, width: width,
                              height: max(0, controls.frame.minY - top - headerHeight))

        let referenceLineHeight = min(34, max(28, safeHeight * 0.036))
        let scaledLineHeight = lyricPreviewLabels.map { $0.font.lineHeight }.max() ?? 0
        let lineHeight = max(referenceLineHeight, scaledLineHeight + 4)
        let previewHeight = lineHeight * 3
        let theoreticalSize = shortHeight
            ? min(width * 0.76, safeHeight * 0.37)
            : min(width * 0.808, safeHeight * 0.42, 400)
        let recordGap = safeHeight * (shortHeight ? 0.03 : 0.0665)
        let recordTop = contentFrame.minY + recordGap
        let recordRoom = max(0, contentFrame.maxY - recordTop - previewHeight - 8)
        let recordSize = veryShortHeight ? 0 : min(theoreticalSize, recordRoom)
        recordScroll.frame = CGRect(x: 0, y: recordTop, width: width, height: recordSize)
        recordScroll.contentSize = CGSize(width: width * 3, height: recordSize)
        for index in discs.indices {
            let page = index == records.center ? 1 : index == records.neighbor(.previous) ? 0 : 2
            discs[index].frame = CGRect(x: CGFloat(page) * width + (width - recordSize) / 2,
                                       y: 0, width: recordSize, height: recordSize)
        }
        let arm = CGRect(x: width / 2 + recordSize * 0.3655,
                         y: recordTop + recordSize * 0.01,
                         width: recordSize * 0.21, height: recordSize * 0.84)
        tonearm.bounds = CGRect(origin: .zero, size: arm.size)
        tonearm.center = CGPoint(x: arm.minX + arm.width * tonearm.layer.anchorPoint.x,
                                y: arm.minY + arm.height * tonearm.layer.anchorPoint.y)
        tonearm.isHidden = lyricExpanded || recordSize <= 0
        if recordWidth != width {
            recordWidth = width
            records.cancel()
            synchronizeRecords()
            recordScroll.setContentOffset(CGPoint(x: width, y: 0), animated: false)
        }

        let previewRoom = max(0, contentFrame.maxY - recordScroll.frame.maxY)
        let previewY = veryShortHeight
            ? contentFrame.midY - previewHeight / 2
            : min(contentFrame.maxY - previewHeight,
                  recordScroll.frame.maxY + max(4, (previewRoom - previewHeight) / 2))
        lyricPreview.frame = CGRect(x: 0, y: max(contentFrame.minY, previewY),
                                    width: width, height: previewHeight)
        for (index, label) in lyricPreviewLabels.enumerated() {
            label.frame = CGRect(x: 26, y: CGFloat(index) * lineHeight,
                                 width: max(0, width - 52), height: lineHeight)
        }

        let lyricsBottomGap = veryShortHeight ? 0 : max(0, safeHeight * 0.036)
        lyricTable.frame = CGRect(x: 0, y: contentFrame.minY + 10, width: width,
                                  height: max(0, contentFrame.height - 10 - lyricsBottomGap))
        lyricBackdrop.frame = lyricTable.frame
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        backdropMask.frame = lyricBackdrop.bounds
        lyricMask.frame = lyricTable.bounds
        CATransaction.commit()
        if lyricViewportSize != lyricTable.bounds.size {
            lyricViewportSize = lyricTable.bounds.size
            lyricTable.layoutIfNeeded()
            updateLyricInsets()
            reanchorLyrics()
        }

        let markerTop = lyricTable.frame.midY + (lyricSelection.isDragging ? 0 : -22)
        previewTime.frame = CGRect(x: 12, y: markerTop, width: 44, height: 44)
        previewSeek.frame = CGRect(x: width - 56, y: markerTop, width: 44, height: 44)
        previewRule.frame = CGRect(x: 56, y: markerTop + 21.5,
                                   width: max(0, width - 112), height: 1)
        followLyrics.frame = CGRect(x: 12, y: lyricTable.frame.maxY - 48, width: 44, height: 44)
        controls.layoutIfNeeded()
        let moreAnchor = controls.moreAnchor
        let moreFrame = moreAnchor.convert(moreAnchor.bounds, to: self)
        backgroundToggle.frame = CGRect(x: moreFrame.minX, y: controls.frame.minY - 52,
                                        width: 44, height: 44)
        translationToggle.frame = CGRect(x: moreFrame.minX, y: controls.frame.minY - 102,
                                         width: 44, height: 44)
        updateRotation()
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldReceive touch: UITouch) -> Bool {
        guard gestureRecognizer === contentTap, contentFrame.contains(touch.location(in: self)) else { return false }
        var view = touch.view
        while let current = view, current !== self {
            if current is UIControl { return false }
            view = current.superview
        }
        return true
    }

    func tableView(_ tableView: UITableView, didEndDisplaying cell: UITableViewCell,
                   forRowAt indexPath: IndexPath) {
        (cell as? MusicLyricCell)?.updateTime(content.clock.position, playing: false,
                                              rate: content.clock.rate, active: false)
    }

    func tableView(_ tableView: UITableView, willDisplay cell: UITableViewCell,
                   forRowAt indexPath: IndexPath) {
        guard !tableView.isDragging, indexPath.row == 0 || indexPath.row == lines.count - 1 else { return }
        updateLyricInsets()
        reanchorLyrics()
    }

    func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        if scrollView === lyricTable {
            lyricSelection.begin(content.fence, selected: centeredLyric() ?? activeLine, dragging: true,
                                 playbackLine: content.model.currentLyricIndex)
            followedLine = nil
            updateLyricClock()
        } else if scrollView === recordScroll {
            _ = records.begin(content.fence)
        }
    }

    func scrollViewDidScroll(_ scrollView: UIScrollView) {
        guard scrollView === lyricTable, lyricSelection.isDragging, let id = centeredLyric() else { return }
        lyricSelection.preview(id)
        updateLyricClock()
    }

    func scrollViewWillEndDragging(_ scrollView: UIScrollView, withVelocity velocity: CGPoint,
                                   targetContentOffset: UnsafeMutablePointer<CGPoint>) {
        guard scrollView === lyricTable, let index = nearestLyricIndex(to: lyricTable.contentOffset.y) else { return }
        targetContentOffset.pointee.y = centeredOffset(for: index)
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

    private var visualActiveLine: Int? {
        guard lyricSelection.isManual, let index = lyricSelection.playbackLine,
              lines.indices.contains(index) else { return activeLine }
        return lines[index].id
    }

    private func configure(_ cell: MusicLyricCell, line: MusicLyricLine) {
        let selected = line.id == lyricSelection.selected
        let timed = !lyricSelection.isManual && line.id == activeLine
        cell.configure(line: line, selected: selected, active: line.id == visualActiveLine, timed: timed,
                       previewDragging: lyricSelection.isDragging, position: content.clock.position,
                       rate: content.clock.rate, playing: rendering && lyricExpanded &&
                       content.clock.playing && timed, preferences: content.appearance.preferences,
                       theme: content.controlTheme)
        cell.accessibilityCustomActions = [
            UIAccessibilityCustomAction(name: "返回唱片", target: self,
                                        selector: #selector(accessibilityToggleLyrics))
        ]
    }

    private func updateLyrics() {
        let next = content.model.lyricLines
        lyricStatus.text = next.isEmpty ? (content.model.lyricState == "disabled" ? "歌词已关闭" :
            content.model.lyricState == "loading" ? "歌词加载中" : "暂无歌词") : nil
        lyricTable.backgroundView = lyricStatus.text == nil ? nil : lyricStatus
        guard next != lines else {
            updateLyricPreview(force: true)
            return
        }
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
            guard let self else { return }
            self.applyingLyrics = false
            self.lyricTable.layoutIfNeeded()
            self.updateLyricInsets()
            self.updateLyricClock()
            self.updateLyricPreview(force: true)
        }
    }

    private func updateLyricClock() {
        updatePreviewControls()
        guard !applyingLyrics else { return }
        let active = activeLine
        let visualActive = visualActiveLine
        let selected = lyricSelection.selected
        let clock = content.clock
        var resized = false
        for index in lyricTable.indexPathsForVisibleRows ?? [] {
            guard let id = lyricDataSource.itemIdentifier(for: index),
                  let cell = lyricTable.cellForRow(at: index) as? MusicLyricCell else { continue }
            let timed = !lyricSelection.isManual && id == active
            resized = cell.updateState(selected: id == selected, active: id == visualActive, timed: timed,
                previewDragging: lyricSelection.isDragging, position: clock.position,
                playing: rendering && lyricExpanded && clock.playing && timed,
                rate: clock.rate) || resized
        }
        if resized {
            lyricTable.performBatchUpdates(nil) { [weak self] _ in
                self?.updateLyricInsets()
                self?.reanchorLyrics()
            }
        }
        guard rendering, lyricExpanded, !lyricSelection.isManual, !lyricTable.isDragging,
              !lyricTable.isDecelerating, let active, followedLine != active else { return }
        followedLine = active
        centerLyric(active, animated: !UIAccessibility.isReduceMotionEnabled)
    }

    private func refreshVisibleLyrics() {
        for index in lyricTable.indexPathsForVisibleRows ?? [] {
            guard let id = lyricDataSource.itemIdentifier(for: index), let line = linesByID[id],
                  let cell = lyricTable.cellForRow(at: index) as? MusicLyricCell else { continue }
            configure(cell, line: line)
        }
        if !applyingLyrics {
            lyricTable.performBatchUpdates(nil) { [weak self] _ in
                self?.updateLyricInsets()
                self?.reanchorLyrics()
            }
        }
    }

    private func updatePreviewControls() {
        let manual = lyricExpanded && lyricSelection.isManual
        previewTime.isHidden = !manual
        previewSeek.isHidden = !manual
        followLyrics.isHidden = !manual || lyricSelection.isDragging
        previewRule.isHidden = !manual || !lyricSelection.isDragging
        let time = lyricSelection.previewTime(lines: content.model.lyricLines,
                                              current: content.fence, permitted: content.isAvailable)
        previewTime.text = time.map { MusicMiniPlayerText.time($0) }
        previewSeek.accessibilityLabel = time.map { "从 \(MusicMiniPlayerText.time($0)) 播放" } ?? "从选中歌词播放"
        previewSeek.isEnabled = lyricSelection.target(lines: content.model.lyricLines, current: content.fence,
            permitted: content.isAvailable, duration: content.clock.duration) != nil
        if markerDragging != lyricSelection.isDragging {
            markerDragging = lyricSelection.isDragging
            setNeedsLayout()
        }
    }

    private func centeredLyric() -> Int? {
        guard let index = nearestLyricIndex(to: lyricTable.contentOffset.y) else { return nil }
        return lyricDataSource.itemIdentifier(for: index)
    }

    private func nearestLyricIndex(to offset: CGFloat) -> IndexPath? {
        let point = CGPoint(x: max(1, lyricTable.bounds.midX),
                            y: offset + lyricTable.bounds.height / 2)
        if let index = lyricTable.indexPathForRow(at: point) { return index }
        guard !lines.isEmpty else { return nil }
        var closest: IndexPath?
        var distance = CGFloat.greatestFiniteMagnitude
        for row in lines.indices {
            let index = IndexPath(row: row, section: 0)
            let value = abs(lyricTable.rectForRow(at: index).midY - point.y)
            if value < distance { distance = value; closest = index }
        }
        return closest
    }

    private func centeredOffset(for index: IndexPath) -> CGFloat {
        let raw = lyricTable.rectForRow(at: index).midY - lyricTable.bounds.height / 2
        let minimum = -lyricTable.contentInset.top
        let maximum = max(minimum, lyricTable.contentSize.height - lyricTable.bounds.height +
                          lyricTable.contentInset.bottom)
        return min(maximum, max(minimum, raw))
    }

    private func centerLyric(_ id: Int, animated: Bool) {
        guard let index = lyricDataSource.indexPath(for: id) else { return }
        lyricTable.layoutIfNeeded()
        let point = CGPoint(x: lyricTable.contentOffset.x, y: centeredOffset(for: index))
        guard lyricTable.contentOffset != point else { return }
        if animated {
            UIView.animate(withDuration: 0.35, delay: 0,
                           options: [.beginFromCurrentState, .allowUserInteraction, .curveEaseInOut]) {
                self.lyricTable.setContentOffset(point, animated: false)
            }
        } else {
            lyricTable.setContentOffset(point, animated: false)
        }
    }

    private func updateLyricInsets() {
        guard lyricTable.bounds.height > 0, !lines.isEmpty else {
            lyricTable.contentInset = .zero
            lyricTable.scrollIndicatorInsets = .zero
            return
        }
        let first = lyricTable.rectForRow(at: IndexPath(row: 0, section: 0)).height
        let last = lyricTable.rectForRow(at: IndexPath(row: lines.count - 1, section: 0)).height
        let inset = UIEdgeInsets(top: max(0, lyricTable.bounds.height / 2 - first / 2), left: 0,
                                 bottom: max(0, lyricTable.bounds.height / 2 - last / 2), right: 0)
        if lyricTable.contentInset != inset {
            lyricTable.contentInset = inset
            lyricTable.scrollIndicatorInsets = inset
        }
    }

    private func reanchorLyrics() {
        guard lyricExpanded, !applyingLyrics, !lyricSelection.isDragging,
              !lyricTable.isDragging, !lyricTable.isDecelerating else { return }
        if let selected = lyricSelection.selected { centerLyric(selected, animated: false) }
        else if let active = activeLine { centerLyric(active, animated: false) }
    }

    private func confirmLyric() {
        guard let target = lyricSelection.target(lines: content.model.lyricLines, current: content.fence,
            permitted: content.isAvailable, duration: content.clock.duration) else { return }
        let confirmed = lyricSelection.confirm(accepted: content.seek(to: target, expected: content.fence),
                                                current: content.fence, permitted: content.isAvailable)
        if confirmed { followedLine = nil }
        updateLyricClock()
    }

    private func resumeLyricFollowing() {
        lyricSelection.cancel()
        followedLine = nil
        updateLyricClock()
    }

    @objc private func contentTapped() { toggleLyrics() }
    @objc private func accessibilityToggleLyrics() -> Bool {
        toggleLyrics()
        return true
    }

    private func toggleLyrics() {
        lyricSelection.cancel()
        lyricExpanded.toggle()
        followedLine = nil
        updateVisibility()
        updateLyricClock()
        updateRotation()
    }

    private func updateAccessibilityAction() {
        let label = lyricExpanded ? "返回唱片" : "展开完整歌词"
        lyricPreview.accessibilityLabel = label
        let action = UIAccessibilityCustomAction(name: label, target: self,
                                                 selector: #selector(accessibilityToggleLyrics))
        accessibilityCustomActions = [action]
        lyricTable.accessibilityCustomActions = lyricExpanded ? [action] : nil
        lyricStatus.accessibilityCustomActions = lyricExpanded ? [action] : nil
    }

    private func updateVisibility() {
        recordScroll.isHidden = lyricExpanded
        lyricPreview.isHidden = lyricExpanded
        tonearm.isHidden = lyricExpanded || tonearm.bounds.isEmpty
        lyricTable.isHidden = !lyricExpanded
        lyricBackdrop.isHidden = !content.appearance.preferences.showsLyricBackground(expanded: lyricExpanded)
        updateLyricTools()
        updateAccessibilityAction()
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
        updateLyricPreview(force: true)
        updateRotation()
    }

    private func synchronizeRecords() {
        records.synchronize(current: content.model.current, previous: content.model.recordPreview(.previous),
                            next: content.model.recordPreview(.next))
        let theme = PageTheme(palette: content.appearance.effectivePalette, dark: false)
        tonearm.configure(theme: theme)
        for slot in records.slots {
            discs[slot.id].configure(theme: theme, image: content.artwork(for: slot.entry))
        }
        recordScroll.isScrollEnabled = content.model.canNavigate && !records.busy
        updateRotation()
    }

    private func finishScroll(_ scrollView: UIScrollView) {
        if scrollView === lyricTable {
            if let id = centeredLyric() {
                lyricSelection.end(id)
                updateLyricInsets()
                centerLyric(id, animated: !UIAccessibility.isReduceMotionEnabled)
            }
            updateLyricClock()
            return
        }
        guard scrollView === recordScroll, recordScroll.bounds.width > 0, records.accepts(content.fence),
              !records.busy else { return }
        let page = Int((recordScroll.contentOffset.x / recordScroll.bounds.width).rounded())
        if page == 1 {
            records.cancel()
            synchronizeRecords()
            return
        }
        let direction: MusicDirection = page < 1 ? .previous : .next
        guard let token = records.request(direction, fence: content.fence) else { return }
        recordScroll.isScrollEnabled = false
        let accepted = content.model.requestNavigation(direction, onSelection: { [weak self] entry in
            guard let self else { return }
            if self.records.selected(entry, token: token, current: self.content.fence) {
                self.synchronizeRecords()
            }
        }, onCompletion: { [weak self] in
            guard let self, self.records.token == token else { return }
            if self.records.finish(token, currentID: self.content.model.current?.id) {
                self.synchronizeRecords()
                self.setNeedsLayout()
                self.layoutIfNeeded()
                self.recordScroll.setContentOffset(CGPoint(x: self.recordScroll.bounds.width, y: 0),
                                                   animated: false)
            } else if !self.records.accepts(self.content.fence) {
                self.records.cancel()
                self.synchronizeRecords()
                self.recordScroll.setContentOffset(CGPoint(x: self.recordScroll.bounds.width, y: 0),
                                                   animated: false)
            } else {
                self.returnRecordToCenter()
            }
        })
        if !accepted { returnRecordToCenter() }
    }

    private func returnRecordToCenter() {
        guard let token = records.returnToOrigin() else { return }
        let point = CGPoint(x: recordScroll.bounds.width, y: 0)
        let animated = rendering && !UIAccessibility.isReduceMotionEnabled && recordScroll.contentOffset != point
        recordScroll.setContentOffset(point, animated: animated)
        if !animated {
            _ = records.finish(token, currentID: content.model.current?.id)
            synchronizeRecords()
        }
    }

    private func updateArtwork() {
        lyricBackdrop.image = blurredArtwork(content.artwork)
        synchronizeRecords()
    }

    private func blurredArtwork(_ image: UIImage?) -> UIImage? {
        guard let image, let input = CIImage(image: image),
              let filter = CIFilter(name: "CIGaussianBlur") else { return image }
        filter.setValue(input.clampedToExtent(), forKey: kCIInputImageKey)
        filter.setValue(3, forKey: kCIInputRadiusKey)
        guard let output = filter.outputImage?.cropped(to: input.extent),
              let cgImage = blurContext.createCGImage(output, from: input.extent) else { return image }
        return UIImage(cgImage: cgImage, scale: image.scale, orientation: image.imageOrientation)
    }

    private func updateAppearance() {
        apply(palette: content.appearance.effectivePalette,
              animated: rendering && window != nil && content.appearance.animatesPaletteChanges)
        let theme = content.controlTheme
        close.tintColor = UIColor(theme.ink)
        previewSeek.tintColor = UIColor(theme.accent)
        followLyrics.tintColor = UIColor(theme.muted)
        sourceLabel.textColor = UIColor(theme.ink)
        lyricStatus.textColor = UIColor(theme.muted)
        previewTime.textColor = UIColor(theme.muted)
        previewRule.backgroundColor = UIColor(theme.muted).withAlphaComponent(0.28)
        route.tintColor = UIColor(theme.ink)
        route.activeTintColor = UIColor(theme.accent)
        for button in [translationToggle, backgroundToggle] {
            button.tintColor = UIColor(theme.muted)
            button.backgroundColor = UIColor(theme.buttonSurface)
            button.layer.cornerRadius = 22
        }
        updateVisibility()
        synchronizeRecords()
        updateLyricPreview(force: true)
        let translation = content.appearance.preferences.lyricTranslation
        if lyricTranslation != translation || lyricTheme != theme {
            lyricTranslation = translation
            lyricTheme = theme
            refreshVisibleLyrics()
        }
    }

    private func updateLyricTools() {
        for button in [translationToggle, backgroundToggle] {
            button.isHidden = !lyricExpanded
            button.isUserInteractionEnabled = lyricExpanded
            button.accessibilityElementsHidden = !lyricExpanded
        }
        guard lyricExpanded else { return }
        let preferences = content.appearance.preferences
        translationToggle.accessibilityLabel = preferences.translationActionLabel
        translationToggle.accessibilityValue = preferences.lyricTranslation ? "已显示" : "已隐藏"
        translationToggle.alpha = preferences.lyricTranslation ? 1 : 0.5
        backgroundToggle.accessibilityLabel = preferences.backgroundActionLabel
        backgroundToggle.accessibilityValue = preferences.lyricBackground ? "已显示" : "已隐藏"
        backgroundToggle.alpha = preferences.lyricBackground ? 1 : 0.5
    }

    private func updateSource() {
        let model = content.model
        sourceLabel.text = MusicReferencePlayerLabels.source(
            fm: model.isFMActive ? MusicFMModeChoice.title(for: model.activeHomeFM) : nil,
            context: model.current?.sourceTitle,
            collection: model.collections.first { $0.id == model.state.sourceCollectionID }?.title,
            track: model.displayTrack?.title ?? "")
    }

    private func updateLyricPreview(force: Bool = false) {
        let activeIndex = content.model.currentLyricIndex
        var indices: [Int?] = [nil, nil, nil]
        if !lines.isEmpty {
            let center = lines.count >= 3
                ? min(lines.count - 2, max(1, activeIndex ?? 1))
                : min(lines.count - 1, max(0, activeIndex ?? lines.count / 2))
            for offset in -1...1 {
                let lineIndex = center + offset
                if lines.indices.contains(lineIndex) { indices[offset + 1] = lineIndex }
            }
        }
        let identity = indices.map { $0.map { lines[$0].id } } + [activeIndex]
        guard force || identity != previewIdentity else { return }
        previewIdentity = identity
        let baseSize = min(20, max(16, bounds.width * 0.0422))
        for index in lyricPreviewLabels.indices {
            let label = lyricPreviewLabels[index]
            guard let lineIndex = indices[index] else {
                label.text = lines.isEmpty && index == 1 ? lyricStatus.text : nil
                label.textColor = UIColor(content.controlTheme.muted)
                label.font = UIFontMetrics(forTextStyle: .body).scaledFont(
                    for: .systemFont(ofSize: baseSize, weight: .medium))
                continue
            }
            let isActive = lineIndex == activeIndex
            label.text = lines[lineIndex].text
            label.textColor = UIColor(isActive ? content.controlTheme.ink : content.controlTheme.muted)
            label.font = UIFontMetrics(forTextStyle: .body).scaledFont(
                for: .systemFont(ofSize: baseSize, weight: isActive ? .semibold : .medium))
        }
        lyricPreview.accessibilityValue = indices.compactMap { $0.map { lines[$0].text } }
            .joined(separator: "\n")
        if lyricPreview.accessibilityValue?.isEmpty != false {
            lyricPreview.accessibilityValue = lyricStatus.text
        }
    }

    private func updateRotation() {
        let current = records.phase == .settling
            ? records.direction.map { records.neighbor($0) } ?? records.center : records.center
        for index in discs.indices {
            discs[index].setRotating(index == current && rendering && recordScroll.bounds.height > 0 &&
                                     !lyricExpanded &&
                                     content.clock.playing && !UIAccessibility.isReduceMotionEnabled)
        }
        tonearm.setPlaying(content.clock.playing)
    }
}
#endif
