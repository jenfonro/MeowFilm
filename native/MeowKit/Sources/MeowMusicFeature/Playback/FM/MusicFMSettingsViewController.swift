#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicFMSettingsViewController: PageViewController {
    private let owner: MusicViewModel
    let model: MusicFMSettingsViewModel
    private let account: UUID
    private let closeOnFMDeactivation: Bool
    private let transition = VerticalPageTransition(edge: .top)
    private var closeQueued = false
    private var icons: [String: UIImage] = [:]
    private let scrollView = UIScrollView()
    private let contentStack = UIStackView()
    private let sceneHeader = UILabel()
    private var choiceButtons: [String: MusicFMChoiceButton] = [:]
    var onSelected: @MainActor () -> Void

    public init(
        model: MusicViewModel,
        appearance: MusicAppearancePreferencesStore,
        onSelected: @escaping @MainActor () -> Void = {}
    ) {
        owner = model
        account = model.searchAccessGeneration
        let activeAtOpen = model.isFMActive
        closeOnFMDeactivation = activeAtOpen
        self.model = MusicFMSettingsViewModel(owner: model, requireActiveFM: activeAtOpen)
        self.onSelected = onSelected
        super.init(themeProvider: appearance)
        pageTitle = MusicFMModeChoice.title(for: model.activeHomeFM)
        overrideUserInterfaceStyle = .light
        modalPresentationStyle = .custom
        transitioningDelegate = transition
        transition.onTransitionEnded = { [weak self] presenting, completed in
            if presenting || !completed { self?.drainClose() }
        }
    }

    public override func loadView() {
        view = MusicPlayerBackgroundView(style: .full, palette: themeProvider.pagePalette)
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "music.fm.settings"
        pageHeader.titleFont = .systemFont(ofSize: 15)
        pageHeader.background.isHidden = true
        transition.installEdgeDismissal(on: self)
        icons = Dictionary(uniqueKeysWithValues: MusicFMModeChoice.scenes.map {
            ($0.submode, MusicFMSceneIcon.image(code: $0.submode))
        })
        pageHeader.setLeading(PageHeaderButton(
            image: PageIcon.image(.close, size: 18), accessibilityLabel: "关闭"
        ) { [weak self] in self?.close() })

        scrollView.backgroundColor = .clear
        scrollView.alwaysBounceVertical = true
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(scrollView)
        NSLayoutConstraint.activate([
            scrollView.topAnchor.constraint(equalTo: contentTopAnchor),
            scrollView.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            scrollView.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            scrollView.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        scrollView.panGestureRecognizer.require(toFail: transition.edgeGesture)

        contentStack.axis = .vertical
        contentStack.spacing = 10
        contentStack.isLayoutMarginsRelativeArrangement = true
        contentStack.directionalLayoutMargins = NSDirectionalEdgeInsets(top: 14, leading: 14, bottom: 24, trailing: 14)
        contentStack.translatesAutoresizingMaskIntoConstraints = false
        scrollView.addSubview(contentStack)
        NSLayoutConstraint.activate([
            contentStack.topAnchor.constraint(equalTo: scrollView.contentLayoutGuide.topAnchor),
            contentStack.bottomAnchor.constraint(equalTo: scrollView.contentLayoutGuide.bottomAnchor),
            contentStack.leadingAnchor.constraint(equalTo: scrollView.contentLayoutGuide.leadingAnchor),
            contentStack.trailingAnchor.constraint(equalTo: scrollView.contentLayoutGuide.trailingAnchor),
            contentStack.widthAnchor.constraint(equalTo: scrollView.frameLayoutGuide.widthAnchor),
        ])

        for choice in MusicFMModeChoice.main {
            let button = MusicFMChoiceButton(choice: choice, scene: false, icon: nil) { [weak self] in
                self?.model.select(choice)
            }
            choiceButtons[choice.id] = button
            contentStack.addArrangedSubview(button)
        }

        sceneHeader.text = "场景模式"
        sceneHeader.font = .preferredFont(forTextStyle: .subheadline)
        sceneHeader.adjustsFontForContentSizeCategory = true
        contentStack.addArrangedSubview(sceneHeader)
        contentStack.setCustomSpacing(10, after: sceneHeader)
        if let lastMain = contentStack.arrangedSubviews.dropLast().last {
            contentStack.setCustomSpacing(20, after: lastMain)
        }

        let scenes = MusicFMModeChoice.scenes
        let columns = 4
        var index = 0
        while index < scenes.count {
            let row = UIStackView()
            row.axis = .horizontal
            row.distribution = .fillEqually
            row.spacing = 4
            for col in 0..<columns {
                if index + col < scenes.count {
                    let choice = scenes[index + col]
                    let button = MusicFMChoiceButton(choice: choice, scene: true, icon: icons[choice.submode]) { [weak self] in
                        self?.model.select(choice)
                    }
                    choiceButtons[choice.id] = button
                    row.addArrangedSubview(button)
                } else {
                    let spacer = UIView()
                    spacer.isUserInteractionEnabled = false
                    row.addArrangedSubview(spacer)
                }
            }
            contentStack.addArrangedSubview(row)
            contentStack.setCustomSpacing(4, after: row)
            index += columns
        }

        observe(model, whenVisible: true) { [weak self] in self?.render() }
        observe(owner.searchAccessChanges) { [weak self] in self?.syncAccess() }
        observe(owner.playbackChanges, whenVisible: true) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeActive() { syncAccess() }
    public override func pageDidBecomeVisible() { render() }
    public override func pageDidFinishAppearing() {
        drainClose()
    }
    public override func pageDidBecomeInactive() { model.close() }

    public override func viewWillTransition(to size: CGSize, with coordinator: UIViewControllerTransitionCoordinator) {
        transition.cancelInteractiveDismissal()
        super.viewWillTransition(to: size, with: coordinator)
    }

    public override func applyTheme() {
        super.applyTheme()
        (viewIfLoaded as? MusicPlayerBackgroundView)?.apply(
            palette: themeProvider.pagePalette, animated: themeProvider.animatesPageColorway
        )
        guard isViewLoaded else { return }
        sceneHeader.textColor = UIColor(theme.muted)
        for button in choiceButtons.values { button.applyTheme(theme) }
    }

    private func syncAccess() {
        guard isPageActive else { return }
        model.setAccess(
            available: owner.isAvailable && !owner.isClosed,
            generation: owner.searchAccessGeneration
        )
        render()
    }

    private func render() {
        guard isViewLoaded else { return }
        if account != owner.searchAccessGeneration || !owner.isAvailable || owner.isClosed ||
            (closeOnFMDeactivation && !owner.isFMActive) {
            model.close()
            close()
            return
        }
        let active = model.hasAccess ? model.selection : nil
        let available = model.hasAccess && (!closeOnFMDeactivation || owner.isFMActive)
        let pendingChoice = model.hasAccess ? model.pendingChoice : nil
        pageTitle = MusicFMModeChoice.title(for: model.selection)
        for choice in MusicFMModeChoice.main + MusicFMModeChoice.scenes {
            choiceButtons[choice.id]?.update(
                selected: choice.matches(active),
                pending: pendingChoice == choice.id,
                enabled: available && pendingChoice == nil,
                theme: theme
            )
        }
        if model.hasAccess, let error = model.error {
            let access = owner.searchAccessGeneration
            enqueueError(error, acknowledged: { [weak self] in
                guard let self, self.owner.searchAccessGeneration == access else { return }
                self.model.dismissError(error)
            })
        }
        if isPageVisible, model.hasAccess, model.selectionCompleted {
            model.acknowledgeSelection()
            onSelected()
            close()
        }
    }

    private func close() {
        if isBeingPresented || transition.isTransitioning {
            closeQueued = true
            transition.cancelInteractiveDismissal()
            return
        }
        guard !isBeingDismissed, presentingViewController != nil else { return }
        if let child = presentedViewController {
            child.dismiss(animated: false) { [weak self] in self?.close() }
            return
        }
        dismiss(animated: !UIAccessibility.isReduceMotionEnabled)
    }

    private func drainClose() {
        guard closeQueued, !isBeingPresented, !transition.isTransitioning else { return }
        closeQueued = false
        close()
    }
}

@MainActor
private final class MusicFMChoiceButton: UIButton {
    private let iconView = UIImageView()
    private let label = UILabel()
    private let spinner = UIActivityIndicatorView(style: .medium)
    private let checkmark = UIImageView(image: UIImage(
        systemName: "checkmark.circle.fill",
        withConfiguration: UIImage.SymbolConfiguration(pointSize: 12, weight: .regular)
    ))
    private lazy var feedback = PressFeedback(view: self)
    private var isChoiceSelected = false

    override var isHighlighted: Bool {
        didSet { feedback.setPressed(isHighlighted) }
    }

    init(choice: MusicFMModeChoice, scene: Bool, icon: UIImage?, action: @escaping @MainActor () -> Void) {
        super.init(frame: .zero)
        layer.cornerRadius = 12
        layer.cornerCurve = .continuous
        clipsToBounds = true
        accessibilityLabel = choice.title
        accessibilityIdentifier = "music.fm.choice." + choice.id

        let stack = UIStackView()
        stack.axis = .vertical
        stack.alignment = .center
        stack.spacing = 8
        stack.isUserInteractionEnabled = false
        stack.translatesAutoresizingMaskIntoConstraints = false

        if scene, let icon {
            iconView.image = icon.withRenderingMode(.alwaysTemplate)
            iconView.contentMode = .scaleAspectFit
            iconView.isAccessibilityElement = false
            iconView.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                iconView.widthAnchor.constraint(equalToConstant: 25),
                iconView.heightAnchor.constraint(equalToConstant: 25),
            ])
            stack.addArrangedSubview(iconView)
        }

        label.text = choice.title
        label.font = .systemFont(ofSize: scene ? 12 : 16)
        label.numberOfLines = 0
        label.textAlignment = .center
        label.isUserInteractionEnabled = false
        stack.addArrangedSubview(label)

        spinner.hidesWhenStopped = true
        spinner.accessibilityLabel = "正在切换"
        stack.addArrangedSubview(spinner)

        addSubview(stack)
        checkmark.isAccessibilityElement = false
        checkmark.isUserInteractionEnabled = false
        checkmark.translatesAutoresizingMaskIntoConstraints = false
        addSubview(checkmark)

        NSLayoutConstraint.activate([
            heightAnchor.constraint(greaterThanOrEqualToConstant: scene ? 84 : 64),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor),
            stack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 6),
            stack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -6),
            stack.topAnchor.constraint(greaterThanOrEqualTo: topAnchor, constant: 8),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: bottomAnchor, constant: -8),
            checkmark.topAnchor.constraint(equalTo: topAnchor, constant: 7),
            checkmark.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -7),
        ])
        addAction(UIAction { _ in action() }, for: .touchUpInside)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func update(selected: Bool, pending: Bool, enabled: Bool, theme: PageTheme) {
        isChoiceSelected = selected
        isEnabled = enabled
        applyTheme(theme)
        checkmark.isHidden = !selected
        if pending { spinner.startAnimating() } else { spinner.stopAnimating() }
        accessibilityValue = pending ? "正在切换" : ""
        accessibilityTraits = selected ? [.button, .selected] : .button
        if !enabled { accessibilityTraits.insert(.notEnabled) }
    }

    func applyTheme(_ theme: PageTheme) {
        backgroundColor = isChoiceSelected
            ? UIColor(theme.accent).withAlphaComponent(0.16)
            : UIColor(theme.buttonSurface)
        layer.borderColor = UIColor(isChoiceSelected ? theme.accent : theme.border).cgColor
        layer.borderWidth = isChoiceSelected ? 1.5 : 1
        iconView.tintColor = UIColor(theme.ink)
        label.textColor = UIColor(theme.ink)
        checkmark.tintColor = UIColor(theme.accent)
        spinner.color = UIColor(theme.muted)
    }
}
#endif
