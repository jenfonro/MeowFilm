#if os(iOS)
import UIKit
import Combine
import MeowDebug

/// Unified pure UIKit centered floating-card dialog base controller.
@MainActor
open class FloatingCardDialogController: UIViewController, DebugDialogControllable {
    public let themeProvider: any PageThemeProviding
    public let backdropView = UIView()
    public let cardView = UIView()
    public let headerContainer = UIView()
    public let headerStack = UIStackView()
    public let titleLabel = UILabel()
    public let closeButton = UIButton(type: .system)
    public let headerDivider = SettingsDividerView()
    public let scrollView = UIScrollView()
    public let bodyStack = UIStackView()
    public let errorLabel = UILabel()
    public let footerContainer = UIView()
    public let footerStack = UIStackView()
    public let footerDivider = SettingsDividerView()

    public var cardTitle: String = "" {
        didSet { titleLabel.text = cardTitle }
    }
    open var dismissesOnBackdropTap: Bool { true }
    open var showsCloseButton: Bool { true }
    open var showsHeaderDivider: Bool { false }
    open var showsFooter: Bool { false }
    open var usesCompactHeader: Bool { false }
    open var cardMaxWidth: CGFloat { 420 }
    open var cardCornerRadius: CGFloat { 22 }

    private var themeables = ThemeableRegistry()
    private var primaryLabels = WeakLabelRegistry()
    private var mutedLabels = WeakLabelRegistry()
    private var dangerLabels = WeakLabelRegistry()
    private var themeObservation: AnyCancellable?
    private var dialogTasks: [UUID: Task<Void, Never>] = [:]
    private var keyedDialogTaskIDs: [String: UUID] = [:]
    private var didNotifyClose = false
    private var leadingHeaderView: UIView?
    private var trailingHeaderView: UIView?
    private let errorWrapper = UIView()

    public var hasActiveDialogTask: Bool { !dialogTasks.isEmpty }

    public var theme: PageTheme {
        PageTheme(colors: themeProvider.pagePalette,
                  dark: traitCollection.userInterfaceStyle == .dark)
    }

    public init(title: String = "", themeProvider: any PageThemeProviding) {
        self.cardTitle = title
        self.themeProvider = themeProvider
        super.init(nibName: nil, bundle: nil)
        modalPresentationStyle = .overFullScreen
        modalTransitionStyle = .crossDissolve
    }

