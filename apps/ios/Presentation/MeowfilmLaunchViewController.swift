import UIKit
import MeowUI

/// No business models, preferences or runtime are opened on this surface.
@MainActor
final class MeowfilmLaunchViewController: UIViewController {
    private let phase: MeowfilmLaunchController.Phase
    private let retry: () -> Void

    init(phase: MeowfilmLaunchController.Phase, retry: @escaping () -> Void) {
        self.phase = phase; self.retry = retry
        super.init(nibName: nil, bundle: nil)
    }
    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .systemBackground
        let label = UILabel()
        label.font = UIFontMetrics(forTextStyle: .headline).scaledFont(for: .systemFont(ofSize: 17), compatibleWith: traitCollection)
        label.adjustsFontForContentSizeCategory = true
        label.textAlignment = .center; label.numberOfLines = 0
        switch phase {
        case .loading, .ready: label.text = "加载中"
        case .restartRequired: label.text = "等待重新启动"
        case .failed: label.text = "本机数据不可用"
        }
        label.accessibilityIdentifier = "app.launch.status"
        label.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(label)
        NSLayoutConstraint.activate([
            label.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            label.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 24),
            label.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -24)
        ])
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        showStatus()
    }

    func showStatus() {
        guard viewIfLoaded?.window != nil, presentedViewController == nil else { return }
        switch phase {
        case .restartRequired:
            presentPageAlert(
                title: "清除尚未完成",
                message: "请从多任务列表关闭 App 后重新打开。下次启动会先清除本机用户数据，再进入应用。",
                actions: [PageAlertAction(title: "知道了", style: .cancel)]
            )
        case .failed(let error):
            presentPageAlert(
                title: "清除未完成",
                message: error.localizedDescription,
                actions: [
                    PageAlertAction(title: "重试", style: .default) { [retry] in retry() },
                    PageAlertAction(title: "稍后", style: .cancel),
                ]
            )
        case .loading, .ready: return
        }
    }
}
