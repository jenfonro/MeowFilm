#if os(iOS)
import UIKit
import Combine
import AVKit
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
    var onFMSettings: (() -> Void)? { get { controls.onFMSettings } set { controls.onFMSettings = newValue } }
    var onSources: (() -> Void)? { get { controls.onSources } set { controls.onSources = newValue } }
    var moreAnchor: UIView { controls.moreAnchor }

    let content: MusicPlayerContentModel
    let controls: MusicPlaybackControlsView
    lazy var close: PageHeaderButton = {
        let button = PageHeaderButton(image: PageIcon.image(.playerDown, size: 25),
                                      accessibilityLabel: "收起播放器") { [weak self] in self?.onClose?() }
        button.accessibilityIdentifier = "music.player.close"
        return button
    }()
    private(set) var route: AVRoutePickerView?
    private let routeContainer = UIView()
    let sourceButton = UIButton(type: .system)
    let recordScroll = MusicRecordScrollView()
    let discs = (0..<3).map { _ in MusicRecordDiscView() }
    let tonearm = MusicTonearmView()
    let lyricPreview = UIButton(type: .custom)
    let lyricPreviewLabels = (0..<3).map { _ in UILabel() }
    let lyricViewport = UIView()
    let lyricTable = UITableView(frame: .zero, style: .plain)
    let lyricStatus = UILabel()
    let previewTime = UILabel()
    let previewRule = UIView()
    let previewSeek = UIButton(type: .system)
    let followLyrics = UIButton(type: .system)
    let translationToggle = UIButton(type: .system)
    let backgroundToggle = UIButton(type: .system)
    let lyricBackdrop = UIImageView()
    let backdropMask = CAGradientLayer()
    let lyricMask = CAGradientLayer()
    var blurTask: Task<Void, Never>?
    var blurGeneration: UInt64 = 0
    var blurKey: String?
    lazy var contentTap: UITapGestureRecognizer = {
        let value = UITapGestureRecognizer(target: self, action: #selector(contentTapped))
        value.cancelsTouchesInView = false
        value.delegate = self
        return value
    }()

    var records = MusicRecordTransition()
    var lyricSelection = MusicReferenceLyricSelection()
    var lyricProjection = IndexedDiffableProjection<Int, Int, MusicLyricLine>()
    var lines: [MusicLyricLine] { lyricProjection.items }
    var displayedFence: MusicReferencePlayerFence?
    var followedLine: Int?
    var centerAfterSeekSample = false
    var applyingLyrics = false
    var lyricRevision = UUID()
    var lyricExpanded = false
    var rendering = false
    var recordWidth: CGFloat = 0
    var lyricTranslation: Bool?
    var lyricColors: MusicLyricColors
    var lyricViewportSize = CGSize.zero
    var lyricHeights: [Int: CGFloat] = [:]
    var lyricsPositioned = false
    var previewWidth: CGFloat = 0
    var previewIdentity: [Int?] = []
    var markerDragging = false
    var contentFrame = CGRect.zero
    var subscriptions: [AnyCancellable] = []
    lazy var lyricDataSource = UITableViewDiffableDataSource<Int, Int>(tableView: lyricTable) {
        [weak self] table, index, id in
        let cell = table.dequeueReusableCell(withIdentifier: MusicLyricCell.reuseIdentifier,
                                             for: index) as! MusicLyricCell
        if let self, let line = self.lyricProjection[id] { self.configure(cell, line: line) }
        return cell
    }

    init(content: MusicPlayerContentModel) {
        self.content = content
        lyricColors = MusicPlayerBackgroundView.lyricColors(palette: content.appearance.effectivePalette,
            artwork: content.appearance.preferences.lyricBackground)
        controls = MusicPlaybackControlsView(content: content, layout: .full)
        super.init(style: .full, palette: content.appearance.effectivePalette)
        accessibilityIdentifier = "music.player.page"
        for item in [lyricBackdrop, recordScroll, tonearm, lyricPreview, lyricViewport, controls,
                     translationToggle, backgroundToggle, close, routeContainer, sourceButton,
                     previewRule, previewTime, previewSeek, followLyrics] {
            addSubview(item)
        }

        routeContainer.backgroundColor = .clear
        var sourceStyle = UIButton.Configuration.plain()
        sourceStyle.contentInsets = .zero
        sourceStyle.imagePlacement = .trailing
        sourceStyle.imagePadding = 5
        sourceStyle.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { attributes in
            var result = attributes
            result.font = .systemFont(ofSize: 14)
            return result
        }
        sourceButton.configuration = sourceStyle
        sourceButton.titleLabel?.lineBreakMode = .byTruncatingTail
        sourceButton.accessibilityIdentifier = "music.player.fmSource"
        sourceButton.addAction(UIAction { [weak self] _ in
            guard let self, self.rendering, self.content.isAvailable, self.content.model.isFMActive else { return }
            self.onFMSettings?()
        }, for: .touchUpInside)

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
        // Exact, state-independent heights keep distant seeks and font emphasis
        // from correcting the scroll offset after movement has already begun.
        lyricTable.estimatedRowHeight = 0
        lyricTable.delegate = self
        lyricTable.allowsSelection = false
        lyricTable.bounces = false
        lyricTable.isDirectionalLockEnabled = true
        lyricTable.decelerationRate = .fast
        lyricTable.showsVerticalScrollIndicator = false
        lyricTable.contentInsetAdjustmentBehavior = .never
        lyricTable.register(MusicLyricCell.self, forCellReuseIdentifier: MusicLyricCell.reuseIdentifier)
        lyricTable.accessibilityIdentifier = "music.player.lyrics"
        lyricViewport.addSubview(lyricTable)
        lyricViewport.layer.mask = lyricMask
        lyricViewport.clipsToBounds = true
        lyricStatus.textAlignment = .center
        lyricStatus.isAccessibilityElement = true

        lyricBackdrop.contentMode = .scaleAspectFill
        lyricBackdrop.clipsToBounds = true
        lyricBackdrop.alpha = CGFloat(MusicLyricColors.artworkOpacity)
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
            Task { await content.appearance.set(\.lyricTranslation, !content.appearance.preferences.lyricTranslation) }
        }, for: .touchUpInside)
        backgroundToggle.setImage(PageIcon.image(.image, size: 21), for: .normal)
        backgroundToggle.accessibilityIdentifier = "music.player.lyricBackground"
        backgroundToggle.addAction(UIAction { [weak content] _ in
            guard let content else { return }
            Task { await content.appearance.set(\.lyricBackground, !content.appearance.preferences.lyricBackground) }
        }, for: .touchUpInside)

        addGestureRecognizer(contentTap)
        updateAccessibilityAction()
        subscriptions = [
            content.playbackChanges.sink { [weak self] in self?.reconcilePlayback() },
            content.queueChanges.sink { [weak self] in self?.synchronizeRecords(); self?.updateSource() },
            content.metadataChanges.sink { [weak self] in self?.updateLyrics() },
            content.artworkChanges.sink { [weak self] in self?.updateArtwork() },
            content.seekChanges.sink { [weak self] _ in
                guard let self else { return }
                self.centerAfterSeekSample = self.centerAfterSeekSample || self.lyricSelection.isManual
                _ = self.lyricSelection.confirm(accepted: true, current: self.content.fence,
                                               permitted: self.content.isAvailable)
                // The accepted media position, not the slider's requested time,
                // remains the source of the next scrolling destination.
                self.followedLine = self.activeLine ?? self.lines.first?.id
                self.updatePreviewControls()
                if self.rendering, self.lyricExpanded {
                    self.updateLyricClock(forceCalibrate: true)
                }
            },
            content.positionSamples.sink { [weak self] _ in
                guard let self, self.rendering else { return }
                if self.lyricExpanded {
                    self.updateLyricClock(forceCalibrate: false)
                } else {
                    self.updateLyricPreview()
                }
            },
            content.appearanceChanges.sink { [weak self] in self?.updateAppearance() },
            content.clockChanges.sink { [weak self] in
                guard let self, self.rendering else { return }
                if self.lyricExpanded { self.updateLyricClock(forceCalibrate: false) } else { self.updateLyricPreview() }
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

    /// Defer `AVRoutePickerView` instantiation until after presentation completes so
    /// AirPlay / Bluetooth output route discovery IPC never collides with opening frames.
    func installRoutePickerIfNeeded() {
        guard route == nil else { return }
        let picker = AVRoutePickerView()
        picker.prioritizesVideoDevices = false
        picker.accessibilityLabel = "音频输出"
        picker.accessibilityIdentifier = "music.player.audioOutput"
        let theme = PageTheme(colors: content.appearance.effectivePalette, dark: false)
        picker.tintColor = UIColor(theme.ink)
        picker.activeTintColor = UIColor(theme.accent)
        picker.frame = routeContainer.bounds
        picker.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        routeContainer.addSubview(picker)
        route = picker
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
        updateLyricBackdrop()
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
        routeContainer.frame = CGRect(x: width - headerTrailing - headerButton, y: headerY,
                                      width: headerButton, height: headerButton)
        route?.frame = routeContainer.bounds
        sourceButton.frame = CGRect(x: close.frame.maxX + 12, y: top,
                                   width: max(0, routeContainer.frame.minX - close.frame.maxX - 24),
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
        lyricViewport.frame = CGRect(x: 0, y: contentFrame.minY + 10, width: width,
                                     height: max(0, contentFrame.height - 10 - lyricsBottomGap))
        lyricTable.frame = lyricViewport.bounds
        lyricBackdrop.frame = lyricViewport.frame
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        backdropMask.frame = lyricBackdrop.bounds
        lyricMask.frame = lyricViewport.bounds
        CATransaction.commit()
        if lyricViewportSize != lyricTable.bounds.size {
            let widthChanged = lyricViewportSize.width != lyricTable.bounds.width
            lyricViewportSize = lyricTable.bounds.size
            if widthChanged { lyricHeights.removeAll() }
            updateLyricGeometry()
        }

        let markerTop = lyricViewport.frame.midY + (lyricSelection.isDragging ? 0 : -22)
        previewTime.frame = CGRect(x: 12, y: markerTop, width: 44, height: 44)
        previewSeek.frame = CGRect(x: width - 56, y: markerTop, width: 44, height: 44)
        previewRule.frame = CGRect(x: 56, y: markerTop + 21.5,
                                   width: max(0, width - 112), height: 1)
        followLyrics.frame = CGRect(x: 12, y: lyricViewport.frame.maxY - 48, width: 44, height: 44)
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

    func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        if scrollView === lyricTable {
            lyricTable.setContentOffset(lyricTable.contentOffset, animated: false)
            centerAfterSeekSample = false
            lyricSelection.begin(content.fence, selected: centeredLyric() ?? activeLine, dragging: true,
                                 playbackLine: content.model.currentLyricIndex)
            followedLine = nil
            updateLyricClock()
        } else if scrollView === recordScroll {
            _ = records.begin(content.fence)
        }
    }

    func scrollViewDidScroll(_ scrollView: UIScrollView) {
        if scrollView === recordScroll {
            updateTonearm()
            return
        }
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

    func updateVisibility() {
        recordScroll.isHidden = lyricExpanded
        lyricPreview.isHidden = lyricExpanded
        tonearm.isHidden = lyricExpanded || tonearm.bounds.isEmpty
        lyricViewport.isHidden = !lyricExpanded
        lyricBackdrop.isHidden = !content.appearance.preferences.showsLyricBackground(expanded: lyricExpanded)
        updateLyricBackdrop()
        updateLyricTools()
        updateAccessibilityAction()
        updatePreviewControls()
    }

    func reconcilePlayback() {
        if displayedFence != content.fence {
            if displayedFence?.occurrence != content.fence.occurrence { lyricsPositioned = false }
            centerAfterSeekSample = false
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

    func synchronizeRecords() {
        let old = records.slots[records.center].entry?.id
        if records.phase == .idle, old != nil, old != content.model.current?.id {
            // Button/system navigation replaces the center in place. Retire the
            // old element before supplying the incoming artwork, never on image updates.
            discs[records.center].resetAfterDeparture()
        }
        records.synchronize(current: content.model.current, previous: content.model.recordPreview(.previous),
                            next: content.model.recordPreview(.next))
        let theme = PageTheme(colors: content.appearance.effectivePalette, dark: false)
        tonearm.configure(theme: theme)
        for slot in records.slots {
            discs[slot.id].configure(theme: theme, image: content.artwork(for: slot.entry))
        }
        recordScroll.isScrollEnabled = content.model.canNavigate && !records.busy
        updateRotation()
    }

    func finishScroll(_ scrollView: UIScrollView) {
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
            let outgoing = self.records.center
            if self.records.finish(token, currentID: self.content.model.current?.id) {
                if outgoing != self.records.center { self.discs[outgoing].resetAfterDeparture() }
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

    func returnRecordToCenter() {
        guard let token = records.returnToOrigin() else { return }
        let point = CGPoint(x: recordScroll.bounds.width, y: 0)
        let animated = rendering && !UIAccessibility.isReduceMotionEnabled && recordScroll.contentOffset != point
        recordScroll.setContentOffset(point, animated: animated)
        if !animated {
            _ = records.finish(token, currentID: content.model.current?.id)
            synchronizeRecords()
        }
    }

    func updateArtwork() {
        updateLyricBackdrop()
        synchronizeRecords()
    }

    func updateLyricBackdrop() {
        let image = content.artwork
        let currentKey = image.map {
            content.model.displayTrack?.artwork?.absoluteString ?? String(ObjectIdentifier($0).hashValue)
        }
        if blurKey != currentKey {
            blurTask?.cancel()
            blurTask = nil
            blurGeneration &+= 1
            blurKey = nil
            lyricBackdrop.image = nil
        }
        guard rendering, content.appearance.preferences.showsLyricBackground(expanded: lyricExpanded),
              let image, let key = currentKey else {
            blurTask?.cancel()
            blurTask = nil
            blurGeneration &+= 1
            return
        }
        if blurKey == key && lyricBackdrop.image != nil { return }
        if blurKey == key && blurTask != nil { return }
        blurTask?.cancel()
        blurTask = nil
        blurGeneration &+= 1
        let generation = blurGeneration
        blurKey = key
        if let cached = content.artworkImages.effects.cachedBlur(forKey: key) {
            lyricBackdrop.image = cached
        } else {
            let effects = content.artworkImages.effects
            blurTask = Task { [weak self] in
                let blurred = await effects.blurredImage(for: image, key: key, radius: 3)
                guard let self, !Task.isCancelled, self.blurGeneration == generation else { return }
                self.blurTask = nil
                self.lyricBackdrop.image = blurred ?? image
            }
        }
    }

    func updateAppearance() {
        apply(palette: content.appearance.effectivePalette,
              animated: rendering && window != nil && content.appearance.animatesPaletteChanges)
        let theme = PageTheme(colors: content.appearance.effectivePalette, dark: false)
        close.tintColor = UIColor(theme.ink)
        previewSeek.tintColor = UIColor(theme.accent)
        followLyrics.tintColor = UIColor(theme.muted)
        sourceButton.configuration?.baseForegroundColor = UIColor(theme.ink)
        let colors = MusicPlayerBackgroundView.lyricColors(palette: content.appearance.effectivePalette,
            artwork: content.appearance.preferences.lyricBackground)
        lyricStatus.textColor = UIColor(colors.normal)
        previewTime.textColor = UIColor(colors.normal)
        previewRule.backgroundColor = UIColor(colors.highlight).withAlphaComponent(0.28)
        route?.tintColor = UIColor(theme.ink)
        route?.activeTintColor = UIColor(theme.accent)
        for button in [translationToggle, backgroundToggle] {
            button.tintColor = UIColor(theme.muted)
            button.backgroundColor = UIColor(theme.buttonSurface)
            button.layer.cornerRadius = 22
        }
        updateVisibility()
        synchronizeRecords()
        updateLyricPreview(force: true)
        let translation = content.appearance.preferences.lyricTranslation
        if lyricTranslation != translation || lyricColors != colors {
            lyricTranslation = translation
            lyricColors = colors
            refreshVisibleLyrics()
        }
    }

    func updateLyricTools() {
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

    func updateSource() {
        let model = content.model
        let title = MusicReferencePlayerLabels.source(
            fm: model.isFMActive ? MusicReferencePlayerLabels.fmSource(model.activeHomeFM) : nil,
            context: model.playbackSourceTitle,
            collection: model.collections.first { $0.id == model.state.sourceCollectionID }?.title,
            track: model.displayTrack?.title ?? "")
        sourceButton.configuration?.title = title
        sourceButton.configuration?.image = model.isFMActive ? PageIcon.image(.down, size: 10) : nil
        sourceButton.isUserInteractionEnabled = model.isFMActive && content.isAvailable
        sourceButton.accessibilityTraits = model.isFMActive ? .button : .staticText
        sourceButton.accessibilityLabel = model.isFMActive
            ? "切换 FM：" + MusicFMModeChoice.title(for: model.activeHomeFM) : title
    }

    func updateRotation() {
        let current = records.phase == .settling
            ? records.direction.map { records.neighbor($0) } ?? records.center : records.center
        for index in discs.indices {
            discs[index].setRotating(index == current && rendering && recordScroll.bounds.height > 0 &&
                                     !lyricExpanded &&
                                     content.clock.playing && !UIAccessibility.isReduceMotionEnabled)
        }
        updateTonearm()
    }

    func updateTonearm() {
        let model = content.model
        let navigatingToPlayback = model.isNavigating &&
            (model.navigationPlaybackIntent?.isActive == true ||
             (model.fmAdvanceIntent?.isActive == true && model.fmShouldPlay))
        let engaged = model.phase == .playing || model.phase == .buffering ||
            model.phase == .preparing || navigatingToPlayback
        if records.phase != .idle, recordScroll.bounds.width > 0 {
            let page = recordScroll.contentOffset.x / recordScroll.bounds.width
            let nearest = min(2, max(0, page.rounded()))
            tonearm.setContactProgress(engaged ? max(0, 1 - abs(page - nearest) * 2) : 0)
        } else {
            tonearm.setPlaying(engaged)
        }
    }

    deinit {
        blurTask?.cancel()
    }
}
#endif
