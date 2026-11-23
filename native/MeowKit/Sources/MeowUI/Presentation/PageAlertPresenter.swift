#if os(iOS)
import UIKit
import MeowDebug

/// Single alert/action-sheet gateway for the entire application.
/// Every alert constructed through `PageAlertPresenter` automatically registers
/// its real action callbacks in `DebugAlertActionRegistry` (zero private APIs).
@MainActor
public struct PageAlertAction {
    public let title: String
    public let style: UIAlertAction.Style
    public let handler: (@MainActor () -> Void)?

    public init(title: String, style: UIAlertAction.Style = .default,
                handler: (@MainActor () -> Void)? = nil) {
        self.title = title
        self.style = style
        self.handler = handler
    }

    var debugKind: String {
        switch style {
        case .cancel: return "cancel"
        case .destructive: return "destructive"
        default: return "default"
        }
    }
}

@MainActor
public struct PageAlertTextField {
    public let placeholder: String?
    public let text: String?
    public let keyboardType: UIKeyboardType
    public let isSecureTextEntry: Bool
    public let accessibilityIdentifier: String?
    public let configure: ((UITextField) -> Void)?

    public init(
        placeholder: String? = nil,
        text: String? = nil,
        keyboardType: UIKeyboardType = .default,
        isSecureTextEntry: Bool = false,
        accessibilityIdentifier: String? = nil,
        configure: ((UITextField) -> Void)? = nil
    ) {
        self.placeholder = placeholder
        self.text = text
        self.keyboardType = keyboardType
        self.isSecureTextEntry = isSecureTextEntry
        self.accessibilityIdentifier = accessibilityIdentifier
        self.configure = configure
    }
}

@MainActor
public enum PageAlertPresenter {
    @discardableResult
    public static func present(
        on presenter: UIViewController,
        title: String?,
        message: String? = nil,
        preferredStyle: UIAlertController.Style = .alert,
        sourceView: UIView? = nil,
        textFields: [PageAlertTextField] = [],
        actions: [PageAlertAction],
        inputSubmit: (@MainActor ([String]) -> Void)? = nil,
        inputSubmitTitle: String? = nil,
        inputSubmitStyle: UIAlertAction.Style = .default,
        animated: Bool = true
    ) -> UIAlertController? {
        if let page = presenter as? PageViewController {
            guard page.canPresentPageAlert else { return nil }
        } else {
            guard presenter.presentedViewController == nil, presenter.viewIfLoaded?.window != nil else {
                return nil
            }
        }
        let alert = UIAlertController(title: title, message: message, preferredStyle: preferredStyle)
        for spec in textFields {
            alert.addTextField { field in
                field.placeholder = spec.placeholder
                field.text = spec.text
                field.keyboardType = spec.keyboardType
                field.isSecureTextEntry = spec.isSecureTextEntry
                field.accessibilityIdentifier = spec.accessibilityIdentifier
                spec.configure?(field)
            }
        }

        var registered: [DebugAlertActionRegistry.RegisteredAction] = []
        let pageOwner = presenter as? PageViewController

        func addRegisteredAction(title actionTitle: String, style: UIAlertAction.Style,
                                 kind: String, handler: @escaping @MainActor () -> Void) {
            let index = registered.count
            let wrapped: @MainActor () -> Void = { [weak pageOwner, weak alert] in
                if let pageOwner, let alert {
                    pageOwner.dismissPageModal(alert, completion: handler)
                } else {
                    handler()
                }
            }
            alert.addAction(UIAlertAction(title: actionTitle, style: style) { _ in wrapped() })
            registered.append(.init(index: index, label: actionTitle, kind: kind, handler: wrapped))
        }

        for item in actions {
            addRegisteredAction(title: item.title, style: item.style, kind: item.debugKind) {
                item.handler?()
            }
        }

        if let inputSubmit, let submitTitle = inputSubmitTitle {
            let kind = inputSubmitStyle == .destructive ? "destructive" : "default"
            let index = registered.count
            let wrapped: @MainActor () -> Void = { [weak pageOwner, weak alert] in
                // Capture text field values synchronously before dismissal can release the alert.
                let values = (alert?.textFields ?? []).map { $0.text ?? "" }
                if let pageOwner, let alert {
                    pageOwner.dismissPageModal(alert) { inputSubmit(values) }
                } else {
                    inputSubmit(values)
                }
            }
            alert.addAction(UIAlertAction(title: submitTitle, style: inputSubmitStyle) { _ in wrapped() })
            registered.append(.init(index: index, label: submitTitle, kind: kind, handler: wrapped))
        }

        if preferredStyle == .actionSheet, let popover = alert.popoverPresentationController {
            let anchor = sourceView ?? presenter.view!
            popover.sourceView = anchor
            popover.sourceRect = anchor.bounds
        }

        DebugAlertActionRegistry.shared.register(alert: alert, actions: registered)
        DebugRuntime.log(.uiNav, sub: "alert", event: "present",
                         level: (title == "错误") ? .warn : .info,
                         data: .object([
                            "title": .optional(title),
                            "message": .optional(message),
                            "style": .string(preferredStyle == .actionSheet ? "actionSheet" : "alert"),
                            "actionCount": .int(registered.count),
                            "page": .string(String(describing: type(of: presenter)))
                         ]))
        presenter.present(alert, animated: animated)
        guard alert.presentingViewController != nil || presenter.presentedViewController === alert else {
            return nil
        }
        return alert
    }
}

