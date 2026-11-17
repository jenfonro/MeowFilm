import UIKit
import Combine
import MeowUI
import MeowMusicFeature
import MeowFilmFeature
import MeowNovelFeature

@MainActor
enum MeowfilmSidebarContent {
    case music(source: MusicHomeSourceSettingsModel, selectedPage: MeowfilmPrimaryPage)
    case novel(discovery: NovelDiscoverySession, home: () -> Void)
    case film(home: FilmHomeModel, settings: FilmSettingsModel?)
}

/// One layout for all domains. Business choices stay in their existing owners.
@MainActor
final class MeowfilmSidebarView: UIView, UIGestureRecognizerDelegate {
    var onDismiss: (() -> Void)?
    var onSettings: (() -> Void)?
    var onClosed: (() -> Void)?
    var onSelectPage: ((MeowfilmPrimaryPage) -> Void)?
    var onError: ((String) -> Void)?
    private let content: MeowfilmSidebarContent
    private let shade = UIButton(type: .custom)
    private let panel: WindowGradientView
    private let scroll = UIScrollView()
    private let card = UIView()
    private let upper = UIStackView()
    private let lower = UIStackView()
    private let videos = UIStackView()
    private let books = UIStackView()
    private let settings = UIButton(type: .system)
    private var sourceButton: UIButton?
    private var buttons: [UIButton] = []
    private var videoRows: [FilmSourceKey: UIStackView] = [:]
    private var bookRows: [String: UIButton] = [:]
    private var observations: Set<AnyCancellable> = []
    private var observing = false
    private var animator: UIViewPropertyAnimator?
    private var closing = false

