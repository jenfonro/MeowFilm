#if os(iOS)
import UIKit

/// Search chrome contains no domain model. The caller supplies content and
/// query submission; UINavigationController owns every ordinary return.
@MainActor
open class SearchPageViewController: PageViewController, UISearchBarDelegate {
    public let searchBar = UISearchBar()
    public var onSubmit: ((String) -> Void)?
    private let initialFocus: Bool
    private var focusedInitially = false

    public init(themeProvider: any PageThemeProviding, query: String,
                initialFocus: Bool = false) {
        self.initialFocus = initialFocus
        super.init(themeProvider: themeProvider)
        searchBar.text = query
    }

    open override func viewDidLoad() {
        super.viewDidLoad()
        searchBar.delegate = self
        searchBar.searchTextField.returnKeyType = .search
        searchBar.accessibilityIdentifier = "search.field"
        navigationItem.titleView = searchBar
        navigationItem.rightBarButtonItem = UIBarButtonItem(
            title: "搜索", style: .plain, target: self, action: #selector(submitQuery)
        )
        navigationItem.rightBarButtonItem?.accessibilityIdentifier = "search.submit"
        updateSubmission()
    }

    open override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        if initialFocus, !focusedInitially {
            focusedInitially = true
            searchBar.becomeFirstResponder()
        }
    }

    public func searchBarSearchButtonClicked(_ searchBar: UISearchBar) { submitQuery() }
    public func searchBar(_ searchBar: UISearchBar, textDidChange searchText: String) {
        updateSubmission()
    }

    open override func applyTheme() {
        super.applyTheme()
        searchBar.tintColor = UIColor(theme.accent)
        searchBar.searchTextField.textColor = UIColor(theme.ink)
    }

    @objc private func submitQuery() {
        let keyword = searchBar.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !keyword.isEmpty else { return }
        searchBar.resignFirstResponder()
        onSubmit?(keyword)
    }

    private func updateSubmission() {
        navigationItem.rightBarButtonItem?.isEnabled =
            searchBar.text?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false
    }
}
#endif
