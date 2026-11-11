#if os(iOS)
import UIKit
import ImageIO
import MeowUI

@MainActor
public final class NovelReaderViewController: PageViewController, UIScrollViewDelegate, UIGestureRecognizerDelegate {
    public override var usesPageHeader: Bool { false }
    public let session: NovelReadingSession
    private let scroll = UIScrollView(), canvas = NovelReaderCanvas(), backdrop = UIImageView()
    private let controls = UIView(), top = UIStackView(), bottom = UIStackView()
    private let position = UILabel(), progress = UISlider(), purchase = UIButton(type: .system), retryImages = UIButton(type: .system)
    private var pages: [NovelReaderPage] = []
    private var currentPage = 0
    private var renderedContent: NovelContent?, renderedSettings: NovelReadingSettings?, renderedSize = CGSize.zero
    private var renderedChapter = ""
    private var imageSizes: [Int: CGSize] = [:], images: [Int: UIImage] = [:]
    private var imageTasks: [Int: Task<Void, Never>] = [:], runningImages: [UUID: Task<Void, Never>] = [:]
    private var imageTaskIDs: [Int: UUID] = [:], imageFailures: Set<Int> = []
    private var imageGeneration = UUID(), layoutScheduled = false, positioning = false, menuShown = true, animating = false
    private var displayLink: CADisplayLink?, clock = NovelReaderClock(), lastTick: CFTimeInterval = 0, elapsed: CFTimeInterval = 0
    private var closing: Task<Void, Never>?
    public init(session: NovelReadingSession, themeProvider: any PageThemeProviding) {
        self.session = session; super.init(themeProvider: themeProvider)
    }
    public override func loadView() { view = UIView() }
    public override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.reader"
        backdrop.contentMode = .scaleAspectFill; backdrop.clipsToBounds = true
        backdrop.isUserInteractionEnabled = false; view.addSubview(backdrop); backdrop.pinEdges(to: view)
        scroll.delegate = self; scroll.contentInsetAdjustmentBehavior = .never
        scroll.showsVerticalScrollIndicator = false; scroll.isDirectionalLockEnabled = true
        canvas.backgroundColor = .clear; canvas.isOpaque = false
        view.addSubview(scroll); scroll.translatesAutoresizingMaskIntoConstraints = false; scroll.addSubview(canvas)
        NSLayoutConstraint.activate([
            scroll.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8),
            scroll.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -28),
            scroll.leadingAnchor.constraint(equalTo: view.leadingAnchor), scroll.trailingAnchor.constraint(equalTo: view.trailingAnchor)])
        let tap = UITapGestureRecognizer(target: self, action: #selector(tapped)); tap.delegate = self; scroll.addGestureRecognizer(tap)
        let pan = UIPanGestureRecognizer(target: self, action: #selector(panned)); pan.delegate = self; scroll.addGestureRecognizer(pan)
        configureControls()
        clock.onTick = { [weak self] in self?.tick($0) }
        observe(session) { [weak self] in self?.scheduleRender() }
        observe(session.feature) { [weak self] in self?.scheduleRender() }
        session.start()
    }
    private func configureControls() {
        controls.backgroundColor = UIColor.secondarySystemBackground.withAlphaComponent(0.96)
        controls.layer.cornerRadius = 16
        top.axis = .horizontal; top.distribution = .fillEqually
        bottom.axis = .horizontal; bottom.distribution = .fillEqually
        top.addArrangedSubview(button("返回", id: "novel.reader.back") { [weak self] in self?.navigationController?.popViewController(animated: true) })
        top.addArrangedSubview(button("目录", id: "novel.reader.catalog") { [weak self] in self?.showCatalog() })
        top.addArrangedSubview(button("换源", id: "novel.reader.sources") { [weak self] in self?.showSources() })
        top.addArrangedSubview(button("排版", id: "novel.reader.settings") { [weak self] in self?.showAppearance() })
        bottom.addArrangedSubview(button("上一章", id: "novel.reader.previous") { [weak self] in guard let self else { return }; session.jump(session.chapterIndex - 1) })
        bottom.addArrangedSubview(button("自动阅读", id: "novel.reader.automatic") { [weak self] in
            guard let self else { return }; session.automatic.toggle(); menuShown = false; updateMenu()
        })
        bottom.addArrangedSubview(button("下一章", id: "novel.reader.next") { [weak self] in guard let self else { return }; session.jump(session.chapterIndex + 1) })
        purchase.setTitle("购买本章", for: .normal); purchase.accessibilityIdentifier = "novel.reader.purchase"
        purchase.addAction(UIAction { [weak self] _ in self?.confirmPurchase() }, for: .touchUpInside)
        retryImages.setTitle("重试图片", for: .normal); retryImages.accessibilityIdentifier = "novel.reader.retryImages"
        retryImages.addAction(UIAction { [weak self] _ in
            self?.imageFailures.removeAll(); self?.retryImages.isHidden = true; self?.loadVisibleImages()
        }, for: .touchUpInside)
        progress.accessibilityIdentifier = "novel.reader.progress"
        progress.addAction(UIAction { [weak self] _ in
            guard let self else { return }; session.automatic = false
            let ratio = Double(progress.value)
            if settings.mode == .scroll {
                scroll.setContentOffset(CGPoint(x: 0, y: CGFloat(ratio) * max(0, scroll.contentSize.height - scroll.bounds.height)), animated: false)
                publishAnchor()
            } else { showPage(Int((ratio * Double(max(0, pages.count - 1))).rounded()), animated: false) }
        }, for: .valueChanged)
        position.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular); position.textAlignment = .center
        position.accessibilityIdentifier = "novel.reader.position"
        let stack = UIStackView(arrangedSubviews: [top, progress, bottom, purchase, retryImages]); stack.axis = .vertical; stack.spacing = 8
        controls.addSubview(stack); stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(controls); controls.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(position); position.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            controls.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 12),
            controls.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -12),
            controls.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -32),
            stack.topAnchor.constraint(equalTo: controls.topAnchor, constant: 12), stack.bottomAnchor.constraint(equalTo: controls.bottomAnchor, constant: -12),
            stack.leadingAnchor.constraint(equalTo: controls.leadingAnchor, constant: 8), stack.trailingAnchor.constraint(equalTo: controls.trailingAnchor, constant: -8),
            top.heightAnchor.constraint(equalToConstant: 44), bottom.heightAnchor.constraint(equalToConstant: 44),
            position.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16), position.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16),
            position.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -4), position.heightAnchor.constraint(equalToConstant: 20)])
        for control in [purchase, retryImages] {
            let height = control.heightAnchor.constraint(equalToConstant: 44); height.priority = .init(999); height.isActive = true
        }
    }
    private func button(_ title: String, id: String, action: @escaping () -> Void) -> UIButton {
        let button = UIButton(type: .system); button.setTitle(title, for: .normal); button.accessibilityIdentifier = id
        button.addAction(UIAction { _ in action() }, for: .touchUpInside); return button
    }
    private var settings: NovelReadingSettings { session.feature.local.settings }
    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        if renderedSize != scroll.bounds.size { render(preserving: session.anchor) }
    }
    public override func pageDidBecomeVisible() { session.resume(); startClock(); scheduleRender() }
    public override func pageDidFinishAppearing() { scheduleRender() }
    public override func pageDidBecomeHidden() {
        // A Web sheet is part of the current source invocation. Hiding under
        // that owned interaction must not cancel the request that presented it.
        session.pauseAutomatic()
        if navigationController?.topViewController !== self || UIApplication.shared.applicationState != .active { session.suspend() }
        stopClock(); cancelImages()
    }
    public override func pageDidBecomeInactive() {
        session.suspend(); stopClock(); cancelImages()
        closing = Task { [session, tasks = Array(runningImages.values)] in
            for task in tasks { await task.value }; await session.close()
        }
    }
    public override func shouldBeginInteractiveBack(at point: CGPoint, in view: UIView) -> Bool {
        !animating && (settings.mode == .scroll || currentPage == 0)
    }
    private func scheduleRender() {
        guard !layoutScheduled else { return }; layoutScheduled = true
        Task { @MainActor [weak self] in
            guard let self else { return }; layoutScheduled = false
            guard isViewLoaded else { return }; render(preserving: nil)
        }
    }
    private func render(preserving override: NovelAnchor?) {
        guard scroll.bounds.width > 0, scroll.bounds.height > 0 else { return }
        if let candidate = session.sourceConfirmation, canPresentPageAlert {
            let alert = UIAlertController(title: "确认换源",
                message: "\(session.book.name) · \(session.book.author.isEmpty ? "作者未知" : session.book.author)\n\(candidate.name) · \(candidate.author.isEmpty ? "作者未知" : candidate.author)",
                preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "取消", style: .cancel) { [weak session] _ in session?.cancelSourceConfirmation() })
            alert.addAction(UIAlertAction(title: "确认换源", style: .default) { [weak session] _ in session?.confirmUnknownSource() })
            present(alert, animated: true)
        }
        if let selection = session.sourceSelection, presentedViewController == nil,
           navigationController?.topViewController === self {
            let page = NovelChapterPicker(chapters: selection.chapters, selected: 0, title: "选择对应章节", themeProvider: themeProvider) { [weak session] in session?.selectSourceChapter($0) }
            page.onCancel = { [weak session] in session?.cancelSourceSelection() }
            navigationController?.pushViewController(page, animated: true)
        }
        if let error = session.error { if showError(error) { session.error = nil } }
        if let error = session.feature.error { if showError(error) { session.feature.error = nil } }
        guard let content = session.content else { position.text = session.loading ? "加载中" : ""; return }
        let chapterKey = session.book.installationId + "\n" + (session.anchor?.chapterUrl ?? "")
        let changed = renderedContent != content || renderedChapter != chapterKey
        let relayout = changed || renderedSettings != settings || renderedSize != scroll.bounds.size || override != nil
        let anchor = override ?? session.anchor ?? currentAnchor()
        if changed { cancelImages(); images.removeAll(); imageSizes.removeAll(); imageFailures.removeAll() }
        renderedContent = content; renderedChapter = chapterKey
        if relayout {
            positioning = true
            renderedSettings = settings; renderedSize = scroll.bounds.size
            pages = NovelReaderLayout.pages(content: content, settings: settings, size: scroll.bounds.size, imageSizes: imageSizes)
            scroll.isScrollEnabled = settings.mode == .scroll
            currentPage = anchor.map { NovelReaderLayout.page(for: $0, pages: pages) } ?? 0
            currentPage = min(max(0, currentPage), max(0, pages.count - 1))
            if settings.mode == .scroll {
                canvas.page = pages[0]; scroll.contentSize = CGSize(width: scroll.bounds.width, height: max(scroll.bounds.height, pages[0].height))
                let y = anchor.map { NovelReaderLayout.y(for: $0, page: pages[0]) } ?? 0
                scroll.contentOffset = CGPoint(x: 0, y: min(max(0, y), max(0, scroll.contentSize.height - scroll.bounds.height)))
            } else { canvas.page = pages[currentPage]; scroll.contentSize = scroll.bounds.size; scroll.contentOffset = .zero }
            positioning = false
        }
        let palette = NovelReadingSettings.themes[min(max(0, settings.theme), 7)]
        view.backgroundColor = UIColor(novelHex: settings.background.isEmpty ? palette.background : settings.background)
        position.textColor = UIColor(novelHex: settings.foreground.isEmpty ? palette.foreground : settings.foreground)
        backdrop.image = settings.backgroundImage.flatMap { UIImage(data: $0) }
        purchase.isHidden = session.purchasableChapter == nil
        retryImages.isHidden = imageFailures.isEmpty
        updateViewport(); updateMenu()
        // A reflow places the durable character/image within the viewport.
        // It is not user movement to the newly wrapped line's earlier start.
        if isPageVisible, let value = anchor ?? currentAnchor() { session.display(value) }
    }
    private func updateViewport() {
        canvas.frame = CGRect(x: 0, y: scroll.contentOffset.y, width: scroll.bounds.width, height: scroll.bounds.height)
        canvas.viewportY = scroll.contentOffset.y; canvas.images = images
        let ratio = settings.mode == .scroll ? Double(scroll.contentOffset.y / max(1, scroll.contentSize.height - scroll.bounds.height)) : Double(currentPage) / Double(max(1, pages.count - 1))
        progress.value = Float(min(1, max(0, ratio)))
        position.text = "\(session.content?.title ?? "")  \(settings.mode == .scroll ? "\(Int(ratio * 100))%" : "\(currentPage + 1)/\(max(1, pages.count))")"
        loadVisibleImages()
    }
    private func currentAnchor() -> NovelAnchor? {
        guard session.chapters.indices.contains(session.chapterIndex), !pages.isEmpty else { return session.anchor }
        let mode = renderedSettings?.mode ?? settings.mode
        let page = pages[mode == .scroll ? 0 : min(currentPage, pages.count - 1)]
        return NovelReaderLayout.anchor(at: mode == .scroll ? scroll.contentOffset.y : 0, page: page, chapter: session.chapters[session.chapterIndex])
    }
    private func publishAnchor() {
        guard !positioning, isPageVisible, let anchor = currentAnchor() else { return }; session.display(anchor)
    }
    private func showPage(_ index: Int, animated: Bool) {
        guard !session.loading, !animating else { return }
        if index < 0 { session.jump(session.chapterIndex - 1, ratio: 1); return }
        if index >= pages.count {
            if session.chapterIndex + 1 >= session.chapters.count { session.automatic = false; return }
            session.jump(session.chapterIndex + 1, continuingAutomatic: session.automatic); return
        }
        let previous = currentPage; currentPage = index
        let snapshot = animated && settings.mode != .instant ? canvas.snapshotView(afterScreenUpdates: false) : nil
        canvas.page = pages[index]; updateViewport(); publishAnchor()
        guard let snapshot, previous != index else { return }
        let forward = index > previous, width = scroll.bounds.width
        snapshot.frame = canvas.frame; scroll.addSubview(snapshot); animating = true
        if settings.mode == .slide { canvas.transform = CGAffineTransform(translationX: forward ? width : -width, y: 0) }
        UIView.animate(withDuration: UIAccessibility.isReduceMotionEnabled ? 0 : 0.22, animations: {
            snapshot.transform = CGAffineTransform(translationX: forward ? -width : width, y: 0); self.canvas.transform = .identity
        }, completion: { [weak self] _ in snapshot.removeFromSuperview(); self?.animating = false })
    }
    @objc private func tapped(_ tap: UITapGestureRecognizer) {
        guard !session.loading else { return }
        session.automatic = false; let x = tap.location(in: scroll).x / max(1, scroll.bounds.width)
        if x > 0.33 && x < 0.67 { menuShown.toggle(); updateMenu() }
        else if settings.mode == .scroll {
            let delta = scroll.bounds.height * 0.85 * (x < 0.33 ? -1 : 1)
            scroll.setContentOffset(CGPoint(x: 0, y: min(max(0, scroll.contentOffset.y + delta), max(0, scroll.contentSize.height - scroll.bounds.height))), animated: true)
        } else { showPage(currentPage + (x < 0.33 ? -1 : 1), animated: true) }
    }
    @objc private func panned(_ pan: UIPanGestureRecognizer) {
        session.automatic = false
        guard settings.mode != .scroll, pan.state == .ended else { return }
        let delta = pan.translation(in: scroll).x, speed = pan.velocity(in: scroll).x
        guard abs(delta) > scroll.bounds.width * 0.18 || abs(speed) > 400 else { return }
        showPage(currentPage + (delta < 0 ? 1 : -1), animated: true)
    }
    public func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        if let pan = gestureRecognizer as? UIPanGestureRecognizer {
            let location = pan.location(in: scroll), velocity = pan.velocity(in: scroll)
            return settings.mode != .scroll && location.x > 24 && abs(velocity.x) > abs(velocity.y)
        }
        return true
    }
    public func scrollViewDidScroll(_ scrollView: UIScrollView) { guard !positioning else { return }; updateViewport(); publishAnchor() }
    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) { session.automatic = false; menuShown = false; updateMenu() }
    public func scrollViewDidEndDragging(_ scrollView: UIScrollView, willDecelerate decelerate: Bool) { if !decelerate { crossScrollChapter() } }
    public func scrollViewDidEndDecelerating(_ scrollView: UIScrollView) { crossScrollChapter() }
    private func crossScrollChapter() {
        guard settings.mode == .scroll else { return }
        if scroll.contentOffset.y > scroll.contentSize.height - scroll.bounds.height + 32 { session.jump(session.chapterIndex + 1) }
        else if scroll.contentOffset.y < -32 { session.jump(session.chapterIndex - 1, ratio: 1) }
    }
    private func loadVisibleImages() {
        guard isPageVisible, !session.closed, let content = session.content, !pages.isEmpty else { return }
        guard let source = session.contentSource, session.feature.accepts(source) else { cancelImages(); return }
        let page = pages[settings.mode == .scroll ? 0 : currentPage]
        let viewport = CGRect(x: 0, y: scroll.contentOffset.y - scroll.bounds.height, width: scroll.bounds.width, height: scroll.bounds.height * 3)
        let wanted = Set(page.elements.filter { $0.range == nil && $0.frame.intersects(viewport) }.map(\.blockIndex))
        for (index, task) in imageTasks where !wanted.contains(index) { task.cancel(); imageTasks[index] = nil; imageTaskIDs[index] = nil }
        for index in Array(images.keys) where !wanted.contains(index) { images[index] = nil }
        for index in wanted where imageTasks[index] == nil && images[index] == nil && !imageFailures.contains(index) {
            guard content.blocks.indices.contains(index), let url = content.blocks[index].src else { continue }
            let token = imageGeneration, taskID = UUID(), book = session.book, client = session.feature.sourcesClient
            imageTaskIDs[index] = taskID
            imageTasks[index] = Task { [weak self, session] in
                defer {
                    session.imageTaskFinished(taskID)
                    self?.runningImages[taskID] = nil
                    if self?.imageTaskIDs[index] == taskID { self?.imageTasks[index] = nil; self?.imageTaskIDs[index] = nil }
                }
                do {
                    let bytes = try await client.readImage(source: source, book: book, url: url, kind: "content")
                    try Task.checkCancellation()
                    let decoded = await Task.detached(priority: .userInitiated) { () -> UIImage? in
                        guard let input = CGImageSourceCreateWithData(bytes as CFData, nil),
                              let image = CGImageSourceCreateImageAtIndex(input, 0, [kCGImageSourceShouldCacheImmediately: true] as CFDictionary) else { return nil }
                        return UIImage(cgImage: image)
                    }.value
                    guard let self, !Task.isCancelled, imageGeneration == token, session.feature.accepts(source) else { return }
                    guard let decoded else { throw NovelError.invalidResponse }
                    let anchor = session.anchor ?? currentAnchor(); images[index] = decoded; imageTasks[index] = nil
                    if imageSizes[index] != decoded.size {
                        imageSizes[index] = decoded.size; renderedSize = .zero; render(preserving: anchor)
                    } else { canvas.images = images }
                } catch {
                    guard let self else { return }
                    if (error as? NovelError) == .imageReleaseFailed { session.feature.error = error.localizedDescription }
                    guard imageGeneration == token, !Task.isCancelled else { return }
                    imageFailures.insert(index); retryImages.isHidden = false
                    showError("正文图片加载失败：" + error.localizedDescription)
                }
            }
            runningImages[taskID] = imageTasks[index]
            if let task = imageTasks[index] { session.ownImageTask(task, id: taskID) }
        }
        canvas.images = images
    }
    private func cancelImages() {
        imageGeneration = UUID()
        for task in imageTasks.values { task.cancel() }
        imageTasks.removeAll(); imageTaskIDs.removeAll(); images.removeAll(); canvas.images = [:]
    }
    private func updateMenu() { controls.isHidden = !menuShown; if menuShown, session.automatic { session.automatic = false } }
    private func showCatalog() {
        session.suspend()
        let page = NovelChapterPicker(chapters: session.chapters, selected: session.chapterIndex, title: "目录", themeProvider: themeProvider,
            refresh: { [weak session] in session?.refreshCatalog() }) { [weak session] in session?.jump($0) }
        navigationController?.pushViewController(page, animated: true)
    }
    private func showAppearance() {
        session.suspend(); navigationController?.pushViewController(NovelAppearanceViewController(feature: session.feature, themeProvider: themeProvider), animated: true)
    }
    private func showSources() {
        session.suspend()
        let page = NovelSourceSwitchViewController(session: session, themeProvider: themeProvider)
        navigationController?.pushViewController(page, animated: true)
    }
    private func confirmPurchase() {
        guard let chapter = session.purchasableChapter, let source = try? session.feature.source(for: session.book) else { return }
        session.suspend()
        let book = session.book
        let confirmation = NovelPurchaseConfirmationViewController(book: book.name, source: source.name,
            chapter: chapter.name, themeProvider: themeProvider) { [weak session] in session?.confirmPurchase(chapter: chapter, bookID: book.id) }
        confirmation.onCancel = { [weak self] in
            guard let self, isPageVisible, UIApplication.shared.applicationState == .active else { return }
            session.resume()
        }
        present(confirmation, animated: true)
    }
    private func startClock() {
        guard displayLink == nil else { return }; lastTick = 0; elapsed = 0
        let link = CADisplayLink(target: clock, selector: #selector(NovelReaderClock.tick(_:))); link.add(to: .main, forMode: .common); displayLink = link
    }
    private func stopClock() { displayLink?.invalidate(); displayLink = nil; lastTick = 0; elapsed = 0 }
    private func tick(_ link: CADisplayLink) {
        let delta = lastTick == 0 ? 0 : link.timestamp - lastTick; lastTick = link.timestamp
        guard session.automatic, isPageVisible, !session.loading, !menuShown, !animating else { elapsed = 0; return }
        if settings.mode == .scroll {
            let maxY = max(0, scroll.contentSize.height - scroll.bounds.height)
            if scroll.contentOffset.y >= maxY {
                if session.chapterIndex + 1 < session.chapters.count { session.jump(session.chapterIndex + 1, continuingAutomatic: true) } else { session.automatic = false }
            } else {
                scroll.setContentOffset(CGPoint(x: 0, y: min(maxY, scroll.contentOffset.y + CGFloat(delta * (12 + settings.automaticSpeed * 12)))), animated: false)
            }
        } else {
            elapsed += delta
            if elapsed >= max(3.5, 26 - settings.automaticSpeed * 2.25) { elapsed = 0; showPage(currentPage + 1, animated: true) }
        }
    }
    deinit { displayLink?.invalidate(); for task in imageTasks.values { task.cancel() } }
}

