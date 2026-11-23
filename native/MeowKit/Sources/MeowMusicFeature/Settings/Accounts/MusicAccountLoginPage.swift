#if os(iOS)
import UIKit
import Combine
import MeowDebug
import MeowUI

private struct MusicSMSRegion {
    let title: String
    let code: String
    static let all: [MusicSMSRegion] = [
        MusicSMSRegion(title: "中国大陆", code: "86"),
        MusicSMSRegion(title: "中国香港", code: "852"),
        MusicSMSRegion(title: "中国澳门", code: "853"),
        MusicSMSRegion(title: "中国台湾", code: "886"),
    ]
}

/// Pure UIKit music platform login floating card backed by `FloatingCardDialogController`.
@MainActor
final class MusicAccountLoginViewController: FloatingCardDialogController {
    private let owner: MusicViewModel
    let state: MusicAccountLoginState
    private var subscriptions: Set<AnyCancellable> = []

    private let platformMark: AccountPlatformMarkView
    private let signedInBox = UIStackView()
    private let signedInLabel = UILabel()
    private let signedInSpinner = UIActivityIndicatorView(style: .medium)

    private let methodControl = SettingsSegmentedControl(items: ["验证码登录", "扫码登录"])

    private let qrContainer = UIStackView()
    private let qrBox = UIView()
    private let qrImageView = UIImageView()
    private let qrSpinner = UIActivityIndicatorView(style: .medium)
    private let qrStatusLabel = UILabel()
    private var qrActionButton: AccountCardButton!

    private let smsContainer = UIStackView()
    private let phoneTitleLabel = UILabel()
    private let phoneCapsule = AccountInputCapsuleView()
    private let regionLabel = UILabel()
    private let regionButton = UIButton(type: .system)
    private let phoneDivider = UIView()
    private let phoneField = UITextField()

    private let codeTitleLabel = UILabel()
    private let codeCapsule = AccountInputCapsuleView()
    private let codeField = UITextField()
    private let codeDivider = UIView()
    private let sendCodeButton = UIButton(type: .system)

    private var submitSMSButton: AccountCardButton!
    private var smsRetryButton: AccountCardButton!
    private let smsStatusLabel = UILabel()
    private var renderedCountryCode: String?

    init(
        owner: MusicViewModel,
        service: any MusicAccountsServing,
        platform: MusicAccountPlatform,
        appearance: MusicAppearancePreferencesStore
    ) {
        self.owner = owner
        state = MusicAccountLoginState(owner: owner, service: service, platform: platform)
        platformMark = AccountPlatformMarkView(assetName: platform.settingsLogoName, symbol: "music.note", size: 34)
        super.init(title: platform.title + "登录", themeProvider: appearance)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "music.accounts.login"
        setHeaderLeading(platformMark)

        buildSignedInView()
        buildMethodTabs()
        buildQRView()
        buildSMSView()
        bindState()
        applyTheme()
        render()
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        state.start()
        render()
        finishIfSignedIn()
    }

    override func dialogWillClose() {
        super.dialogWillClose()
        subscriptions.removeAll()
        state.close()
    }

    override func requestDismiss() {
        closeCard()
    }

    private func buildSignedInView() {
        signedInBox.axis = .vertical
        signedInBox.alignment = .center
        signedInBox.spacing = 12
        signedInLabel.text = MusicLoginProgress.confirmed.title
        signedInLabel.font = .preferredFont(forTextStyle: .body)
        signedInLabel.adjustsFontForContentSizeCategory = true
        registerPrimaryLabel(signedInLabel)
        signedInSpinner.hidesWhenStopped = true
        signedInSpinner.accessibilityLabel = "读取账号"
        signedInBox.addArrangedSubview(signedInLabel)
        signedInBox.addArrangedSubview(signedInSpinner)
        signedInBox.heightAnchor.constraint(greaterThanOrEqualToConstant: 88).isActive = true
        addBodyView(signedInBox)
    }

