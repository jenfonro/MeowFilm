#if os(iOS)
import UIKit
import SwiftUI

@MainActor
open class HostingPageViewController<Content: SwiftUI.View>: PageViewController {
    private let hostingController: UIHostingController<Content>

    public init(themeProvider: any PageThemeProviding, content: Content) {
        hostingController = UIHostingController(rootView: content)
        super.init(themeProvider: themeProvider)
    }

    @available(*, unavailable)
    required public init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    open override func viewDidLoad() {
        super.viewDidLoad()
        addChild(hostingController)
        hostingController.view.backgroundColor = .clear
        view.addSubview(hostingController.view)
        hostingController.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            hostingController.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            hostingController.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            hostingController.view.topAnchor.constraint(equalTo: contentTopAnchor),
            hostingController.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        hostingController.didMove(toParent: self)
    }

    public func setContent(_ content: Content) {
        hostingController.rootView = content
    }
}
#endif
