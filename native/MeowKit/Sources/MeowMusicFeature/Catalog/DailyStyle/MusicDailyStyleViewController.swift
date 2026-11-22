#if os(iOS)
import UIKit
import MeowUI

/// Pure UIKit daily style editor reusing `FilterTagRows` wrapping geometry.
@MainActor
public final class MusicDailyStyleViewController: PageViewController {
    private var done: PageHeaderTextButton?
    let model: MusicDailyStyleViewModel
    private let scrollView = UIScrollView()
    private let contentStack = UIStackView()
    private let busyRow = UIStackView()
    private let spinner = UIActivityIndicatorView(style: .medium)
    private let busyLabel = UILabel()
    private let categoryWrap = MusicWrappingChipsView()
    private let tagWrap = MusicWrappingChipsView()
    private var shownError: String?

    public init(
        page: MusicPublicBrowseViewModel,
        source: MusicRemoteQueueSource,
        receipts: MusicSettingsReceiptOwner,
        appearance: MusicAppearancePreferencesStore
    ) {
        model = MusicDailyStyleViewModel(page: page, source: source, receipts: receipts)
        super.init(themeProvider: appearance)
        pageTitle = "选择推荐风格"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        let done = PageHeaderTextButton(title: "完成") { [weak self] in self?.save() }
        self.done = done
        pageHeader.setLeading(PageHeaderButton(
            image: PageIcon.image(.close, size: 18), accessibilityLabel: "关闭"
        ) { [weak self] in self?.close() })
        pageHeader.setTrailing([done])

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

        contentStack.axis = .vertical
        contentStack.alignment = .fill
        contentStack.spacing = 20
        contentStack.isLayoutMarginsRelativeArrangement = true
        contentStack.directionalLayoutMargins = NSDirectionalEdgeInsets(top: 16, leading: 16, bottom: 24, trailing: 16)
        contentStack.translatesAutoresizingMaskIntoConstraints = false
        scrollView.addSubview(contentStack)
        NSLayoutConstraint.activate([
            contentStack.topAnchor.constraint(equalTo: scrollView.contentLayoutGuide.topAnchor),
            contentStack.bottomAnchor.constraint(equalTo: scrollView.contentLayoutGuide.bottomAnchor),
            contentStack.leadingAnchor.constraint(equalTo: scrollView.contentLayoutGuide.leadingAnchor),
            contentStack.trailingAnchor.constraint(equalTo: scrollView.contentLayoutGuide.trailingAnchor),
            contentStack.widthAnchor.constraint(equalTo: scrollView.frameLayoutGuide.widthAnchor),
        ])

        busyLabel.text = "正在处理"
        busyLabel.font = .preferredFont(forTextStyle: .subheadline)
        busyRow.axis = .horizontal
        busyRow.alignment = .center
        busyRow.spacing = 8
        busyRow.addArrangedSubview(spinner)
        busyRow.addArrangedSubview(busyLabel)
        busyRow.addArrangedSubview(UIView())

        contentStack.addArrangedSubview(busyRow)
        contentStack.addArrangedSubview(categoryWrap)
        contentStack.addArrangedSubview(tagWrap)

        observe(model, whenVisible: true) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeActive() { model.loadIfNeeded() }
    public override func pageDidBecomeVisible() { render() }
    public override func pageDidBecomeInactive() { model.close() }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        spinner.color = UIColor(theme.muted)
        busyLabel.textColor = UIColor(theme.muted)
        categoryWrap.applyTheme(theme)
        tagWrap.applyTheme(theme)
    }