    private func buildMethodTabs() {
        methodControl.accessibilityIdentifier = "music.accounts.login.method"
        methodControl.accessibilityLabel = "登录方式"
        methodControl.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            let method: MusicAccountLoginMethod = methodControl.selectedSegmentIndex == 0 ? .sms : .qr
            state.selectMethod(method)
        }, for: .valueChanged)
        addBodyView(methodControl)
    }

    private func buildQRView() {
        qrContainer.axis = .vertical
        qrContainer.alignment = .center
        qrContainer.spacing = 16

        qrBox.translatesAutoresizingMaskIntoConstraints = false
        qrImageView.contentMode = .scaleAspectFit
        qrImageView.layer.magnificationFilter = .nearest
        qrImageView.isAccessibilityElement = true
        qrImageView.accessibilityLabel = "登录二维码"
        qrImageView.accessibilityIdentifier = "music.accounts.login.qr"
        qrImageView.translatesAutoresizingMaskIntoConstraints = false
        qrSpinner.hidesWhenStopped = true
        qrSpinner.accessibilityLabel = "读取二维码"
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

        qrStatusLabel.font = .preferredFont(forTextStyle: .subheadline)
        qrStatusLabel.adjustsFontForContentSizeCategory = true
        qrStatusLabel.numberOfLines = 0
        qrStatusLabel.textAlignment = .center
        registerMutedLabel(qrStatusLabel)

        qrActionButton = AccountCardButton(title: "保存二维码", style: .primary, theme: theme) { [weak self] in
            guard let self else { return }
            if state.canRetry {
                state.retry()
            } else if let image = state.image {
                presentQRShare(image)
            }
        }
        qrActionButton.accessibilityIdentifier = "music.accounts.login.qrAction"
        registerThemeable(qrActionButton)

        qrContainer.addArrangedSubview(qrBox)
        qrContainer.addArrangedSubview(qrStatusLabel)
        qrContainer.addArrangedSubview(qrActionButton)
        qrActionButton.widthAnchor.constraint(equalTo: qrContainer.widthAnchor).isActive = true
        addBodyView(qrContainer)
    }

    private func buildSMSView() {
        smsContainer.axis = .vertical
        smsContainer.spacing = 10

        phoneTitleLabel.text = "手机号"
        phoneTitleLabel.font = .preferredFont(forTextStyle: .body)
        phoneTitleLabel.adjustsFontForContentSizeCategory = true
        registerPrimaryLabel(phoneTitleLabel)
        smsContainer.addArrangedSubview(phoneTitleLabel)

        registerThemeable(phoneCapsule)
        phoneDivider.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            phoneDivider.widthAnchor.constraint(equalToConstant: 1),
            phoneDivider.heightAnchor.constraint(equalToConstant: 22),
        ])

        if state.platform == .kugou {
            regionLabel.font = .monospacedDigitSystemFont(ofSize: UIFont.preferredFont(forTextStyle: .body).pointSize, weight: .regular)
            regionLabel.adjustsFontForContentSizeCategory = true
            regionLabel.textAlignment = .center
            regionLabel.widthAnchor.constraint(greaterThanOrEqualToConstant: 64).isActive = true
            regionLabel.heightAnchor.constraint(greaterThanOrEqualToConstant: 48).isActive = true
            registerPrimaryLabel(regionLabel)
            phoneCapsule.stack.addArrangedSubview(regionLabel)
        } else {
            var config = UIButton.Configuration.plain()
            config.image = UIImage(
                systemName: "chevron.down",
                withConfiguration: UIImage.SymbolConfiguration(pointSize: 11, weight: .regular)
            )
            config.imagePlacement = .trailing
            config.imagePadding = 4
            config.contentInsets = NSDirectionalEdgeInsets(top: 0, leading: 16, bottom: 0, trailing: 14)
            config.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { incoming in
                var out = incoming
                out.font = .monospacedDigitSystemFont(ofSize: UIFont.preferredFont(forTextStyle: .body).pointSize, weight: .regular)
                return out
            }
            regionButton.configuration = config
            regionButton.showsMenuAsPrimaryAction = true
            regionButton.setContentHuggingPriority(.required, for: .horizontal)
            regionButton.setContentCompressionResistancePriority(.required, for: .horizontal)
            regionButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 48).isActive = true
            phoneCapsule.stack.addArrangedSubview(regionButton)
        }
        phoneCapsule.stack.addArrangedSubview(phoneDivider)

        phoneField.keyboardType = .numberPad
        phoneField.textContentType = .telephoneNumber
        phoneField.font = .preferredFont(forTextStyle: .body)
        phoneField.adjustsFontForContentSizeCategory = true
        phoneField.accessibilityLabel = "手机号"
        phoneField.accessibilityIdentifier = "music.accounts.login.phone"
        phoneField.leftView = UIView(frame: CGRect(x: 0, y: 0, width: 14, height: 1))
        phoneField.leftViewMode = .always
        phoneField.rightView = UIView(frame: CGRect(x: 0, y: 0, width: 14, height: 1))
        phoneField.rightViewMode = .always
        phoneField.heightAnchor.constraint(greaterThanOrEqualToConstant: 48).isActive = true
        phoneField.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            state.fields.setPhone(phoneField.text ?? "")
        }, for: .editingChanged)
        phoneCapsule.stack.addArrangedSubview(phoneField)
        smsContainer.addArrangedSubview(phoneCapsule)

        codeTitleLabel.text = "验证码"
        codeTitleLabel.font = .preferredFont(forTextStyle: .body)
        codeTitleLabel.adjustsFontForContentSizeCategory = true
        registerPrimaryLabel(codeTitleLabel)
        smsContainer.setCustomSpacing(16, after: phoneCapsule)
        smsContainer.addArrangedSubview(codeTitleLabel)

        registerThemeable(codeCapsule)
        codeDivider.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            codeDivider.widthAnchor.constraint(equalToConstant: 1),
            codeDivider.heightAnchor.constraint(equalToConstant: 22),
        ])

        codeField.keyboardType = .numberPad
        codeField.textContentType = .oneTimeCode
        codeField.font = .preferredFont(forTextStyle: .body)
        codeField.adjustsFontForContentSizeCategory = true
        codeField.accessibilityLabel = "验证码"
        codeField.accessibilityIdentifier = "music.accounts.login.code"
        codeField.leftView = UIView(frame: CGRect(x: 0, y: 0, width: 18, height: 1))
        codeField.leftViewMode = .always
        codeField.rightView = UIView(frame: CGRect(x: 0, y: 0, width: 10, height: 1))
        codeField.rightViewMode = .always
        codeField.heightAnchor.constraint(greaterThanOrEqualToConstant: 48).isActive = true
        codeField.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            state.fields.setCode(codeField.text ?? "")
        }, for: .editingChanged)

        var sendConfig = UIButton.Configuration.plain()
        sendConfig.contentInsets = NSDirectionalEdgeInsets(top: 0, leading: 16, bottom: 0, trailing: 16)
        sendConfig.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { incoming in
            var out = incoming
            out.font = .monospacedDigitSystemFont(ofSize: UIFont.preferredFont(forTextStyle: .body).pointSize, weight: .regular)
            return out
        }
        sendCodeButton.configuration = sendConfig
        sendCodeButton.accessibilityIdentifier = "music.accounts.login.sendCode"
        sendCodeButton.setContentHuggingPriority(.required, for: .horizontal)
        sendCodeButton.setContentCompressionResistancePriority(.required, for: .horizontal)
        sendCodeButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 48).isActive = true
        sendCodeButton.addAction(UIAction { [weak self] _ in
            self?.state.actions.send()
        }, for: .touchUpInside)

        codeCapsule.stack.addArrangedSubview(codeField)
        codeCapsule.stack.addArrangedSubview(codeDivider)
        codeCapsule.stack.addArrangedSubview(sendCodeButton)
        smsContainer.addArrangedSubview(codeCapsule)

        submitSMSButton = AccountCardButton(title: "登录", style: .primary, theme: theme) { [weak self] in
            self?.state.actions.submit()
        }
        submitSMSButton.accessibilityIdentifier = "music.accounts.login.submit"
        registerThemeable(submitSMSButton)
        smsContainer.setCustomSpacing(18, after: codeCapsule)
        smsContainer.addArrangedSubview(submitSMSButton)

        smsRetryButton = AccountCardButton(title: "重试", style: .primary, theme: theme) { [weak self] in
            self?.state.retry()
        }
        smsRetryButton.accessibilityIdentifier = "music.accounts.login.retry"
        registerThemeable(smsRetryButton)
        smsContainer.addArrangedSubview(smsRetryButton)

        smsStatusLabel.font = .preferredFont(forTextStyle: .subheadline)
        smsStatusLabel.adjustsFontForContentSizeCategory = true
        smsStatusLabel.numberOfLines = 0
        smsStatusLabel.textAlignment = .center
        registerMutedLabel(smsStatusLabel)
        smsContainer.addArrangedSubview(smsStatusLabel)

        addBodyView(smsContainer)
    }

    private func bindState() {
        owner.accessChanges.receive(on: RunLoop.main).sink { [weak self] in
            self?.state.reconcileAccess()
            self?.render()
        }.store(in: &subscriptions)

        owner.accountChanges.receive(on: RunLoop.main).sink { [weak self] platform in
            self?.state.accountSessionChanged(platform)
        }.store(in: &subscriptions)

        let updates = Publishers.MergeMany([
            state.$method.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$signedIn.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$checkingAccount.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$status.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$canRetry.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$image.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.fields.$value.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.actions.$value.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.model.$error.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ])
        updates.receive(on: RunLoop.main).sink { [weak self] in
            guard let self else { return }
            render()
            finishIfSignedIn()
        }.store(in: &subscriptions)
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        let settings = theme.settings
        signedInSpinner.color = UIColor(settings.muted)
        qrSpinner.color = UIColor(settings.muted)
        phoneDivider.backgroundColor = UIColor(settings.border)
        codeDivider.backgroundColor = UIColor(settings.border)
        phoneField.textColor = UIColor(settings.ink)
        phoneField.tintColor = UIColor(settings.accent)
        codeField.textColor = UIColor(settings.ink)
        codeField.tintColor = UIColor(settings.accent)
        regionButton.configuration?.baseForegroundColor = UIColor(settings.ink)
        updateSendCodeAppearance()
    }

    private func updateSendCodeAppearance() {
        let settings = theme.settings
        let actions = state.actions.value
        sendCodeButton.configuration?.baseForegroundColor = UIColor(actions.canSend ? settings.accent : settings.muted)
    }

    private func render() {
        guard isViewLoaded else { return }
        setErrorMessage(state.model.error)

        signedInBox.isHidden = !state.signedIn
        if state.signedIn {
            if state.checkingAccount { signedInSpinner.startAnimating() }
            else { signedInSpinner.stopAnimating() }
            methodControl.isHidden = true
            qrContainer.isHidden = true
            smsContainer.isHidden = true
            return
        }
        signedInSpinner.stopAnimating()

        methodControl.isHidden = !state.supportsSMS
        methodControl.isEnabled = state.isActive
        let selectedIndex = state.method == .sms ? 0 : 1
        if methodControl.selectedSegmentIndex != selectedIndex {
            methodControl.selectedSegmentIndex = selectedIndex
        }

        let isQR = state.method == .qr
        qrContainer.isHidden = !isQR
        smsContainer.isHidden = isQR

        if isQR {
            if let image = state.image {
                qrImageView.image = image
                qrImageView.isHidden = false
                qrSpinner.stopAnimating()
            } else if state.isActive, !state.canRetry {
                qrImageView.image = nil
                qrImageView.isHidden = true
                qrSpinner.startAnimating()
            } else {
                qrImageView.image = nil
                qrImageView.isHidden = true
                qrSpinner.stopAnimating()
            }
            qrStatusLabel.text = state.status
            qrStatusLabel.isHidden = (state.status == nil)
            if state.canRetry {
                qrActionButton.setTitle("重试", for: .normal)
                qrActionButton.isHidden = false
            } else if state.image != nil {
                qrActionButton.setTitle("保存二维码", for: .normal)
                qrActionButton.isHidden = false
            } else {
                qrActionButton.isHidden = true
            }
        } else {
            let fields = state.fields.value
            let actions = state.actions.value
            let regionText = "+" + fields.countryCode
            if state.platform == .kugou {
                if regionLabel.text != regionText {
                    regionLabel.text = regionText
                    regionLabel.accessibilityLabel = "区号 " + regionText
                }
            } else if renderedCountryCode != fields.countryCode {
                renderedCountryCode = fields.countryCode
                regionButton.configuration?.title = regionText
                regionButton.accessibilityLabel = "区号 " + regionText
                regionButton.menu = UIMenu(children: MusicSMSRegion.all.map { region in
                    UIAction(
                        title: region.title + "  +" + region.code,
                        state: region.code == fields.countryCode ? .on : .off
                    ) { [weak self] _ in
                        self?.state.fields.setCountryCode(region.code)
                    }
                })
            }
            if !phoneField.isFirstResponder, phoneField.text != fields.phone {
                phoneField.text = fields.phone
            }
            if !codeField.isFirstResponder, codeField.text != fields.code {
                codeField.text = fields.code
            }
            let sendTitle: String
            if actions.resendSeconds > 0 {
                sendTitle = "\(actions.resendSeconds) 秒后重发"
            } else {
                sendTitle = actions.sent ? "重新获取" : "获取验证码"
            }
            if sendCodeButton.configuration?.title != sendTitle {
                sendCodeButton.configuration?.title = sendTitle
            }
            sendCodeButton.isEnabled = actions.canSend
            updateSendCodeAppearance()
            submitSMSButton.isEnabled = actions.canSubmit
            smsRetryButton.isHidden = !state.canRetry
            smsStatusLabel.text = state.status
            smsStatusLabel.isHidden = (state.status == nil)
        }
    }

    private func finishIfSignedIn() {
        guard state.canFinish else { return }
        closeCard()
    }

    private func presentQRShare(_ image: UIImage) {
        guard presentedViewController == nil, view.window != nil else { return }
        let activity = UIActivityViewController(activityItems: [image], applicationActivities: nil)
        if let popover = activity.popoverPresentationController {
            popover.sourceView = qrActionButton
            popover.sourceRect = qrActionButton.bounds
        }
        present(activity, animated: true)
    }

    private func closeCard() {
        guard presentingViewController != nil, !isBeingDismissed else { return }
        dismissSelf(animated: true)
    }

    // MARK: - DebugDialogControllable

    override func debugDialogActions() -> [DebugDialogActionDescriptor] {
        var actions: [DebugDialogActionDescriptor] = [
            DebugDialogActionDescriptor(index: 0, label: "关闭", kind: "cancel", enabled: true)
        ]
        if !state.signedIn {
            if state.method == .qr {
                if state.canRetry {
                    actions.append(DebugDialogActionDescriptor(index: actions.count, label: "重试", kind: "default", enabled: true))
                } else if state.image != nil {
                    actions.append(DebugDialogActionDescriptor(index: actions.count, label: "保存二维码", kind: "default", enabled: true))
                }
            } else {
                actions.append(DebugDialogActionDescriptor(index: actions.count, label: "登录", kind: "default", enabled: state.actions.value.canSubmit))
                if state.canRetry {
                    actions.append(DebugDialogActionDescriptor(index: actions.count, label: "重试", kind: "default", enabled: true))
                }
            }
        }
        return actions
    }

    override func debugInvokeDialogAction(kind: String?, index: Int?, label: String?) throws -> String {
        let actions = debugDialogActions()
        let chosen: DebugDialogActionDescriptor?
        if let index {
            chosen = actions.first { $0.index == index }
        } else if let label {
            chosen = actions.first { $0.label == label }
        } else if let kind {
            chosen = actions.first { $0.kind == kind }
        } else {
            chosen = actions.first
        }
        guard let chosen else {
            throw DebugAPIError.notFound("Dialog action not found")
        }
        guard chosen.enabled else {
            throw DebugAPIError.invalidState("Dialog action '\(chosen.label)' is disabled")
        }
        switch chosen.label {
        case "关闭":
            closeCard()
        case "重试":
            state.retry()
        case "登录":
            state.actions.submit()
        case "保存二维码":
            if let image = state.image {
                presentQRShare(image)
            }
        default:
            break
        }
        return chosen.label
    }
}
#endif
