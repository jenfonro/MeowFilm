#if os(iOS)
import UIKit

public struct PageNoticeItem {
    public let key: String
    public let text: String
    public let retry: (@MainActor () -> Void)?
    public let acknowledged: (@MainActor () -> Void)?

    public init(
        key: String,
        text: String,
        retry: (@MainActor () -> Void)? = nil,
        acknowledged: (@MainActor () -> Void)? = nil
    ) {
        self.key = key
        self.text = text
        self.retry = retry
        self.acknowledged = acknowledged
    }
}

@MainActor
public protocol PageNoticeHost: AnyObject {
    func enqueueNotice(_ item: PageNoticeItem)
}

/// Persistent error-source deduplicator and notice dispatcher for `PageViewController`.
@MainActor
final class PageFeedbackQueue {
    private var presentedErrorSources: [String: AnyHashable] = [:]

    func enqueueError(
        _ message: String?,
        sourceKey: String? = nil,
        retry: (@MainActor () -> Void)? = nil,
        acknowledged: (@MainActor () -> Void)? = nil,
        presenter: PageViewController
    ) {
        guard presenter.isPageActive,
              let text = message?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty,
              let host = presenter.noticeHost else { return }
        let wrappedRetry: (@MainActor () -> Void)? = retry.map { action in
            { [weak self] in
                if let sourceKey { self?.presentedErrorSources.removeValue(forKey: sourceKey) }
                action()
            }
        }
        host.enqueueNotice(PageNoticeItem(key: "e:\(text)", text: text, retry: wrappedRetry, acknowledged: acknowledged))
    }

    func enqueueNotice(
        title: String,
        message: String? = nil,
        acknowledged: (@MainActor () -> Void)? = nil,
        presenter: PageViewController
    ) {
        guard presenter.isPageActive, let host = presenter.noticeHost else { return }
        let t = title.trimmingCharacters(in: .whitespacesAndNewlines)
        let m = message?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let text = (m.isEmpty || m == t) ? t : ((t.isEmpty || t == "错误") ? m : "\(t)：\(m)")
        guard !text.isEmpty else { return }
        host.enqueueNotice(PageNoticeItem(key: "n:\(text)", text: text, acknowledged: acknowledged))
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
        enqueueError(message, sourceKey: sourceKey, retry: retry, acknowledged: acknowledged, presenter: presenter)
    }

    func clearErrorSource(_ sourceKey: String) { presentedErrorSources.removeValue(forKey: sourceKey) }
    func clear() { presentedErrorSources.removeAll() }
}

/// Permanent top-center non-blocking slide-down notice overlay slot owned by the root container.
@MainActor
public final class PageNoticeBannerView: UIView, PageThemeable {
    private let label = UILabel()
    private let retryButton = UIButton(type: .system)
    private var queue: [PageNoticeItem] = []
    private var current: PageNoticeItem?
    private var timer: Timer?

    public override init(frame: CGRect) {
        super.init(frame: frame)
        accessibilityIdentifier = "page.notice.banner"
        isHidden = true
        alpha = 0
        transform = CGAffineTransform(translationX: 0, y: -20)
        layer.cornerRadius = 12
        layer.cornerCurve = .continuous
        layer.borderWidth = 1
        layer.shadowColor = UIColor.black.cgColor
        layer.shadowOpacity = 0.10
        layer.shadowRadius = 10
        layer.shadowOffset = CGSize(width: 0, height: 4)

        label.font = .systemFont(ofSize: 14)
        label.numberOfLines = 2
        label.lineBreakMode = .byTruncatingTail

        var cfg = UIButton.Configuration.plain()
        cfg.title = "重试"
        cfg.contentInsets = NSDirectionalEdgeInsets(top: 6, leading: 8, bottom: 6, trailing: 4)
        cfg.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var a = $0; a.font = .systemFont(ofSize: 14); return a
        }
        retryButton.configuration = cfg
        retryButton.accessibilityIdentifier = "page.notice.retry"
        retryButton.setContentHuggingPriority(.required, for: .horizontal)
        retryButton.setContentCompressionResistancePriority(.required, for: .horizontal)
        retryButton.addAction(UIAction { [weak self] _ in self?.settle(runRetry: true) }, for: .touchUpInside)

        let row = UIStackView(arrangedSubviews: [label, retryButton])
        row.alignment = .center
        row.spacing = 10
        row.translatesAutoresizingMaskIntoConstraints = false
        addSubview(row)
        NSLayoutConstraint.activate([
            row.topAnchor.constraint(equalTo: topAnchor, constant: 10),
            row.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -10),
            row.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            row.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
        ])
        addGestureRecognizer(UITapGestureRecognizer(target: self, action: #selector(dismissTapped)))
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func hitTest(_ point: CGPoint, with event: UIEvent?) -> UIView? {
        guard !isHidden, alpha > 0.01, bounds.contains(point) else { return nil }
        if !retryButton.isHidden, retryButton.bounds.insetBy(dx: -6, dy: -8).contains(convert(point, to: retryButton)) {
            return retryButton
        }
        return self
    }

    public func applyTheme(_ theme: PageTheme) {
        let s = theme.settings
        backgroundColor = UIColor(s.surface)
        layer.borderColor = UIColor(s.border).cgColor
        label.textColor = UIColor(s.ink)
        retryButton.configuration?.baseForegroundColor = UIColor(s.accent)
    }

    public func enqueue(_ item: PageNoticeItem) {
        if current?.key == item.key || queue.contains(where: { $0.key == item.key }) { return }
        queue.append(item)
        if current == nil { presentNext() }
    }

    @objc private func dismissTapped() { settle(runRetry: false) }

    private func presentNext() {
        guard current == nil, !queue.isEmpty, let superview else {
            if current == nil, queue.isEmpty { isHidden = true }
            return
        }
        let item = queue.removeFirst()
        current = item
        label.text = item.text
        accessibilityLabel = item.text
        retryButton.isHidden = item.retry == nil
        isHidden = false
        superview.bringSubviewToFront(self)
        UIView.animate(withDuration: UIAccessibility.isReduceMotionEnabled ? 0 : 0.22,
                       delay: 0, options: [.curveEaseOut, .allowUserInteraction]) {
            self.alpha = 1
            self.transform = .identity
        }
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: item.retry == nil ? 2.4 : 3.6, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.settle(runRetry: false) }
        }
    }

    private func settle(runRetry: Bool) {
        guard let item = current else { return }
        timer?.invalidate()
        timer = nil
        current = nil
        let callback = runRetry ? item.retry : item.acknowledged
        if !queue.isEmpty {
            callback?()
            presentNext()
            return
        }
        UIView.animate(withDuration: UIAccessibility.isReduceMotionEnabled ? 0 : 0.18,
                       delay: 0, options: [.curveEaseIn, .beginFromCurrentState]) {
            self.alpha = 0
            self.transform = CGAffineTransform(translationX: 0, y: -20)
        } completion: { _ in
            if self.current == nil, self.queue.isEmpty { self.isHidden = true }
            callback?()
            self.presentNext()
        }
    }

    deinit { timer?.invalidate() }
}
#endif
