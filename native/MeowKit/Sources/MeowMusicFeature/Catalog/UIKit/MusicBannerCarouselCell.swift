#if os(iOS)
import UIKit
import MeowUI

/// UIScrollView supplies paging and deceleration; a six-second task selects
/// the next banner while the carousel is active.
@MainActor
final class MusicBannerCarouselCell: UICollectionViewCell, UIScrollViewDelegate {
    private let scroll = UIScrollView()
    private let dots = UIPageControl()
    private var items: [MusicPublicBrowseItem] = []
    private var tiles: [MusicBrowseCell] = []
    private var active = false
    private var holdUntil = Date.distantPast
    private var task: Task<Void, Never>?
    private var observers: [NSObjectProtocol] = []

    override init(frame: CGRect) {
        super.init(frame: frame)
        contentView.layer.cornerRadius = 14; contentView.clipsToBounds = true
        scroll.isPagingEnabled = true; scroll.showsHorizontalScrollIndicator = false
        scroll.delegate = self
        contentView.addSubview(scroll)
        contentView.addSubview(dots)
        dots.hidesForSinglePage = true
        dots.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.holdUntil = Date().addingTimeInterval(10)
            self.select(self.dots.currentPage)
        }, for: .valueChanged)
        for name in [UIApplication.willResignActiveNotification, UIApplication.didBecomeActiveNotification,
                     UIAccessibility.reduceMotionStatusDidChangeNotification,
                     UIAccessibility.voiceOverStatusDidChangeNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) {
                [weak self] _ in MainActor.assumeIsolated { self?.updateAutoAdvance() }
            })
        }
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(_ items: [MusicPublicBrowseItem], theme: PageTheme, open: @escaping (MusicPublicBrowseItem) -> Void) {
        let changed = self.items.map(\.id) != items.map(\.id)
        self.items = items
        if changed {
            for tile in tiles { tile.removeFromSuperview() }
            tiles = items.map { _ in
                let tile = MusicBrowseCell(frame: .zero)
                scroll.addSubview(tile)
                return tile
            }
            dots.currentPage = 0
        }
        for (tile, item) in zip(tiles, items) {
            tile.configure(item: item, layout: .banner, theme: theme, onPlay: { open(item) })
            if changed { tile.setOpenAction { open(item) } }
        }
        dots.numberOfPages = items.count
        setNeedsLayout()
        updateAutoAdvance()
    }

    func setActive(_ value: Bool) { active = value; updateAutoAdvance() }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        updateAutoAdvance()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let changed = scroll.bounds.size != contentView.bounds.size
        scroll.frame = contentView.bounds
        for (index, tile) in tiles.enumerated() {
            tile.frame = CGRect(x: CGFloat(index) * scroll.bounds.width, y: 0,
                                width: scroll.bounds.width, height: scroll.bounds.height)
        }
        scroll.contentSize = CGSize(width: CGFloat(items.count) * scroll.bounds.width, height: scroll.bounds.height)
        dots.frame = CGRect(x: 16, y: bounds.height - 28, width: bounds.width - 32, height: 26)
        if changed { scroll.contentOffset.x = CGFloat(dots.currentPage) * scroll.bounds.width }
    }

    func scrollViewWillBeginDragging(_ scrollView: UIScrollView) { holdUntil = Date().addingTimeInterval(10) }
    func scrollViewDidEndDecelerating(_ scrollView: UIScrollView) { settled() }
    func scrollViewDidEndScrollingAnimation(_ scrollView: UIScrollView) { settled() }
    func scrollViewDidEndDragging(_ scrollView: UIScrollView, willDecelerate decelerate: Bool) {
        if !decelerate { settled() }
    }

    private func settled() {
        guard scroll.bounds.width > 0 else { return }
        dots.currentPage = min(max(0, Int((scroll.contentOffset.x / scroll.bounds.width).rounded())),
                               max(0, items.count - 1))
    }

    private func select(_ index: Int) {
        scroll.setContentOffset(CGPoint(x: CGFloat(index) * scroll.bounds.width, y: 0),
                                animated: !UIAccessibility.isReduceMotionEnabled)
    }

    private func updateAutoAdvance() {
        task?.cancel(); task = nil
        guard active, window != nil, UIApplication.shared.applicationState == .active,
              !UIAccessibility.isReduceMotionEnabled, !UIAccessibility.isVoiceOverRunning,
              items.count > 1 else { return }
        task = Task { [weak self] in
            do {
                while !Task.isCancelled {
                    try await Task.sleep(for: .seconds(6))
                    guard let self, !Task.isCancelled, self.active, self.window != nil,
                          self.items.count > 1 else { return }
                    guard Date() >= self.holdUntil, !self.scroll.isDragging, !self.scroll.isDecelerating else { continue }
                    self.dots.currentPage = (self.dots.currentPage + 1) % self.items.count
                    self.select(self.dots.currentPage)
                }
            } catch { }
        }
    }

    deinit {
        task?.cancel()
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
    }
}
#endif