@MainActor
public extension UIViewController {
    @discardableResult
    func presentPageAlert(
        title: String?,
        message: String? = nil,
        actions: [PageAlertAction],
        animated: Bool = true
    ) -> UIAlertController? {
        PageAlertPresenter.present(
            on: self, title: title, message: message,
            preferredStyle: .alert, actions: actions, animated: animated
        )
    }

    @discardableResult
    func presentPageActionSheet(
        title: String? = nil,
        message: String? = nil,
        sourceView: UIView? = nil,
        actions: [PageAlertAction],
        cancelTitle: String = "取消",
        animated: Bool = true
    ) -> UIAlertController? {
        var all = actions
        if !all.contains(where: { $0.style == .cancel }) {
            all.append(PageAlertAction(title: cancelTitle, style: .cancel))
        }
        return PageAlertPresenter.present(
            on: self, title: title, message: message,
            preferredStyle: .actionSheet, sourceView: sourceView,
            actions: all, animated: animated
        )
    }

    @discardableResult
    func presentPageConfirmation(
        title: String,
        message: String? = nil,
        confirmTitle: String = "确认",
        cancelTitle: String = "取消",
        destructive: Bool = false,
        onConfirm: @escaping @MainActor () -> Void,
        onCancel: (@MainActor () -> Void)? = nil
    ) -> UIAlertController? {
        PageAlertPresenter.present(
            on: self,
            title: title,
            message: message,
            preferredStyle: .alert,
            actions: [
                PageAlertAction(title: cancelTitle, style: .cancel, handler: onCancel),
                PageAlertAction(title: confirmTitle, style: destructive ? .destructive : .default, handler: onConfirm),
            ]
        )
    }

    @discardableResult
    func presentPageInputAlert(
        title: String,
        message: String? = nil,
        fields: [PageAlertTextField],
        confirmTitle: String = "确定",
        cancelTitle: String = "取消",
        destructive: Bool = false,
        onConfirm: @escaping @MainActor ([String]) -> Void,
        onCancel: (@MainActor () -> Void)? = nil
    ) -> UIAlertController? {
        PageAlertPresenter.present(
            on: self,
            title: title,
            message: message,
            preferredStyle: .alert,
            textFields: fields,
            actions: [PageAlertAction(title: cancelTitle, style: .cancel, handler: onCancel)],
            inputSubmit: onConfirm,
            inputSubmitTitle: confirmTitle,
            inputSubmitStyle: destructive ? .destructive : .default
        )
    }
}
#endif