    @available(*, unavailable)
    public required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    open override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear
        buildChrome()
        themeObservation = themeProvider.pageThemeChanges
            .receive(on: RunLoop.main)
            .sink { [weak self] in self?.applyTheme() }
        registerForTraitChanges([UITraitUserInterfaceStyle.self]) { (dialog: FloatingCardDialogController, _: UITraitCollection) in
            dialog.applyTheme()
        }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (dialog: FloatingCardDialogController, _: UITraitCollection) in
            dialog.applyTypography()
        }
        applyTypography()
        applyTheme()
    }

    private func buildChrome() {
        backdropView.backgroundColor = UIColor(PageTheme.scrim)
        backdropView.accessibilityIdentifier = "floating.dialog.scrim"
        view.addSubview(backdropView)
        backdropView.pinEdges(to: view)
        if dismissesOnBackdropTap {
            let tap = UITapGestureRecognizer(target: self, action: #selector(handleBackdropTap))
            backdropView.addGestureRecognizer(tap)
        }

        cardView.accessibilityIdentifier = "floating.dialog.panel"
        cardView.layer.cornerRadius = cardCornerRadius
        cardView.layer.cornerCurve = .continuous
        cardView.clipsToBounds = true
        cardView.layer.borderWidth = SettingsVisualMetrics.cardBorderWidth
        cardView.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(cardView)

        let topGuide = UILayoutGuide()
        view.addLayoutGuide(topGuide)
        NSLayoutConstraint.activate([
            topGuide.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 16),
            topGuide.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor, constant: -16),
            topGuide.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 16),
            topGuide.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -16),

            cardView.centerXAnchor.constraint(equalTo: topGuide.centerXAnchor),
            cardView.centerYAnchor.constraint(equalTo: topGuide.centerYAnchor),
            cardView.leadingAnchor.constraint(greaterThanOrEqualTo: topGuide.leadingAnchor),
            cardView.trailingAnchor.constraint(lessThanOrEqualTo: topGuide.trailingAnchor),
            cardView.topAnchor.constraint(greaterThanOrEqualTo: topGuide.topAnchor),
            cardView.bottomAnchor.constraint(lessThanOrEqualTo: topGuide.bottomAnchor),
            cardView.widthAnchor.constraint(lessThanOrEqualToConstant: cardMaxWidth),
        ])
        let preferredWidth = cardView.widthAnchor.constraint(equalTo: topGuide.widthAnchor)
        preferredWidth.priority = .defaultHigh
        preferredWidth.isActive = true

        // Header
        headerStack.axis = .horizontal
        headerStack.alignment = .center
        headerStack.spacing = 10
        headerStack.translatesAutoresizingMaskIntoConstraints = false
        titleLabel.text = cardTitle
        titleLabel.numberOfLines = 0
        titleLabel.lineBreakMode = .byWordWrapping
        titleLabel.adjustsFontForContentSizeCategory = true
        titleLabel.accessibilityTraits.insert(.header)
        titleLabel.setContentCompressionResistancePriority(.required, for: .vertical)
        titleLabel.setContentHuggingPriority(.defaultLow, for: .horizontal)
        headerStack.addArrangedSubview(titleLabel)

        closeButton.setImage(
            UIImage(systemName: "xmark", withConfiguration: UIImage.SymbolConfiguration(pointSize: 15, weight: .regular)),
            for: .normal
        )
        closeButton.accessibilityLabel = "关闭"
        closeButton.accessibilityIdentifier = "floating.dialog.close"
        closeButton.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            closeButton.widthAnchor.constraint(equalToConstant: 44),
            closeButton.heightAnchor.constraint(equalToConstant: 44),
        ])
        closeButton.addAction(UIAction { [weak self] _ in self?.requestDismiss() }, for: .touchUpInside)
        closeButton.isHidden = !showsCloseButton
        headerStack.addArrangedSubview(closeButton)

        headerContainer.translatesAutoresizingMaskIntoConstraints = false
        headerContainer.addSubview(headerStack)
        headerDivider.translatesAutoresizingMaskIntoConstraints = false
        headerDivider.isHidden = !showsHeaderDivider
        headerContainer.addSubview(headerDivider)
        registerThemeable(headerDivider)

        let headerVerticalInset: CGFloat = usesCompactHeader ? 12 : 16
        NSLayoutConstraint.activate([
            headerStack.topAnchor.constraint(equalTo: headerContainer.topAnchor, constant: headerVerticalInset),
            headerStack.bottomAnchor.constraint(equalTo: headerContainer.bottomAnchor, constant: -headerVerticalInset),
            headerStack.leadingAnchor.constraint(equalTo: headerContainer.leadingAnchor, constant: 20),
            headerStack.trailingAnchor.constraint(equalTo: headerContainer.trailingAnchor, constant: showsCloseButton ? -10 : -20),
            headerDivider.leadingAnchor.constraint(equalTo: headerContainer.leadingAnchor),
            headerDivider.trailingAnchor.constraint(equalTo: headerContainer.trailingAnchor),
            headerDivider.bottomAnchor.constraint(equalTo: headerContainer.bottomAnchor),
        ])

        // Scrollable Body
        scrollView.alwaysBounceVertical = false
        scrollView.showsHorizontalScrollIndicator = false
        scrollView.keyboardDismissMode = .interactive
        scrollView.translatesAutoresizingMaskIntoConstraints = false

        bodyStack.axis = .vertical
        bodyStack.spacing = 12
        bodyStack.translatesAutoresizingMaskIntoConstraints = false
        scrollView.addSubview(bodyStack)

        let bodyTop: CGFloat = showsHeaderDivider ? 16 : 4
        let bodyBottom: CGFloat = showsFooter ? 14 : 20
        NSLayoutConstraint.activate([
            bodyStack.topAnchor.constraint(equalTo: scrollView.contentLayoutGuide.topAnchor, constant: bodyTop),
            bodyStack.bottomAnchor.constraint(equalTo: scrollView.contentLayoutGuide.bottomAnchor, constant: -bodyBottom),
            bodyStack.leadingAnchor.constraint(equalTo: scrollView.contentLayoutGuide.leadingAnchor, constant: 20),
            bodyStack.trailingAnchor.constraint(equalTo: scrollView.contentLayoutGuide.trailingAnchor, constant: -20),
            bodyStack.widthAnchor.constraint(equalTo: scrollView.frameLayoutGuide.widthAnchor, constant: -40),
        ])
        let matchHeight = scrollView.heightAnchor.constraint(equalTo: scrollView.contentLayoutGuide.heightAnchor)
        matchHeight.priority = .defaultLow
        matchHeight.isActive = true

        // Inline Error Label
        errorLabel.accessibilityIdentifier = "floating.dialog.error"
        errorLabel.numberOfLines = 0
        errorLabel.lineBreakMode = .byWordWrapping
        errorLabel.adjustsFontForContentSizeCategory = true
        errorLabel.isHidden = true
        errorLabel.setContentCompressionResistancePriority(.required, for: .vertical)

        // Footer
        footerContainer.translatesAutoresizingMaskIntoConstraints = false
        footerContainer.isHidden = !showsFooter
        footerDivider.translatesAutoresizingMaskIntoConstraints = false
        footerContainer.addSubview(footerDivider)
        registerThemeable(footerDivider)
        footerStack.axis = .horizontal
        footerStack.alignment = .center
        footerStack.spacing = 12
        footerStack.translatesAutoresizingMaskIntoConstraints = false
        footerContainer.addSubview(footerStack)
        NSLayoutConstraint.activate([
            footerDivider.topAnchor.constraint(equalTo: footerContainer.topAnchor),
            footerDivider.leadingAnchor.constraint(equalTo: footerContainer.leadingAnchor),
            footerDivider.trailingAnchor.constraint(equalTo: footerContainer.trailingAnchor),
            footerStack.topAnchor.constraint(equalTo: footerContainer.topAnchor, constant: 10),
            footerStack.bottomAnchor.constraint(equalTo: footerContainer.bottomAnchor, constant: -12),
            footerStack.leadingAnchor.constraint(equalTo: footerContainer.leadingAnchor, constant: 18),
            footerStack.trailingAnchor.constraint(equalTo: footerContainer.trailingAnchor, constant: -18),
        ])

        let rootStack = UIStackView(arrangedSubviews: [headerContainer, scrollView, errorLabelContainer(), footerContainer])
        rootStack.axis = .vertical
        rootStack.spacing = 0
        cardView.addSubview(rootStack)
        rootStack.pinEdges(to: cardView)
    }

    private func errorLabelContainer() -> UIView {
        errorWrapper.isHidden = true
        errorWrapper.addSubview(errorLabel)
        errorLabel.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            errorLabel.topAnchor.constraint(equalTo: errorWrapper.topAnchor, constant: 4),
            errorLabel.bottomAnchor.constraint(equalTo: errorWrapper.bottomAnchor, constant: -10),
            errorLabel.leadingAnchor.constraint(equalTo: errorWrapper.leadingAnchor, constant: 20),
            errorLabel.trailingAnchor.constraint(equalTo: errorWrapper.trailingAnchor, constant: -20),
        ])
        return errorWrapper
    }

    public func setHeaderLeading(_ view: UIView?) {
        if let existing = leadingHeaderView {
            headerStack.removeArrangedSubview(existing)
            existing.removeFromSuperview()
            leadingHeaderView = nil
        }
        if let view {
            leadingHeaderView = view
            headerStack.insertArrangedSubview(view, at: 0)
            if let themeable = view as? any PageThemeable { registerThemeable(themeable) }
        }
    }

    public func setHeaderTrailing(_ view: UIView?) {
        if let existing = trailingHeaderView {
            headerStack.removeArrangedSubview(existing)
            existing.removeFromSuperview()
            trailingHeaderView = nil
        }
        if let view {
            trailingHeaderView = view
            let index = max(0, headerStack.arrangedSubviews.count - 1)
            headerStack.insertArrangedSubview(view, at: index)
            if let themeable = view as? any PageThemeable { registerThemeable(themeable) }
        }
    }

    public func setErrorMessage(_ message: String?) {
        let trimmed = message?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        errorLabel.text = trimmed.isEmpty ? nil : trimmed
        errorLabel.isHidden = trimmed.isEmpty
        errorWrapper.isHidden = trimmed.isEmpty
    }

    public func registerThemeable(_ item: any PageThemeable) {
        themeables.register(item)
        if isViewLoaded { item.applyTheme(theme) }
    }

    open override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        didNotifyClose = false
        applyTheme()
    }

    open override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        PageViewController.reconcileWindowModalCoverage(from: presentingViewController)
    }

    open override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        PageViewController.reconcileWindowModalCoverage(from: presentingViewController)
        if !didNotifyClose, (isBeingDismissed || presentingViewController == nil) {
            didNotifyClose = true
            cancelDialogTasks()
            dialogWillClose()
        }
    }

    open override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        PageViewController.reconcileWindowModalCoverage(from: presentingViewController)
    }

    /// Hook invoked once when the dialog is dismissed or detached from its presenter.
    open func dialogWillClose() {}

    public func runDialogTask(
        key: String? = nil,
        _ operation: @escaping @MainActor () async -> Void
    ) {
        if let key, let existingID = keyedDialogTaskIDs.removeValue(forKey: key) {
            dialogTasks.removeValue(forKey: existingID)?.cancel()
        }
        let id = UUID()
        if let key { keyedDialogTaskIDs[key] = id }
        dialogTasks[id] = Task { [weak self] in
            defer {
                if let self {
                    self.dialogTasks[id] = nil
                    if let key, self.keyedDialogTaskIDs[key] == id {
                        self.keyedDialogTaskIDs[key] = nil
                    }
                }
            }
            guard !Task.isCancelled else { return }
            await operation()
        }
    }

    public func runDialogTask(
        key: String? = nil,
        onFinally: (@MainActor () -> Void)? = nil,
        onError: (@MainActor (Error) -> Void)? = nil,
        _ operation: @escaping @MainActor () async throws -> Void
    ) {
        runDialogTask(key: key) { [weak self] in
            defer { onFinally?() }
            do {
                try await operation()
            } catch is CancellationError {
            } catch {
                if Task.isCancelled || (error as? URLError)?.code == .cancelled { return }
                guard let self else { return }
                if let onError {
                    onError(error)
                } else {
                    self.setErrorMessage(error.localizedDescription)
                }
            }
        }
    }

    public func cancelDialogTask(key: String) {
        guard let existingID = keyedDialogTaskIDs.removeValue(forKey: key) else { return }
        dialogTasks.removeValue(forKey: existingID)?.cancel()
    }

    public func cancelDialogTasks() {
        for task in dialogTasks.values { task.cancel() }
        dialogTasks.removeAll()
        keyedDialogTaskIDs.removeAll()
    }

    public func hasActiveDialogTask(key: String) -> Bool {
        keyedDialogTaskIDs[key] != nil
    }

    nonisolated deinit {
        MainActor.assumeIsolated {
            for task in dialogTasks.values { task.cancel() }
        }
    }

    public func registerPrimaryLabel(_ label: UILabel) {
        primaryLabels.register(label)
        if isViewLoaded { label.textColor = UIColor(theme.settings.ink) }
    }

    public func registerMutedLabel(_ label: UILabel) {
        mutedLabels.register(label)
        if isViewLoaded { label.textColor = UIColor(theme.settings.muted) }
    }

    public func registerDangerLabel(_ label: UILabel) {
        dangerLabels.register(label)
        if isViewLoaded { label.textColor = UIColor(theme.settings.danger) }
    }

    public func addBodyView(_ view: UIView) {
        bodyStack.addArrangedSubview(view)
        if let themeable = view as? any PageThemeable { registerThemeable(themeable) }
    }

    @objc private func handleBackdropTap() {
        requestDismiss()
    }

    open func requestDismiss() {
        dismissSelf(animated: true)
    }

    open func applyTypography() {
        titleLabel.font = UIFontMetrics(forTextStyle: usesCompactHeader ? .headline : .title3)
            .scaledFont(for: .systemFont(ofSize: usesCompactHeader ? 17 : 19, weight: .regular),
                        compatibleWith: traitCollection)
        errorLabel.font = .preferredFont(forTextStyle: .footnote, compatibleWith: traitCollection)
    }

    open func applyTheme() {
        guard isViewLoaded else { return }
        let current = theme
        let settings = current.settings
        view.tintColor = UIColor(settings.accent)
        cardView.backgroundColor = UIColor(settings.surface)
        cardView.layer.borderColor = UIColor(settings.border).cgColor
        titleLabel.textColor = UIColor(settings.ink)
        closeButton.tintColor = UIColor(settings.muted)
        errorLabel.textColor = UIColor(settings.danger)

        themeables.apply(current)
        primaryLabels.applyColor(UIColor(settings.ink))
        mutedLabels.applyColor(UIColor(settings.muted))
        dangerLabels.applyColor(UIColor(settings.danger))
    }

    // MARK: - DebugDialogControllable

    open var debugDialogTypeName: String { String(describing: type(of: self)) }
    open var debugDialogTitle: String { cardTitle }
    open var debugDialogMessage: String? { errorLabel.isHidden ? nil : errorLabel.text }

    open func debugDialogActions() -> [DebugDialogActionDescriptor] {
        [DebugDialogActionDescriptor(index: 0, label: "关闭", kind: "cancel", enabled: true)]
    }

    open func debugDialogSnapshot() -> DebugDialogSnapshot {
        DebugDialogSnapshot(
            typeName: debugDialogTypeName,
            title: debugDialogTitle,
            message: debugDialogMessage,
            errorText: errorLabel.isHidden ? nil : errorLabel.text,
            actions: debugDialogActions()
        )
    }

    open func performDebugDialogAction(_ action: DebugDialogActionDescriptor) throws {
        requestDismiss()
    }

    open func debugInvokeDialogAction(kind: String?, index: Int?, label: String?) throws -> String {
        let descriptors = debugDialogActions()
        let chosen: DebugDialogActionDescriptor?
        if let index {
            guard descriptors.indices.contains(where: { descriptors[$0].index == index }) else {
                throw DebugAPIError.invalidArgument("\(debugDialogTypeName) button index out of range: \(index)")
            }
            chosen = descriptors.first { $0.index == index }
        } else if let label {
            chosen = descriptors.first { $0.label == label }
        } else if let kind {
            let normalized = kind == "confirm" ? "default" : kind
            chosen = descriptors.first { $0.kind == kind || $0.kind == normalized }
        } else {
            chosen = descriptors.first { $0.kind != "cancel" } ?? descriptors.first
        }
        guard let chosen else {
            throw DebugAPIError.notFound("\(debugDialogTypeName) action not found")
        }
        guard chosen.enabled else {
            throw DebugAPIError.invalidState("\(debugDialogTypeName) action '\(chosen.label)' is disabled")
        }
        try performDebugDialogAction(chosen)
        return chosen.label
    }
}
#endif