    private func render() {
        guard isViewLoaded else { return }
        let categories = model.available ? model.categories : []
        let enabled = model.available && !model.working
        busyRow.isHidden = !model.working
        if model.working { spinner.startAnimating() } else { spinner.stopAnimating() }

        categoryWrap.isHidden = categories.isEmpty
        categoryWrap.configure(
            items: categories.map { category in
                MusicWrappingChipsView.Item(
                    id: "music.dailyStyle.category.\(category.id)",
                    title: category.name,
                    selected: category.id == model.category,
                    enabled: enabled,
                    action: { [weak model] in model?.selectCategory(category.id) }
                )
            },
            theme: theme
        )

        if let activeCategory = categories.first(where: { $0.id == model.category }) {
            tagWrap.isHidden = false
            tagWrap.configure(
                items: activeCategory.tags.map { tag in
                    MusicWrappingChipsView.Item(
                        id: "music.dailyStyle.tag.\(tag.id)",
                        title: tag.name,
                        selected: model.selected.contains(tag.id),
                        enabled: enabled,
                        action: { [weak model] in model?.selectTag(tag.id) }
                    )
                },
                theme: theme
            )
        } else {
            tagWrap.isHidden = true
            tagWrap.configure(items: [], theme: theme)
        }

        done?.isEnabled = model.canSave
        if let error = model.error {
            if shownError != error {
                shownError = error
                let retryAction: (@MainActor () -> Void)?
                if model.canRetry {
                    retryAction = { [weak self] in
                        self?.shownError = nil
                        self?.model.retry()
                    }
                } else {
                    retryAction = nil
                }
                enqueueError(error, retry: retryAction, acknowledged: { [weak self, weak model] in
                    if self?.shownError == error { self?.shownError = nil }
                    model?.dismissError(error)
                })
            }
        } else {
            shownError = nil
        }
        if isPageVisible, model.saved {
            model.acknowledgeSave()
            dismiss(animated: true)
        }
    }

    private func save() { model.save() }
    private func close() { dismiss(animated: true) }
}

@MainActor
private final class MusicWrappingChipsView: UIView {
    struct Item {
        let id: String
        let title: String
        let selected: Bool
        let enabled: Bool
        let action: @MainActor () -> Void
    }

    private var buttons: [UIButton] = []
    private var selectedStates: [Bool] = []
    private var cachedHeight: CGFloat = 0
    private var lastWidth: CGFloat = 0

    func configure(items: [Item], theme: PageTheme) {
        selectedStates = items.map(\.selected)
        while buttons.count > items.count {
            buttons.removeLast().removeFromSuperview()
        }
        while buttons.count < items.count {
            let button = UIButton(type: .system)
            button.titleLabel?.lineBreakMode = .byTruncatingTail
            button.layer.borderWidth = 0.5
            button.clipsToBounds = true
            addSubview(button)
            buttons.append(button)
        }
        for (button, item) in zip(buttons, items) {
            var config = UIButton.Configuration.plain()
            config.title = item.title
            config.contentInsets = NSDirectionalEdgeInsets(top: 7, leading: 12, bottom: 7, trailing: 12)
            config.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { incoming in
                var out = incoming
                out.font = .systemFont(ofSize: 13)
                return out
            }
            config.baseForegroundColor = UIColor(theme.ink)
            button.configuration = config
            button.backgroundColor = UIColor(item.selected ? theme.buttonSelected : theme.chip)
            button.layer.borderColor = UIColor(item.selected ? theme.buttonSelected : theme.border).cgColor
            button.isEnabled = item.enabled
            button.accessibilityIdentifier = item.id
            button.accessibilityLabel = item.title
            button.accessibilityTraits = item.selected ? [.button, .selected] : .button
            if !item.enabled { button.accessibilityTraits.insert(.notEnabled) }
            button.removeAction(identifiedBy: UIAction.Identifier("chip"), for: .touchUpInside)
            button.addAction(UIAction(identifier: UIAction.Identifier("chip")) { _ in item.action() }, for: .touchUpInside)
        }
        setNeedsLayout()
        layoutIfNeeded()
    }

    func applyTheme(_ theme: PageTheme) {
        for (button, selected) in zip(buttons, selectedStates) {
            button.configuration?.baseForegroundColor = UIColor(theme.ink)
            button.backgroundColor = UIColor(selected ? theme.buttonSelected : theme.chip)
            button.layer.borderColor = UIColor(selected ? theme.buttonSelected : theme.border).cgColor
        }
    }

    override var intrinsicContentSize: CGSize {
        CGSize(width: UIView.noIntrinsicMetric, height: cachedHeight)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let width = bounds.width
        guard width > 0 else { return }
        let sizes = buttons.map { $0.sizeThatFits(CGSize(width: width, height: CGFloat.greatestFiniteMagnitude)) }
        let rows = FilterTagRows(width: width, items: sizes)
        for (button, frame) in zip(buttons, rows.frames) {
            button.frame = frame
            button.layer.cornerRadius = frame.height / 2
        }
        if abs(cachedHeight - rows.size.height) > 0.5 || abs(lastWidth - width) > 0.5 {
            cachedHeight = rows.size.height
            lastWidth = width
            invalidateIntrinsicContentSize()
        }
    }
}
#endif