@MainActor
final class NovelPurchaseConfirmationViewController: PageViewController {
    var onCancel: (() -> Void)?
    private let book: String, source: String, chapter: String
    private var confirm: (() -> Void)?
    init(book: String, source: String, chapter: String, themeProvider: any PageThemeProviding, confirm: @escaping () -> Void) {
        self.book = book; self.source = source; self.chapter = chapter; self.confirm = confirm
        super.init(themeProvider: themeProvider); pageTitle = "购买本章"; modalPresentationStyle = .pageSheet
        sheetPresentationController?.detents = [.medium(), .large()]
    }
    override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.purchase"
        let labels = [book, source, chapter].map { text in
            let label = UILabel(); label.text = text; label.numberOfLines = 0
            label.font = .preferredFont(forTextStyle: .body); label.textColor = UIColor(theme.ink); return label
        }
        let cancel = UIButton(type: .system), purchase = UIButton(type: .system)
        cancel.setTitle("取消", for: .normal); cancel.accessibilityIdentifier = "novel.purchase.cancel"
        purchase.setTitle("确认购买", for: .normal); purchase.accessibilityIdentifier = "novel.purchase.confirm"
        cancel.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            confirm = nil; let cancelled = onCancel; onCancel = nil
            dismiss(animated: true, completion: cancelled)
        }, for: .touchUpInside)
        purchase.addAction(UIAction { [weak self] _ in
            guard let self, let action = confirm else { return }; confirm = nil; onCancel = nil
            dismiss(animated: true, completion: action)
        }, for: .touchUpInside)
        let buttons = UIStackView(arrangedSubviews: [cancel, purchase]); buttons.distribution = .fillEqually
        let stack = UIStackView(arrangedSubviews: (labels as [UIView]) + [buttons]); stack.axis = .vertical; stack.spacing = 20
        view.addSubview(stack); stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: contentTopAnchor, constant: 24),
            stack.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -24),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -16),
            buttons.heightAnchor.constraint(equalToConstant: 48)])
    }
    override func pageDidBecomeInactive() {
        if confirm != nil { onCancel?() }
        confirm = nil; onCancel = nil
    }
}
@MainActor
private final class NovelReaderClock: NSObject {
    var onTick: ((CADisplayLink) -> Void)?
    @objc func tick(_ link: CADisplayLink) { onTick?(link) }
}

@MainActor
final class NovelSourceSwitchViewController: PageViewController {
    private let session: NovelReadingSession
    private let content: NovelSearchResultsContent
    init(session: NovelReadingSession, themeProvider: any PageThemeProviding) {
        self.session = session
        content = NovelSearchResultsContent(feature: session.feature, theme: PageTheme(colors: themeProvider.pagePalette), matchingBook: session.book)
        super.init(themeProvider: themeProvider); pageTitle = "换源"
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        addChild(content); view.addSubview(content.view); content.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([content.view.topAnchor.constraint(equalTo: contentTopAnchor), content.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            content.view.leadingAnchor.constraint(equalTo: view.leadingAnchor), content.view.trailingAnchor.constraint(equalTo: view.trailingAnchor)])
        content.didMove(toParent: self)
        content.onSelect = { [weak self] book in
            guard let self else { return }; session.switchSource(book); navigationController?.popViewController(animated: true)
        }
    }
    override func pageDidBecomeActive() {
        content.submit(session.book.name)
    }
    override func pageDidBecomeHidden() { content.cancel() }
    override func pageDidBecomeInactive() { content.close() }
}
#endif
