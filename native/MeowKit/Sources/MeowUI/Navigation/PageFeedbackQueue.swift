#if os(iOS)
import UIKit

/// Single feedback queue and persistent error-source reconciler owned by `PageViewController`.
/// Eliminates per-VC `shownReadError` / `displayedError` / `shownError` shadow dedup fields
/// and ensures active + queued alerts never duplicate or loop.
@MainActor
final class PageFeedbackQueue {
    private enum Key: Equatable {
        case error(String)
        case notice(title: String, message: String?)
    }

    private enum Entry {
        case error(
            message: String,
            sourceKey: String?,
            retry: (@MainActor () -> Void)?,
            acknowledged: (@MainActor () -> Void)?
        )
        case notice(
            title: String,
            message: String?,
            acknowledged: (@MainActor () -> Void)?
        )

        var key: Key {
            switch self {
            case .error(let message, _, _, _): return .error(message)
            case .notice(let title, let message, _): return .notice(title: title, message: message)
            }
        }
    }

    private var queued: [Entry] = []
    private var activeKey: Key?
    private var presentedErrorSources: [String: AnyHashable] = [:]

    func enqueueError(
        _ message: String?,
        sourceKey: String? = nil,
        retry: (@MainActor () -> Void)? = nil,
        acknowledged: (@MainActor () -> Void)? = nil,
        presenter: PageViewController
    ) {
        guard presenter.isPageActive, let message, !message.isEmpty else { return }
        reconcileActive(presenter: presenter)
        let key = Key.error(message)
        if activeKey == key || queued.contains(where: { $0.key == key }) { return }
        queued.append(.error(message: message, sourceKey: sourceKey, retry: retry, acknowledged: acknowledged))
        drain(presenter: presenter)
    }

    func enqueueNotice(
        title: String,
        message: String? = nil,
        acknowledged: (@MainActor () -> Void)? = nil,
        presenter: PageViewController
    ) {
        guard presenter.isPageActive else { return }
        reconcileActive(presenter: presenter)
        let key = Key.notice(title: title, message: message)
        if activeKey == key || queued.contains(where: { $0.key == key }) { return }
        queued.append(.notice(title: title, message: message, acknowledged: acknowledged))
        drain(presenter: presenter)
    }

    func syncErrorSource(
        _ sourceKey: String,
        message: String?,
        token: AnyHashable?,
        retry: (@MainActor () -> Void)?,
        acknowledged: (@MainActor () -> Void)?,
        presenter: PageViewController
    ) {
        guard presenter.isPageActive else { return }
        guard let message, !message.isEmpty else {
            presentedErrorSources.removeValue(forKey: sourceKey)
            return
        }
        let resolvedToken = token ?? AnyHashable(message)
        guard presentedErrorSources[sourceKey] != resolvedToken else { return }
        presentedErrorSources[sourceKey] = resolvedToken
        enqueueError(
            message,
            sourceKey: sourceKey,
            retry: retry,
            acknowledged: acknowledged,
            presenter: presenter
        )
    }

    func clearErrorSource(_ sourceKey: String) {
        presentedErrorSources.removeValue(forKey: sourceKey)
    }

    func clear() {
        queued.removeAll()
        activeKey = nil
        presentedErrorSources.removeAll()
    }

    func drain(presenter: PageViewController) {
        reconcileActive(presenter: presenter)
        guard activeKey == nil, !queued.isEmpty, presenter.canPresentPageAlert else { return }
        let next = queued.removeFirst()
        let key = next.key
        activeKey = key

        let presented: UIAlertController?
        switch next {
        case .error(let message, let sourceKey, let retry, let acknowledged):
            let wrappedRetry: (@MainActor () -> Void)? = retry.map { action in
                { [weak self, weak presenter] in
                    if let sourceKey { self?.presentedErrorSources.removeValue(forKey: sourceKey) }
                    self?.finishActive(key: key, presenter: presenter, after: action)
                }
            }
            var actions: [PageAlertAction] = [
                PageAlertAction(title: wrappedRetry == nil ? "确定" : "取消", style: .cancel) { [weak self, weak presenter] in
                    self?.finishActive(key: key, presenter: presenter, after: acknowledged)
                }
            ]
            if let wrappedRetry {
                actions.append(PageAlertAction(title: "重试", style: .default, handler: wrappedRetry))
            }
            presented = PageAlertPresenter.present(
                on: presenter,
                title: "错误",
                message: message,
                preferredStyle: .alert,
                actions: actions
            )

        case .notice(let title, let message, let acknowledged):
            presented = PageAlertPresenter.present(
                on: presenter,
                title: title,
                message: message,
                actions: [PageAlertAction(title: "确定", style: .default) { [weak self, weak presenter] in
                    self?.finishActive(key: key, presenter: presenter, after: acknowledged)
                }]
            )
        }
        if presented == nil {
            if activeKey == key { activeKey = nil }
            queued.insert(next, at: 0)
        }
    }

    private func finishActive(key: Key, presenter: PageViewController?, after action: (@MainActor () -> Void)?) {
        if activeKey == key { activeKey = nil }
        action?()
        Task { @MainActor [weak self, weak presenter] in
            guard let self, let presenter else { return }
            self.drain(presenter: presenter)
        }
    }

    private func reconcileActive(presenter: PageViewController) {
        if activeKey != nil, !presenter.hasActivePresentedModal {
            activeKey = nil
        }
    }
}
#endif
