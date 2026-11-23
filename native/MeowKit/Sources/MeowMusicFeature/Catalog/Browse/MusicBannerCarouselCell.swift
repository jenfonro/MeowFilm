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
    private var open: ((MusicPublicBrowseItem) -> Void)?
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
        dots.backgroundStyle = .minimal
        dots.currentPageIndicatorTintColor = .white
        dots.pageIndicatorTintColor = UIColor.white.withAlphaComponent(0.4)
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

    func configure(_ items: [MusicPublicBrowseItem], theme: PageTheme, images: MusicArtworkImages, open: @escaping (MusicPublicBrowseItem) -> Void) {
        let changed = self.items.map(\.id) != items.map(\.id)
        self.items = items
        self.open = open
        while tiles.count < items.count {
            let index = tiles.count
            let tile = MusicBrowseCell(frame: .zero)
            tile.setOpenAction { [weak self] in
                guard let self, self.items.indices.contains(index) else { return }
                self.open?(self.items[index])
            }
            scroll.addSubview(tile)
            tiles.append(tile)
        }
        if changed {
            dots.currentPage = 0
            scroll.contentOffset.x = 0
        }
        for (index, tile) in tiles.enumerated() {
            guard items.indices.contains(index) else {
                tile.isHidden = true
                tile.setDisplayVisible(false)
                tile.resetPressFeedback()
                continue
            }
            let item = items[index]
            tile.isHidden = false
            tile.configure(item: item, layout: .banner, theme: theme, images: images, onPlay: { [weak self] in
                guard let self, self.items.indices.contains(index) else { return }
                self.open?(self.items[index])
            })
            tile.setDisplayVisible(active)
        }
        dots.numberOfPages = items.count
        setNeedsLayout()
        updateAutoAdvance()
    }

    override func prepareForReuse() {
        super.prepareForReuse()
        setActive(false)
        for tile in tiles { tile.resetPressFeedback() }
    }

    func setActive(_ value: Bool) {
        active = value
        for (index, tile) in tiles.enumerated() {
            tile.setDisplayVisible(value && items.indices.contains(index))
        }
        updateAutoAdvance()
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        updateAutoAdvance()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let changed = scroll.bounds.size != contentView.bounds.size
        scroll.frame = contentView.bounds
        for index in items.indices where tiles.indices.contains(index) {
            tiles[index].frame = CGRect(x: CGFloat(index) * scroll.bounds.width, y: 0,
                                        width: scroll.bounds.width, height: scroll.bounds.height)
        }
        scroll.contentSize = CGSize(width: CGFloat(items.count) * scroll.bounds.width, height: scroll.bounds.height)
        let dotWidth = min(max(0, bounds.width - 32), dots.size(forNumberOfPages: items.count).width)
        dots.frame = CGRect(x: bounds.width - 12 - dotWidth, y: bounds.height - 27, width: dotWidth, height: 26)
        let trailingInset = items.count > 1 ? 12 + dotWidth + 8 : 14
        for index in items.indices where tiles.indices.contains(index) {
            tiles[index].bannerTrailingInset = trailingInset
        }
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
