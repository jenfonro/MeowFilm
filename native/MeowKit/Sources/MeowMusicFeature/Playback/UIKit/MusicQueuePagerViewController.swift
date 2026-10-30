// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS)
import UIKit
import MeowUI

/// Two full-width lists and retained history cards share one native scroll strip.
@MainActor
final class MusicQueuePagerViewController: UIViewController, UIScrollViewDelegate {
    let scroll = UIScrollView()
    var makePage: ((String) -> UIViewController?)?
    var onSelect: (() -> Void)?
    var onCategoryPosition: ((CGFloat) -> Void)?
    private(set) var selectedID = "current"
    private var ids: [String] = []
    private var pages: [String: UIViewController] = [:]
    private var positions: [String: CGFloat] = [:]
    private var visible = Set<String>()
    private var appeared = false, layingOut = false, needsPosition = true
    private var previousSize = CGSize.zero

    var categoryPosition: CGFloat {
        guard let songs = positions["songs"], songs > 0 else { return 0 }
        let x = scroll.contentOffset.x
        if x <= songs { return max(0, x / songs) }
        guard let id = ids.first(where: { MusicReferenceQueueStrip.category($0) == .playlists }),
              let history = positions[id], history > songs else { return 1 }
        return min(2, 1 + (x - songs) / (history - songs))
    }

    override var shouldAutomaticallyForwardAppearanceMethods: Bool { false }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear
        scroll.delegate = self
        scroll.showsHorizontalScrollIndicator = false
        scroll.showsVerticalScrollIndicator = false
        scroll.contentInsetAdjustmentBehavior = .never
        scroll.decelerationRate = .fast
        scroll.isDirectionalLockEnabled = true
        scroll.bounces = false
        scroll.accessibilityIdentifier = "music.queue.pages"
        view.addSubview(scroll)
    }

    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        appeared = true
        view.layoutIfNeeded()
        updateVisiblePages()
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        appeared = false
        updateVisiblePages()
    }

    func setPages(_ ids: [String], selected: String) {
        guard self.ids != ids else {
            if selectedID != selected { select(selected, animated: false) }
            return
        }
        loadViewIfNeeded()
        let removed = Set(self.ids).subtracting(ids)
        for id in removed {
            guard let page = pages.removeValue(forKey: id) else { continue }
            if visible.remove(id) != nil { setVisible(page, false) }
            PageViewController.invalidatePages(in: page)
            page.willMove(toParent: nil)
            page.view.removeFromSuperview()
            page.removeFromParent()
        }
        for id in ids where pages[id] == nil {
            guard let page = makePage?(id) else { continue }
            addChild(page)
            scroll.addSubview(page.view)
            page.didMove(toParent: self)
            pages[id] = page
        }
        self.ids = ids
        selectedID = selected
        needsPosition = true
        view.setNeedsLayout()
        view.layoutIfNeeded()
    }

    func select(_ id: String, animated: Bool = true) {
        guard ids.contains(id) else { return }
        selectedID = id
        onSelect?()
        view.layoutIfNeeded()
        guard let x = positions[id] else { return }
        scroll.setContentOffset(CGPoint(x: x, y: 0),
                                animated: animated && appeared && !UIAccessibility.isReduceMotionEnabled)
        updateVisiblePages()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        layingOut = true
        let size = view.bounds.size
        if size.width != previousSize.width { needsPosition = true }
        previousSize = size
        scroll.frame = view.bounds
        let width = size.width, height = size.height
        var x: CGFloat = 0
        positions = [:]
        for id in ids {
            let card = id.hasPrefix("history:")
            let pageWidth = card ? CGFloat(MusicReferenceQueueMetrics.historyCardWidth(pageWidth: Double(width))) : width
            if card, !positions.keys.contains(where: { $0.hasPrefix("history:") }) { x += 16 }
            pages[id]?.view.frame = CGRect(x: x, y: 0, width: pageWidth, height: height)
            positions[id] = max(0, x + pageWidth / 2 - width / 2)
            x += pageWidth + (card ? 12 : 0)
        }
        if ids.last?.hasPrefix("history:") == true { x += 4 }
        scroll.contentSize = CGSize(width: max(width, x), height: height)
        if needsPosition {
            needsPosition = false
            scroll.setContentOffset(CGPoint(x: positions[selectedID] ?? 0, y: 0), animated: false)
        }
        layingOut = false
        onCategoryPosition?(categoryPosition)
        updateVisiblePages()
    }

    func scrollViewDidScroll(_ scrollView: UIScrollView) {
        guard !layingOut else { return }
        onCategoryPosition?(categoryPosition)
        updateVisiblePages()
    }

    func scrollViewWillEndDragging(_ scrollView: UIScrollView, withVelocity velocity: CGPoint,
                                  targetContentOffset: UnsafeMutablePointer<CGPoint>) {
        if let id = nearest(to: targetContentOffset.pointee.x), let x = positions[id] {
            targetContentOffset.pointee = CGPoint(x: x, y: 0)
        }
    }

    func scrollViewDidEndDragging(_ scrollView: UIScrollView, willDecelerate decelerate: Bool) {
        if !decelerate { settleSelection() }
    }
    func scrollViewDidEndDecelerating(_ scrollView: UIScrollView) { settleSelection() }
    func scrollViewDidEndScrollingAnimation(_ scrollView: UIScrollView) { settleSelection() }

    private func nearest(to x: CGFloat) -> String? {
        ids.min { abs((positions[$0] ?? 0) - x) < abs((positions[$1] ?? 0) - x) }
    }

    private func settleSelection() {
        guard let id = nearest(to: scroll.contentOffset.x) else { return }
        let changed = selectedID != id
        selectedID = id
        if changed { onSelect?() }
        updateVisiblePages()
    }

    private func updateVisiblePages() {
        guard isViewLoaded, !layingOut else { return }
        let viewport = CGRect(origin: scroll.contentOffset, size: scroll.bounds.size)
        let next = appeared ? Set(ids.filter { pages[$0]?.view.frame.intersects(viewport) == true }) : []
        for id in visible.subtracting(next) {
            if let page = pages[id] { setVisible(page, false) }
        }
        for id in next.subtracting(visible) {
            if let page = pages[id] { setVisible(page, true) }
        }
        visible = next
    }

    private func setVisible(_ page: UIViewController, _ visible: Bool) {
        page.beginAppearanceTransition(visible, animated: false)
        page.endAppearanceTransition()
    }

    nonisolated deinit {
        MainActor.assumeIsolated {
            for page in pages.values { PageViewController.invalidatePages(in: page) }
        }
    }
}
#endif
