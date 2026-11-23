#if os(iOS)
import Combine
import UIKit
import MeowDebug
import MeowUI

/// Pure UIKit pan account login floating card backed by `FloatingCardDialogController`.
@MainActor
final class PanLoginViewController: FloatingCardDialogController {
    let model: PanLoginModel
    private let closeHandler: () -> Void
    private var qrImage: UIImage?
    private var displayedNotice: UUID?
    private var observation: AnyCancellable?

    private let platformMark = AccountPlatformMarkView(symbol: "externaldrive")
    private let methodControl = SettingsSegmentedControl(items: ["扫码登录", "Cookie 登录"])
    private let qrContainer = UIStackView()
    private let qrBox = UIView()
    private let qrImageView = UIImageView()
    private let qrSpinner = UIActivityIndicatorView(style: .medium)
    private let qrProgressLabel = UILabel()
    private let retryButton = UIButton(type: .system)

    private let manualContainer = UIStackView()
    private var manualFields: [(field: PanCredentialField, section: SettingsInputSection, input: SettingsTextField)] = []
    private let saveButton = UIButton(type: .system)

    init(model: PanLoginModel, themeProvider: any PageThemeProviding, close: @escaping () -> Void) {
        self.model = model
        self.closeHandler = close
        super.init(title: model.provider.id.title + "登录", themeProvider: themeProvider)
        model.validateChallenge = { [weak self] challenge in
            self?.qrImage = try PanQRCode.image(challenge)
        }
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "pan.login"
        setHeaderLeading(platformMark)

        if model.provider.login {
            methodControl.accessibilityIdentifier = "pan.login.method"
            methodControl.accessibilityLabel = "登录方式"
            methodControl.addAction(UIAction { [weak self] _ in
                guard let self else { return }
                let method: PanLoginMethod = self.methodControl.selectedSegmentIndex == 0 ? .qr : .manual
                self.model.selectMethod(method)
            }, for: .valueChanged)
            addBodyView(methodControl)
        }

        qrContainer.axis = .vertical
        qrContainer.alignment = .center
        qrContainer.spacing = 16
        qrBox.translatesAutoresizingMaskIntoConstraints = false
        qrImageView.contentMode = .scaleAspectFit
        qrImageView.layer.magnificationFilter = .nearest
        qrImageView.isAccessibilityElement = true
        qrImageView.accessibilityLabel = "登录二维码"
        qrImageView.accessibilityIdentifier = "pan.login.qr"
        qrImageView.translatesAutoresizingMaskIntoConstraints = false
        qrSpinner.accessibilityLabel = "读取二维码"
        qrSpinner.hidesWhenStopped = true
        qrSpinner.translatesAutoresizingMaskIntoConstraints = false
        qrBox.addSubview(qrImageView)
        qrBox.addSubview(qrSpinner)
        NSLayoutConstraint.activate([
            qrBox.widthAnchor.constraint(equalToConstant: 200),
            qrBox.heightAnchor.constraint(equalToConstant: 200),
            qrImageView.topAnchor.constraint(equalTo: qrBox.topAnchor),
            qrImageView.bottomAnchor.constraint(equalTo: qrBox.bottomAnchor),
            qrImageView.leadingAnchor.constraint(equalTo: qrBox.leadingAnchor),
            qrImageView.trailingAnchor.constraint(equalTo: qrBox.trailingAnchor),
            qrSpinner.centerXAnchor.constraint(equalTo: qrBox.centerXAnchor),
            qrSpinner.centerYAnchor.constraint(equalTo: qrBox.centerYAnchor),
        ])
        qrProgressLabel.font = .preferredFont(forTextStyle: .subheadline)
        qrProgressLabel.adjustsFontForContentSizeCategory = true
        qrProgressLabel.numberOfLines = 0
        qrProgressLabel.textAlignment = .center
        registerMutedLabel(qrProgressLabel)
        configurePrimaryButton(retryButton, title: "重试", id: "pan.login.retry") { [weak self] in
            self?.model.retry()
        }
        qrContainer.addArrangedSubview(qrBox)
        qrContainer.addArrangedSubview(qrProgressLabel)
        qrContainer.addArrangedSubview(retryButton)
        addBodyView(qrContainer)

        manualContainer.axis = .vertical
        manualContainer.spacing = 12
        for field in model.provider.credentialFields {
            let input = SettingsTextField()
            input.inputAppearance = .outlined
            input.font = .preferredFont(forTextStyle: .body)
            input.adjustsFontForContentSizeCategory = true
            input.autocapitalizationType = .none
            input.autocorrectionType = .no
            input.smartQuotesType = .no
            input.smartDashesType = .no
            input.isSecureTextEntry = field.isSecret
            input.keyboardType = field.isNumber ? .numberPad : .default
            input.accessibilityLabel = field.title
            input.accessibilityIdentifier = "pan.login.field." + field.rawValue
            input.heightAnchor.constraint(greaterThanOrEqualToConstant: 48).isActive = true
            input.addAction(UIAction { [weak self, weak input] _ in
                guard let self, let input else { return }
                self.model.draft[field.rawValue] = input.text ?? ""
            }, for: .editingChanged)
            let section = SettingsInputSection(title: field.title, input: input)
            registerThemeable(section)
            manualFields.append((field, section, input))
            manualContainer.addArrangedSubview(section)
        }
        configurePrimaryButton(saveButton, title: "保存", id: "pan.login.save") { [weak self] in
            self?.view.endEditing(true)
            self?.model.save()
        }
        manualContainer.addArrangedSubview(saveButton)
        addBodyView(manualContainer)

        observation = Publishers.MergeMany([
            model.$method.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.$challenge.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.$progress.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.$busy.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.$confirmed.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.$saved.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.$notice.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ])
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.render() }
        applyTheme()
        render()
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        if model.completed {
            closeHandler()
        } else {
            model.setVisible(true)
            render()
        }
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        model.setVisible(false)
        qrImage = nil
    }

    override func dialogWillClose() {
        observation?.cancel(); observation = nil
        model.close()
        qrImage = nil
    }

    override func requestDismiss() {
        closeHandler()
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        let settings = theme.settings
        qrSpinner.color = UIColor(settings.muted)
        stylePrimaryButton(retryButton)
        stylePrimaryButton(saveButton)
    }

    private func configurePrimaryButton(_ button: UIButton, title: String, id: String,
                                        action: @escaping @MainActor () -> Void) {
        var config = UIButton.Configuration.filled()
        config.title = title
        config.cornerStyle = .capsule
        config.contentInsets = NSDirectionalEdgeInsets(top: 12, leading: 20, bottom: 12, trailing: 20)
        config.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { incoming in
            var out = incoming
            out.font = .preferredFont(forTextStyle: .body)
            return out
        }
        button.configuration = config
        button.accessibilityIdentifier = id
        button.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        button.addAction(UIAction { _ in action() }, for: .touchUpInside)
    }

    private func stylePrimaryButton(_ button: UIButton) {
        let settings = theme.settings
        button.configuration?.baseBackgroundColor = UIColor(settings.accent)
        button.configuration?.baseForegroundColor = UIColor(settings.onAccent)
    }

    private func render() {
        guard isViewLoaded else { return }
        if model.challenge == nil { qrImage = nil }
        if model.completed, view.window != nil {
            closeHandler()
            return
        }
        if model.provider.login {
            let targetIndex = model.method == .qr ? 0 : 1
            if methodControl.selectedSegmentIndex != targetIndex {
                methodControl.selectedSegmentIndex = targetIndex
            }
            methodControl.isEnabled = !(model.method == .manual && model.busy)
        }

        let isQR = model.method == .qr
        qrContainer.isHidden = !isQR
        manualContainer.isHidden = isQR

        if isQR {
            if model.challenge != nil, let image = qrImage {
                qrImageView.image = image
                qrImageView.isHidden = false
                qrSpinner.stopAnimating()
            } else if model.busy {
                qrImageView.image = nil
                qrImageView.isHidden = true
                qrSpinner.startAnimating()
            } else {
                qrImageView.image = nil
                qrImageView.isHidden = true
                qrSpinner.stopAnimating()
            }
            qrProgressLabel.text = model.progress?.title
            qrProgressLabel.isHidden = (model.progress == nil)
            retryButton.isHidden = !model.canRetry
        } else {
            for entry in manualFields {
                let value = model.draft[entry.field.rawValue] ?? ""
                if !entry.input.isFirstResponder, entry.input.text != value {
                    entry.input.text = value
                }
                entry.input.isEnabled = !model.busy
            }
            saveButton.isEnabled = !model.busy
        }

        if let notice = model.notice, displayedNotice != notice.id {
            setErrorMessage(notice.message)
            displayedNotice = notice.id
        } else if model.notice == nil {
            setErrorMessage(nil)
        }
    }

    // MARK: - DebugDialogControllable

    override var debugDialogTypeName: String { "PanLoginViewController" }

    override func debugDialogActions() -> [DebugDialogActionDescriptor] {
        var items: [DebugDialogActionDescriptor] = [
            DebugDialogActionDescriptor(index: 0, label: "关闭", kind: "cancel", enabled: true)
        ]
        if model.method == .qr && model.canRetry {
            items.append(DebugDialogActionDescriptor(index: 1, label: "重试", kind: "default", enabled: true))
        } else if model.method == .manual {
            items.append(DebugDialogActionDescriptor(index: 1, label: "保存", kind: "default", enabled: !model.busy))
        }
        return items
    }

    override func debugInvokeDialogAction(kind: String?, index: Int?, label: String?) throws -> String {
        if kind == "cancel" || index == 0 || label == "关闭" {
            requestDismiss()
            return "关闭"
        }
        if model.method == .qr && model.canRetry && (kind == "default" || kind == "confirm" || index == 1 || label == "重试") {
            model.retry()
            return "重试"
        }
        if model.method == .manual && (kind == "default" || kind == "confirm" || index == 1 || label == "保存") {
            model.save()
            return "保存"
        }
        throw DebugAPIError.notFound("PanLoginViewController action not available")
    }
}
#endif