    init(content: MeowfilmSidebarContent, themeProvider: any PageThemeProviding) {
        self.content = content
        panel = WindowGradientView(themeProvider: themeProvider)
        super.init(frame: .zero)
        accessibilityIdentifier = "app.sidebar"
        accessibilityViewIsModal = true
        shade.backgroundColor = UIColor.black.withAlphaComponent(0.24)
        shade.accessibilityLabel = "关闭侧边栏"
        shade.addAction(UIAction { [weak self] _ in self?.onDismiss?() }, for: .touchUpInside)
        addSubview(shade)
        shade.pinEdges(to: self)
        addSubview(panel)
        panel.translatesAutoresizingMaskIntoConstraints = false
        panel.layer.cornerRadius = 24
        panel.layer.maskedCorners = [.layerMaxXMinYCorner, .layerMaxXMaxYCorner]
        NSLayoutConstraint.activate([
            panel.leadingAnchor.constraint(equalTo: leadingAnchor),
            panel.topAnchor.constraint(equalTo: topAnchor),
            panel.bottomAnchor.constraint(equalTo: bottomAnchor),
            panel.widthAnchor.constraint(equalTo: widthAnchor, multiplier: 0.8),
        ])
        configureLayout()
        configureContent()
        applyTheme()
        registerForTraitChanges([UITraitUserInterfaceStyle.self]) {
            (view: MeowfilmSidebarView, _: UITraitCollection) in view.applyTheme()
        }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (view: MeowfilmSidebarView, _: UITraitCollection) in
            for button in view.buttons { button.setNeedsUpdateConfiguration() }
        }
        let pan = UIPanGestureRecognizer(target: self, action: #selector(dragged))
        pan.delegate = self
        panel.addGestureRecognizer(pan)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    private func configureLayout() {
        scroll.accessibilityIdentifier = "app.sidebar.scroll"
        scroll.showsVerticalScrollIndicator = false
        scroll.contentInsetAdjustmentBehavior = .never
        panel.addSubview(scroll)
        scroll.translatesAutoresizingMaskIntoConstraints = false
        configureButton(settings, title: "设置", symbol: "gearshape")
        settings.configuration?.contentInsets.trailing = 38
        settings.accessibilityIdentifier = "app.sidebar.settings"
        settings.addAction(UIAction { [weak self] _ in self?.onSettings?() }, for: .touchUpInside)
        panel.addSubview(settings)
        settings.translatesAutoresizingMaskIntoConstraints = false
        let chevron = UIImageView(image: UIImage(systemName: "chevron.right"))
        chevron.preferredSymbolConfiguration = .init(pointSize: 14, weight: .regular)
        chevron.contentMode = .scaleAspectFit
        chevron.accessibilityIdentifier = "app.sidebar.settings.chevron"
        settings.addSubview(chevron)
        chevron.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            scroll.topAnchor.constraint(equalTo: panel.safeAreaLayoutGuide.topAnchor),
            scroll.bottomAnchor.constraint(equalTo: settings.topAnchor, constant: -16),
            scroll.leadingAnchor.constraint(equalTo: panel.safeAreaLayoutGuide.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: panel.safeAreaLayoutGuide.trailingAnchor),
            settings.leadingAnchor.constraint(equalTo: panel.safeAreaLayoutGuide.leadingAnchor, constant: 16),
            settings.trailingAnchor.constraint(equalTo: panel.safeAreaLayoutGuide.trailingAnchor, constant: -16),
            settings.bottomAnchor.constraint(equalTo: panel.safeAreaLayoutGuide.bottomAnchor, constant: -16),
            chevron.trailingAnchor.constraint(equalTo: settings.trailingAnchor, constant: -14),
            chevron.centerYAnchor.constraint(equalTo: settings.centerYAnchor),
            chevron.widthAnchor.constraint(equalToConstant: 12),
            chevron.heightAnchor.constraint(equalToConstant: 18),
        ])
        let column = UIView()
        scroll.addSubview(column)
        column.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            column.topAnchor.constraint(equalTo: scroll.contentLayoutGuide.topAnchor),
            column.bottomAnchor.constraint(equalTo: scroll.contentLayoutGuide.bottomAnchor),
            column.leadingAnchor.constraint(equalTo: scroll.contentLayoutGuide.leadingAnchor),
            column.trailingAnchor.constraint(equalTo: scroll.contentLayoutGuide.trailingAnchor),
        ])
        column.widthAnchor.constraint(equalTo: scroll.frameLayoutGuide.widthAnchor).isActive = true

        let icon = UIImageView(image: UIImage(named: "SidebarAppIcon"))
        icon.contentMode = .scaleAspectFit
        icon.layer.cornerRadius = 36
        icon.clipsToBounds = true
        icon.isAccessibilityElement = true
        icon.accessibilityLabel = "meowfilm"
        icon.accessibilityIdentifier = "app.sidebar.icon"
        card.accessibilityIdentifier = "app.sidebar.card"
        for stack in [upper, lower, videos, books] {
            stack.axis = .vertical
            stack.spacing = 4
        }
        upper.accessibilityIdentifier = "app.sidebar.upper"
        lower.accessibilityIdentifier = "app.sidebar.lower"
        let groups = UIStackView(arrangedSubviews: [upper, lower])
        groups.axis = .vertical
        groups.spacing = 20
        card.addSubview(groups)
        groups.pinEdges(to: card)
        for item in [icon, card] {
            column.addSubview(item)
            item.translatesAutoresizingMaskIntoConstraints = false
        }
        NSLayoutConstraint.activate([
            icon.centerXAnchor.constraint(equalTo: column.centerXAnchor),
            icon.topAnchor.constraint(equalTo: column.topAnchor, constant: 20),
            icon.widthAnchor.constraint(equalToConstant: 72),
            icon.heightAnchor.constraint(equalTo: icon.widthAnchor),
            card.topAnchor.constraint(equalTo: icon.bottomAnchor, constant: 24),
            card.leadingAnchor.constraint(equalTo: column.leadingAnchor, constant: 16),
            card.trailingAnchor.constraint(equalTo: column.trailingAnchor, constant: -16),
            card.bottomAnchor.constraint(equalTo: column.bottomAnchor, constant: -16),
        ])
    }

    private func configureContent() {
        switch content {
        case .music(_, let selected):
            upper.addArrangedSubview(navigationButton(title: "首页", symbol: "house", page: .recommend,
                                                       selected: selected == .recommend))
            upper.addArrangedSubview(navigationButton(title: "频道", symbol: "square.grid.2x2", page: .channels,
                                                       selected: selected == .channels))
            let button = UIButton(type: .system)
            configureButton(button, title: "音乐数据源", symbol: "music.note.house")
            button.accessibilityIdentifier = "app.sidebar.musicSource"
            button.showsMenuAsPrimaryAction = true
            sourceButton = button
            lower.addArrangedSubview(button)
            renderMusicSource()
        case .novel(_, let home):
            let button = UIButton(type: .system)
            configureButton(button, title: "首页", symbol: "house")
            button.accessibilityIdentifier = "app.sidebar.home"
            button.addAction(UIAction { [weak self] _ in home(); self?.onDismiss?() }, for: .touchUpInside)
            upper.addArrangedSubview(button)
            upper.addArrangedSubview(books)
            renderBooks()
            lower.isHidden = true
        case .film(let home, let model):
            let button = UIButton(type: .system)
            configureButton(button, title: "影视数据源", symbol: "film")
            button.accessibilityIdentifier = "app.sidebar.filmSource"
            button.showsMenuAsPrimaryAction = true
            sourceButton = button
            lower.addArrangedSubview(button)
            upper.addArrangedSubview(navigationButton(title: "首页", symbol: "house", page: .film, selected: true))
            upper.addArrangedSubview(videos)
            renderFilmSource(home)
            renderVideos(model?.snapshot?.homeVideos ?? [])
        }
    }

    private func configureButton(_ button: UIButton, title: String, symbol: String) {
        var configuration = UIButton.Configuration.plain()
        configuration.title = title
        configuration.titleAlignment = .leading
        configuration.titleLineBreakMode = .byWordWrapping
        configuration.subtitleLineBreakMode = .byWordWrapping
        configuration.background = .clear()
        configuration.background.cornerRadius = 28
        configuration.cornerStyle = .fixed
        configuration.contentInsets = .init(top: 14, leading: 50, bottom: 14, trailing: 14)
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { [weak button] attributes in
            var value = attributes
            value.font = UIFont.preferredFont(forTextStyle: .body, compatibleWith: button?.traitCollection)
            return value
        }
        configuration.subtitleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { [weak button] attributes in
            var value = attributes
            value.font = UIFont.preferredFont(forTextStyle: .subheadline, compatibleWith: button?.traitCollection)
            return value
        }
        button.configuration = configuration
        button.contentHorizontalAlignment = .leading
        button.titleLabel?.numberOfLines = 0
        button.titleLabel?.adjustsFontForContentSizeCategory = true
        let icon = rowIcon(symbol)
        button.addSubview(icon)
        icon.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            icon.leadingAnchor.constraint(equalTo: button.leadingAnchor, constant: 14),
            icon.centerYAnchor.constraint(equalTo: button.centerYAnchor),
        ])
        button.heightAnchor.constraint(greaterThanOrEqualToConstant: 56).isActive = true
        button.setContentHuggingPriority(.defaultHigh, for: .vertical)
        button.setContentCompressionResistancePriority(.required, for: .vertical)
        buttons.append(button)
    }

    private func rowIcon(_ symbol: String) -> UIImageView {
        let icon = UIImageView(image: UIImage(systemName: symbol)?.withAlignmentRectInsets(.zero))
        icon.preferredSymbolConfiguration = .init(pointSize: 20, weight: .regular)
        icon.contentMode = .scaleAspectFit
        icon.accessibilityIdentifier = "app.sidebar.rowIcon"
        NSLayoutConstraint.activate([
            icon.widthAnchor.constraint(equalToConstant: 24),
            icon.heightAnchor.constraint(equalToConstant: 24),
        ])
        return icon
    }

    private func displayRow(title: String, symbol: String, identifier: String) -> UIStackView {
        let label = UILabel()
        label.text = title
        label.font = .preferredFont(forTextStyle: .body)
        label.adjustsFontForContentSizeCategory = true
        label.numberOfLines = 0
        let row = UIStackView(arrangedSubviews: [rowIcon(symbol), label])
        row.alignment = .center
        row.spacing = 12
        row.isLayoutMarginsRelativeArrangement = true
        row.directionalLayoutMargins = .init(top: 14, leading: 14, bottom: 14, trailing: 14)
        row.heightAnchor.constraint(greaterThanOrEqualToConstant: 56).isActive = true
        row.setContentHuggingPriority(.defaultHigh, for: .vertical)
        row.setContentCompressionResistancePriority(.required, for: .vertical)
        row.isAccessibilityElement = true
        row.accessibilityTraits = .staticText
        row.accessibilityIdentifier = identifier
        row.accessibilityLabel = title
        return row
    }

    private func navigationButton(title: String, symbol: String, page: MeowfilmPrimaryPage, selected: Bool) -> UIButton {
        let button = UIButton(type: .system)
        configureButton(button, title: title, symbol: symbol)
        button.accessibilityIdentifier = page == .channels ? "app.sidebar.channels" : "app.sidebar.home"
        button.isSelected = selected
        button.addAction(UIAction { [weak self] _ in self?.onSelectPage?(page) }, for: .touchUpInside)
        return button
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        if window == nil { stopObserving() }
        else if !closing { startObserving() }
    }

    private func startObserving() {
        guard !observing else { return }
        observing = true
        panel.themeProvider.pageThemeChanges.receive(on: RunLoop.main)
            .sink { [weak self] in self?.applyTheme() }.store(in: &observations)
        switch content {
        case .music(let source, _):
            source.onError = { [weak self] in self?.onError?($0) }
            source.objectWillChange.receive(on: RunLoop.main)
                .sink { [weak self] in self?.renderMusicSource() }.store(in: &observations)
            source.activate()
        case .novel(let discovery, _):
            discovery.feature.$sources.receive(on: RunLoop.main)
                .sink { [weak self] _ in self?.renderBooks() }.store(in: &observations)
            discovery.$selectedSource.receive(on: RunLoop.main)
                .sink { [weak self] _ in self?.renderBooks() }.store(in: &observations)
        case .film(let home, let settings):
            home.$selectedSourceID.receive(on: RunLoop.main)
                .sink { [weak self, weak home] _ in
                    if let home { self?.renderFilmSource(home) }
                }.store(in: &observations)
            settings?.$snapshot.receive(on: RunLoop.main).sink { [weak self] snapshot in
                self?.renderVideos(snapshot?.homeVideos ?? [])
            }.store(in: &observations)
        }
    }

    private func stopObserving() {
        observing = false
        observations.removeAll()
        if case .music(let source, _) = content {
            source.onError = nil
            source.deactivate()
        }
    }

    private func renderMusicSource() {
        guard case .music(let source, _) = content, let button = sourceButton else { return }
        button.configuration?.subtitle = source.selection?.selected.title
        button.isEnabled = source.selection?.available.isEmpty == false && !source.isSaving
        button.menu = source.selection.map { selection in
            UIMenu(title: "音乐数据源", children: selection.available.map { platform in
                UIAction(title: platform.title, state: selection.selected == platform ? .on : .off) { [weak source] _ in
                    source?.select(platform)
                }
            })
        }
    }

    private func renderFilmSource(_ home: FilmHomeModel) {
        sourceButton?.configuration?.subtitle = home.sources.first { $0.id == home.selectedSourceID }?.title
        sourceButton?.menu = UIMenu(title: "影视数据源", children: home.sources.map { source in
            UIAction(title: source.title, state: source.id == home.selectedSourceID ? .on : .off) { [weak home] _ in
                home?.selectSource(id: source.id)
            }
        })
    }

    private func renderVideos(_ sources: [FilmVideoSource]) {
        videos.isHidden = sources.isEmpty
        let keys = Set(sources.map(\.key))
        for (key, row) in videoRows where !keys.contains(key) {
            row.removeFromSuperview()
            videoRows[key] = nil
        }
        for (index, source) in sources.enumerated() {
            let row: UIStackView
            if let existing = videoRows[source.key] { row = existing }
            else {
                let key = source.key
                row = displayRow(title: source.name, symbol: "play.rectangle",
                    identifier: "app.sidebar.video.\(Data(key.installationId.utf8).base64EncodedString()).\(Data(key.siteKey.utf8).base64EncodedString())")
                videoRows[key] = row
            }
            (row.arrangedSubviews.last as? UILabel)?.text = source.name
            row.accessibilityLabel = source.name
            if videos.arrangedSubviews.count <= index || videos.arrangedSubviews[index] !== row {
                videos.insertArrangedSubview(row, at: index)
            }
        }
        applyTheme()
    }

    private func renderBooks() {
        guard case .novel(let discovery, _) = content else { return }
        let sources = discovery.feature.sources
        let available = Set(discovery.availableSources.map(\.id))
        books.isHidden = sources.isEmpty
        let keys = Set(sources.map(\.id))
        for (key, row) in bookRows where !keys.contains(key) {
            row.removeFromSuperview()
            buttons.removeAll { $0 === row }
            bookRows[key] = nil
        }
        for (index, source) in sources.enumerated() {
            let row: UIButton
            if let existing = bookRows[source.id] { row = existing }
            else {
                row = UIButton(type: .system)
                configureButton(row, title: source.name, symbol: "book.closed")
                row.accessibilityIdentifier = "app.sidebar.book.\(Data(source.id.utf8).base64EncodedString())"
                row.addAction(UIAction { [weak self, weak discovery] _ in
                    guard let discovery, discovery.availableSources.contains(where: { $0.id == source.id }) else { return }
                    discovery.selectSource(id: source.id)
                    self?.onDismiss?()
                }, for: .touchUpInside)
                bookRows[source.id] = row
            }
            row.configuration?.title = source.name
            row.accessibilityLabel = source.name
            row.isEnabled = available.contains(source.id)
            row.isSelected = discovery.selectedSource?.id == source.id
            row.isAccessibilityElement = true
            row.accessibilityTraits = [.button]
            if !row.isEnabled { row.accessibilityTraits.insert(.notEnabled) }
            if row.isSelected { row.accessibilityTraits.insert(.selected) }
            row.accessibilityValue = row.isSelected ? "已选择" : nil
            if books.arrangedSubviews.count <= index || books.arrangedSubviews[index] !== row {
                books.insertArrangedSubview(row, at: index)
            }
        }
        applyTheme()
    }

    private func applyTheme() {
        let theme = panel.theme
        for button in buttons {
            button.configuration?.baseForegroundColor = UIColor(button.isSelected ? theme.accent : theme.ink)
            button.tintColor = UIColor(button.isSelected ? theme.accent : theme.ink)
            button.configuration?.background.backgroundColor = button.isSelected ? UIColor(theme.buttonSelected) : .clear
        }
        settings.configuration?.background.backgroundColor = UIColor(theme.surface)
        for stack in [upper, videos, books] {
            for row in stack.arrangedSubviews.compactMap({ $0 as? UIStackView }) {
                (row.arrangedSubviews.first as? UIImageView)?.tintColor = UIColor(theme.ink)
                (row.arrangedSubviews.last as? UILabel)?.textColor = UIColor(theme.ink)
            }
        }
    }

    func show() {
        layoutIfNeeded()
        panel.transform = CGAffineTransform(translationX: -panel.bounds.width, y: 0)
        shade.alpha = 0
        let value = UIViewPropertyAnimator(duration: UIAccessibility.isReduceMotionEnabled ? 0 : 0.28,
                                           curve: .easeOut) {
            self.panel.transform = .identity
            self.shade.alpha = 1
            self.panel.setNeedsLayout()
            self.panel.layoutIfNeeded()
        }
        animator = value
        value.addCompletion { [weak self, weak value] _ in
            if self?.animator === value { self?.animator = nil }
        }
        value.startAnimation()
    }

    func hide(completion: @escaping () -> Void) {
        guard !closing else { return }
        closing = true
        stopObserving()
        isUserInteractionEnabled = false
        if let value = animator {
            animator = nil
            value.stopAnimation(false)
            value.finishAnimation(at: .current)
        }
        let value = closingAnimator()
        animator = value
        value.addCompletion { [weak self] _ in self?.animator = nil; completion() }
        value.startAnimation()
    }

    private func closingAnimator() -> UIViewPropertyAnimator {
        UIViewPropertyAnimator(duration: UIAccessibility.isReduceMotionEnabled ? 0 : 0.25,
                               curve: .easeInOut) {
            self.panel.transform = CGAffineTransform(translationX: -self.panel.bounds.width, y: 0)
            self.shade.alpha = 0
            self.panel.setNeedsLayout()
            self.panel.layoutIfNeeded()
        }
    }

    override func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard animator == nil, !closing, let pan = gestureRecognizer as? UIPanGestureRecognizer else { return false }
        let velocity = pan.velocity(in: self)
        return velocity.x < -abs(velocity.y)
    }

    @objc private func dragged(_ pan: UIPanGestureRecognizer) {
        switch pan.state {
        case .began:
            let value = closingAnimator()
            animator = value
            value.addCompletion { [weak self] position in
                guard let self else { return }
                self.animator = nil
                if position == .end { self.onClosed?() }
            }
            value.startAnimation()
            value.pauseAnimation()
        case .changed:
            animator?.fractionComplete = min(1, max(0, -pan.translation(in: self).x /
                                                     max(1, panel.bounds.width)))
        case .ended, .cancelled:
            let commit = pan.state == .ended &&
                ((animator?.fractionComplete ?? 0) > 0.35 || pan.velocity(in: self).x < -600)
            closing = commit
            if commit { stopObserving(); isUserInteractionEnabled = false }
            animator?.isReversed = !commit
            animator?.continueAnimation(withTimingParameters: nil, durationFactor: 1)
        default:
            break
        }
    }
}
