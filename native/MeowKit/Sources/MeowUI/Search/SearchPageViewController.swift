#if os(iOS)
import UIKit

public struct SearchTargetOption: Equatable {
    public let id: String
    public let title: String
    public let placeholder: String
    public init(id: String, title: String, placeholder: String) {
        self.id = id; self.title = title; self.placeholder = placeholder
    }
}

/// Application composition supplies domain content; shared chrome and the
/// hosting business do not import the additional domain.
@MainActor
public protocol SearchResultContent: AnyObject {
    var controller: UIViewController { get }
    func submit(_ query: String)
    func applyTheme(_ theme: PageTheme)
    func cancel()
    func close()
}

@MainActor
public struct SearchResultCategory {
    public let target: SearchTargetOption
    public let makeContent: () -> any SearchResultContent
    public init(target: SearchTargetOption, makeContent: @escaping () -> any SearchResultContent) {
        self.target = target; self.makeContent = makeContent
    }
}

/// Search chrome contains no domain model. The caller supplies content and
/// query submission; UINavigationController owns every ordinary return.
@MainActor
open class SearchPageViewController: PageViewController {
    private let input = SearchInputView()
    public var searchField: UITextField { input.field }
    public var searchCapsule: UIView { input }
    public var query: String {
        get { searchField.text ?? "" }
        set { searchField.text = newValue; updateSubmission() }
    }
    public var onSubmit: ((String) -> Void)?
    public var searchTargets: [SearchTargetOption] = [] {
        didSet { updateSearchTargets() }
    }
    public var searchTargetID = "" {
        didSet { updateSearchTargets() }
    }
    public var searchTargetButton: UIButton { input.target }
    private let initialFocus: Bool
    private var focusedInitially = false

    public init(themeProvider: any PageThemeProviding, query: String,
                initialFocus: Bool = false) {
        self.initialFocus = initialFocus
        super.init(themeProvider: themeProvider)
        self.query = query
    }

    open override func viewDidLoad() {
        super.viewDidLoad()
        searchField.addTarget(self, action: #selector(updateSubmission), for: .editingChanged)
        searchField.addTarget(self, action: #selector(submitQuery), for: .editingDidEndOnExit)
        input.submit.addTarget(self, action: #selector(submitQuery), for: .touchUpInside)
        pageHeader.setTitleView(input)
        updateSubmission()
    }

    open override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        if initialFocus, !focusedInitially {
            focusedInitially = true
            searchField.becomeFirstResponder()
        }
    }

    open override func applyTheme() {
        super.applyTheme()
        input.backgroundColor = UIColor(theme.control)
        input.layer.borderColor = UIColor(theme.border).cgColor
        input.icon.tintColor = UIColor(theme.muted)
        for divider in input.dividers { divider.backgroundColor = UIColor(theme.border) }
        searchField.tintColor = UIColor(theme.accent)
        searchField.textColor = UIColor(theme.ink)
        input.submit.tintColor = UIColor(theme.ink)
        input.submit.setTitleColor(UIColor(theme.ink), for: .normal)
        input.submit.setTitleColor(UIColor(theme.muted), for: .disabled)
        input.target.setTitleColor(UIColor(theme.ink), for: .normal)
    }

    private func updateSearchTargets() {
        input.target.isHidden = searchTargets.count < 2
        guard let selected = searchTargets.first(where: { $0.id == searchTargetID }) ?? searchTargets.first else { return }
        input.target.setTitle(selected.title + "⌄", for: .normal)
        searchField.placeholder = selected.placeholder
        input.target.menu = UIMenu(children: searchTargets.map { option in
            UIAction(title: option.title, state: option.id == selected.id ? .on : .off) { [weak self] _ in
                self?.searchTargetID = option.id
            }
        })
    }

    @objc private func submitQuery() {
        let keyword = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !keyword.isEmpty else { return }
        searchField.resignFirstResponder()
        onSubmit?(keyword)
    }

    @objc private func updateSubmission() {
        input.submit.isEnabled = !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

@MainActor
private final class SearchInputView: UIView {
    let field = UITextField()
    let submit = UIButton(type: .custom)
    let target = UIButton(type: .system)
    let icon = UIImageView(image: PageIcon.image(.search, size: 18))
    let dividers = [UIView(), UIView()]

    override var intrinsicContentSize: CGSize {
        CGSize(width: UIView.layoutFittingExpandedSize.width, height: PageLayoutMetrics.searchControl)
    }

    init() {
        super.init(frame: .zero)
        layer.cornerRadius = PageLayoutMetrics.searchControl / 2
        layer.borderWidth = 0.5
        setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        field.font = .systemFont(ofSize: 15)
        field.returnKeyType = .search
        field.enablesReturnKeyAutomatically = true
        field.autocorrectionType = .no
        field.spellCheckingType = .no
        field.clearButtonMode = .whileEditing
        field.accessibilityIdentifier = "search.field"
        field.setContentHuggingPriority(.defaultLow, for: .horizontal)
        field.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        submit.setTitle("搜索", for: .normal)
        submit.titleLabel?.font = .systemFont(ofSize: 15, weight: .medium)
        submit.accessibilityIdentifier = "search.submit"
        target.isHidden = true
        target.showsMenuAsPrimaryAction = true
        target.titleLabel?.font = .systemFont(ofSize: 13, weight: .medium)
        target.accessibilityIdentifier = "search.target"
        target.setContentCompressionResistancePriority(.required, for: .horizontal)
        let stack = UIStackView(arrangedSubviews: [icon, dividers[0], target, field, dividers[1], submit])
        stack.axis = .horizontal
        stack.alignment = .center
        stack.spacing = 8
        stack.isLayoutMarginsRelativeArrangement = true
        stack.layoutMargins = UIEdgeInsets(top: 0, left: 12, bottom: 0, right: 4)
        addSubview(stack)
        stack.pinEdges(to: self)
        NSLayoutConstraint.activate([
            icon.widthAnchor.constraint(equalToConstant: 18),
            icon.heightAnchor.constraint(equalToConstant: 18),
            field.heightAnchor.constraint(equalToConstant: PageLayoutMetrics.searchControl),
            submit.widthAnchor.constraint(equalToConstant: 60),
            submit.heightAnchor.constraint(equalToConstant: PageLayoutMetrics.searchControl),
        ] + dividers.flatMap {
            [$0.widthAnchor.constraint(equalToConstant: 0.5),
             $0.heightAnchor.constraint(equalToConstant: 16)]
        })
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
#endif
