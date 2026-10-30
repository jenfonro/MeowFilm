#if os(iOS)
import SwiftUI
import UIKit
import MeowUI

@MainActor
class MusicSettingsPageViewController<Content: View>: HostingPageViewController<Content> {
    override var prefersPlainBackButton: Bool { true }
    let appearance: MusicAppearancePreferencesStore
    private var lastError: String?
    private weak var errorAlert: UIAlertController?
    private var pendingError: (message: String, retry: (() -> Void)?)?
    private var renderedTheme: PageTheme?

    init(appearance: MusicAppearancePreferencesStore, content: Content) {
        self.appearance = appearance
        super.init(themeProvider: appearance, content: content)
    }

    func contentThemeChanged() -> Bool {
        guard isViewLoaded, renderedTheme != theme else { return false }
        renderedTheme = theme
        return true
    }

    func invalidateErrorPresentation() {
        errorAlert?.dismiss(animated: false)
        errorAlert = nil
        lastError = nil
        pendingError = nil
    }

    override func pageDidFinishAppearing() {
        if let pendingError { reportError(pendingError.message, retry: pendingError.retry) }
    }

    func reportError(_ message: String?, retry: (() -> Void)? = nil) {
        guard let message, !message.isEmpty else {
            lastError = nil; pendingError = nil
            return
        }
        guard message != lastError else { pendingError = nil; return }
        pendingError = (message, retry)
        guard canPresentPageAlert else { return }
        pendingError = nil
        if let retry {
            let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "取消", style: .cancel))
            alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
                self?.lastError = nil
                retry()
            })
            present(alert, animated: true)
            errorAlert = alert
            lastError = message
            return
        }
        if showError(message) {
            errorAlert = presentedViewController as? UIAlertController
            lastError = message
        }
    }
}
#endif
