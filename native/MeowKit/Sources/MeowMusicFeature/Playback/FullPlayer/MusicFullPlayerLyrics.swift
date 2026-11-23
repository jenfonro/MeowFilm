#if os(iOS)
import UIKit
import MeowUI

@MainActor
extension MusicFullPlayerView {
    func tableView(_ tableView: UITableView, didEndDisplaying cell: UITableViewCell,
                   forRowAt indexPath: IndexPath) {
        (cell as? MusicLyricCell)?.updateState(
            selected: false, active: false, timed: false, previewDragging: false,
            position: content.clock.position, playing: false, rate: content.clock.rate
        )
    }

    func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
        guard let id = lyricDataSource.itemIdentifier(for: indexPath), let line = lyricProjection[id] else { return 0 }
        if let height = lyricHeights[id] { return height }
        let height = MusicLyricCell.height(for: line, width: tableView.bounds.width,
            preferences: content.appearance.preferences, traits: traitCollection)
        lyricHeights[id] = height
        return height
    }

    var activeLine: Int? {
        content.model.currentLyricIndex.flatMap {
            content.model.lyricLines.indices.contains($0) ? content.model.lyricLines[$0].id : nil
        }
    }

    var visualActiveLine: Int? {
        guard lyricSelection.isManual, let index = lyricSelection.playbackLine,
              lines.indices.contains(index) else { return activeLine }
        return lines[index].id
    }

    func configure(_ cell: MusicLyricCell, line: MusicLyricLine) {
        let selected = line.id == lyricSelection.selected
        let timed = !lyricSelection.isManual && line.id == activeLine
        cell.configure(line: line, selected: selected, active: line.id == visualActiveLine, timed: timed,
                       previewDragging: lyricSelection.isDragging, position: content.clock.position,
                       rate: content.clock.rate, playing: rendering && lyricExpanded &&
                       content.clock.playing && timed, preferences: content.appearance.preferences,
                       colors: lyricColors)
        cell.accessibilityCustomActions = [
            UIAccessibilityCustomAction(name: "返回唱片", target: self,
                                        selector: #selector(accessibilityToggleLyrics))
        ]
    }

    func updateLyrics() {
        let next = content.model.lyricLines
        lyricStatus.text = next.isEmpty ? (content.model.lyricState == "disabled" ? "歌词已关闭" :
            content.model.lyricState == "loading" ? "歌词加载中" : "暂无歌词") : nil
        lyricTable.backgroundView = lyricStatus.text == nil ? nil : lyricStatus
        let update = lyricProjection.update(section: 0, items: next, id: \.id)
        guard update.hasChanges else {
            updateLyricPreview(force: true)
            return
        }
        lyricHeights.removeAll()
        lyricsPositioned = false
        applyingLyrics = true
        let revision = UUID()
        lyricRevision = revision
        followedLine = nil
        lyricDataSource.applyIfNeeded(update, animatingDifferences: false) { [weak self] in
            guard let self, self.lyricRevision == revision else { return }
            self.applyingLyrics = false
            self.updateLyricGeometry()
            self.updateLyricClock()
            self.updateLyricPreview(force: true)
        }
    }

    func updateLyricClock(forceCalibrate: Bool = false) {
        updatePreviewControls()
        guard !applyingLyrics else { return }
        if centerAfterSeekSample {
            centerAfterSeekSample = false
            followedLine = nil
        }
        let active = activeLine
        let visualActive = visualActiveLine
        let selected = lyricSelection.selected
        let clock = content.clock
        for index in lyricTable.indexPathsForVisibleRows ?? [] {
            guard let id = lyricDataSource.itemIdentifier(for: index),
                  let cell = lyricTable.cellForRow(at: index) as? MusicLyricCell else { continue }
            let timed = !lyricSelection.isManual && id == active
            cell.updateState(selected: id == selected, active: id == visualActive, timed: timed,
                previewDragging: lyricSelection.isDragging, position: clock.position,
                playing: rendering && lyricExpanded && clock.playing && timed,
                rate: clock.rate, forceCalibrate: forceCalibrate)
        }
        guard rendering, lyricExpanded, !lyricSelection.isManual, !lyricTable.isDragging,
              !lyricTable.isDecelerating, let target = active ?? lines.first?.id,
              followedLine != target || !lyricsPositioned else { return }
        followedLine = target
        centerLyric(target, animated: lyricsPositioned && !UIAccessibility.isReduceMotionEnabled)
    }

    func refreshVisibleLyrics() {
        for index in lyricTable.indexPathsForVisibleRows ?? [] {
            guard let id = lyricDataSource.itemIdentifier(for: index), let line = lyricProjection[id],
                  let cell = lyricTable.cellForRow(at: index) as? MusicLyricCell else { continue }
            configure(cell, line: line)
        }
        lyricHeights.removeAll()
        updateLyricGeometry()
    }

    func updatePreviewControls() {
        let manual = lyricExpanded && lyricSelection.isManual
        if previewTime.isHidden != !manual { previewTime.isHidden = !manual }
        if previewSeek.isHidden != !manual { previewSeek.isHidden = !manual }
        let hideFollow = !manual || lyricSelection.isDragging
        if followLyrics.isHidden != hideFollow { followLyrics.isHidden = hideFollow }
        let hideRule = !manual || !lyricSelection.isDragging
        if previewRule.isHidden != hideRule { previewRule.isHidden = hideRule }
        if markerDragging != lyricSelection.isDragging {
            markerDragging = lyricSelection.isDragging
            setNeedsLayout()
        }
        guard manual else { return }
        let time = lyricSelection.previewTime(lines: content.model.lyricLines,
                                              current: content.fence, permitted: content.isAvailable)
        previewTime.text = time.map { MusicMiniPlayerText.time($0) }
        previewSeek.accessibilityLabel = time.map { "从 \(MusicMiniPlayerText.time($0)) 播放" } ?? "从选中歌词播放"
        previewSeek.isEnabled = lyricSelection.target(lines: content.model.lyricLines, current: content.fence,
            permitted: content.isAvailable, duration: content.clock.duration) != nil
    }

    func centeredLyric() -> Int? {
        guard let index = nearestLyricIndex(to: lyricTable.contentOffset.y) else { return nil }
        return lyricDataSource.itemIdentifier(for: index)
    }

    func nearestLyricIndex(to offset: CGFloat) -> IndexPath? {
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

    func centeredOffset(for index: IndexPath) -> CGFloat {
        let raw = lyricTable.rectForRow(at: index).midY - lyricTable.bounds.height / 2
        let minimum = -lyricTable.contentInset.top
        let maximum = max(minimum, lyricTable.contentSize.height - lyricTable.bounds.height +
                          lyricTable.contentInset.bottom)
        return min(maximum, max(minimum, raw))
    }

    func centerLyric(_ id: Int, animated: Bool) {
        guard lyricTable.bounds.width > 0, lyricTable.bounds.height > 0,
              let index = lyricDataSource.indexPath(for: id) else { return }
        lyricTable.layoutIfNeeded()
        let point = CGPoint(x: lyricTable.contentOffset.x, y: centeredOffset(for: index))
        lyricsPositioned = true
        guard lyricTable.contentOffset != point else { return }
        lyricTable.setContentOffset(point, animated: animated)
    }

    func updateLyricInsets() {
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

    func updateLyricGeometry() {
        guard !applyingLyrics, lyricTable.bounds.width > 0 else { return }
        UIView.performWithoutAnimation {
            lyricTable.beginUpdates()
            lyricTable.endUpdates()
            lyricTable.layoutIfNeeded()
            updateLyricInsets()
        }
        guard lyricExpanded, !lyricSelection.isDragging,
              !lyricTable.isDragging, !lyricTable.isDecelerating else { return }
        if let target = lyricSelection.selected ?? activeLine ?? lines.first?.id {
            followedLine = target
            centerLyric(target, animated: lyricsPositioned && !UIAccessibility.isReduceMotionEnabled)
        }
    }

    func confirmLyric() {
        guard let target = lyricSelection.target(lines: content.model.lyricLines, current: content.fence,
            permitted: content.isAvailable, duration: content.clock.duration) else { return }
        _ = content.seek(to: target, expected: content.fence)
    }

    func resumeLyricFollowing() {
        lyricSelection.cancel()
        followedLine = nil
        updateLyricClock()
    }

    @objc func contentTapped() { toggleLyrics() }
    @objc private func accessibilityToggleLyrics() -> Bool {
        toggleLyrics()
        return true
    }

    func toggleLyrics() {
        lyricSelection.cancel()
        centerAfterSeekSample = false
        lyricExpanded.toggle()
        followedLine = nil
        lyricsPositioned = false
        updateVisibility()
        updateLyricClock()
        updateRotation()
    }

    func updateAccessibilityAction() {
        let label = lyricExpanded ? "返回唱片" : "展开完整歌词"
        lyricPreview.accessibilityLabel = label
        let action = UIAccessibilityCustomAction(name: label, target: self,
                                                 selector: #selector(accessibilityToggleLyrics))
        accessibilityCustomActions = [action]
        lyricTable.accessibilityCustomActions = lyricExpanded ? [action] : nil
        lyricStatus.accessibilityCustomActions = lyricExpanded ? [action] : nil
    }

    func updateLyricPreview(force: Bool = false) {
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
                    for: .systemFont(ofSize: baseSize))
                continue
            }
            let isActive = lineIndex == activeIndex
            label.text = lines[lineIndex].text
            label.textColor = UIColor(isActive ? content.controlTheme.ink : content.controlTheme.muted)
            label.font = UIFontMetrics(forTextStyle: .body).scaledFont(
                for: .systemFont(ofSize: baseSize))
        }
        lyricPreview.accessibilityValue = indices.compactMap { $0.map { lines[$0].text } }
            .joined(separator: "\n")
        if lyricPreview.accessibilityValue?.isEmpty != false {
            lyricPreview.accessibilityValue = lyricStatus.text
        }
    }

}
#endif
