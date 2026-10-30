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
        hostingController.view.pinEdges(to: view)
        hostingController.didMove(toParent: self)
    }

    public func setContent(_ content: Content) {
        hostingController.rootView = content
    }
}
#endif
