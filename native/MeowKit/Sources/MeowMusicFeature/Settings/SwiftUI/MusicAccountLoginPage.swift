#if os(iOS)
import SwiftUI
import UIKit
import Combine
import MeowUI

@MainActor
final class MusicAccountLoginViewController: MusicSettingsPageViewController<MusicAccountLoginContent> {
    private let owner: MusicViewModel
    private let state: MusicAccountLoginState

    init(
        owner: MusicViewModel,
        service: any MusicAccountsServing,
        platform: MusicAccountPlatform,
        appearance: MusicAppearancePreferencesStore
    ) {
        self.owner = owner
        state = MusicAccountLoginState(owner: owner, service: service, platform: platform)
        super.init(appearance: appearance, content: MusicAccountLoginContent(
            state: state, theme: PageTheme(palette: appearance.pageColorway)
        ))
        title = platform.title
        modalPresentationStyle = .overFullScreen
        modalTransitionStyle = .crossDissolve
    }

    override func loadView() { view = MusicAccountFloatingBackdrop() }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(owner.accessChanges) { [weak self] in
            self?.invalidateErrorPresentation()
            self?.state.reconcileAccess()
        }
        observe(owner.accountChanges) { [weak self] platform in
            self?.state.accountSessionChanged(platform)
        }
        observe(state.model.$error.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            self?.reportError(self?.state.model.error)
        }
        // Published values are delivered on the next main-loop turn, after the state settled.
        observe(state.$signedIn.combineLatest(state.$checkingAccount).map { _ -> Void in }.eraseToAnyPublisher()) {
            [weak self] in self?.finishIfSignedIn()
        }
    }

    override func pageDidBecomeActive() { state.start() }

    override func pageDidBecomeVisible() { reportError(state.model.error) }

    override func pageDidBecomeInactive() { state.close() }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicAccountLoginContent(state: state, theme: theme, shareQR: { [weak self] in
            guard let self, let image = state.image else { return }
            present(UIActivityViewController(activityItems: [image], applicationActivities: nil), animated: true)
        }, close: { [weak self] in
            self?.closeCard()
        }))
    }

    private func finishIfSignedIn() {
        guard state.canFinish else { return }
        closeCard()
    }

    private func closeCard() {
        guard presentingViewController != nil, !isBeingDismissed else { return }
        dismiss(animated: true)
    }
}

@MainActor
struct MusicAccountLoginContent: View {
    @ObservedObject var state: MusicAccountLoginState
    let theme: PageTheme
    var shareQR: () -> Void = {}
    var close: () -> Void = {}

    var body: some View {
        MusicAccountFloatingCard(theme: theme) {
            VStack(alignment: .leading, spacing: 20) {
                MusicAccountCardHeader(
                    platform: state.platform, title: state.platform.title + "登录", theme: theme, close: close
                ) { EmptyView() }
                if state.signedIn {
                    VStack(spacing: 12) {
                        Text(MusicLoginProgress.confirmed.title).font(.headline)
                        if state.checkingAccount { ProgressView().accessibilityLabel("读取账号") }
                    }
                    .frame(maxWidth: .infinity, minHeight: 88)
                } else {
                    if state.supportsSMS {
                        MusicLoginMethodTabs(state: state, theme: theme)
                    }
                    if state.method == .qr {
                        qrContent
                    } else {
                        MusicSMSLoginForm(fields: state.fields, actions: state.actions,
                                          platform: state.platform, theme: theme)
                        if state.canRetry {
                            Button("重试", action: state.retry)
                                .buttonStyle(MusicAccountPrimaryButtonStyle(theme: theme))
                        }
                        if let status = state.status {
                            Text(status)
                                .font(.subheadline)
                                .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                                .frame(maxWidth: .infinity)
                        }
                    }
                }
            }
        }
    }

    private var qrContent: some View {
        VStack(spacing: 16) {
            if let image = state.image {
                Image(uiImage: image)
                    .resizable()
                    .interpolation(.none)
                    .scaledToFit()
                    .frame(width: 200, height: 200)
                    .accessibilityLabel("登录二维码")
            } else if state.available, !state.canRetry {
                ProgressView()
                    .frame(width: 200, height: 200)
                    .accessibilityLabel("读取二维码")
            }
            if let status = state.status {
                Text(status)
                    .font(.subheadline)
                    .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                    .multilineTextAlignment(.center)
            }
            if state.canRetry {
                Button("重试", action: state.retry)
                    .buttonStyle(MusicAccountPrimaryButtonStyle(theme: theme))
            } else if state.image != nil {
                Button("保存二维码", action: shareQR)
                    .buttonStyle(MusicAccountPrimaryButtonStyle(theme: theme))
            }
        }
        .frame(maxWidth: .infinity)
    }
}

@MainActor
private struct MusicLoginMethodTabs: View {
    @ObservedObject var state: MusicAccountLoginState
    let theme: PageTheme

    var body: some View {
        HStack(spacing: 0) {
            tab(.sms, title: "验证码登录")
            tab(.qr, title: "扫码登录")
        }
        .overlay(alignment: .bottom) {
            Rectangle().fill(Color(uiColor: UIColor(theme.border))).frame(height: 1)
        }
        .disabled(!state.available)
    }

    private func tab(_ method: MusicAccountLoginMethod, title: String) -> some View {
        let selected = state.method == method
        return Button { state.selectMethod(method) } label: {
            VStack(spacing: 8) {
                Text(title)
                    .font(.subheadline.weight(selected ? .semibold : .regular))
                    .foregroundStyle(Color(uiColor: UIColor(selected ? theme.ink : theme.muted)))
                Capsule()
                    .fill(selected ? Color(uiColor: UIColor(theme.accent)) : Color.clear)
                    .frame(width: 40, height: 3)
            }
            .frame(maxWidth: .infinity, minHeight: 44)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

private struct MusicSMSRegion: Identifiable {
    let title, code: String
    var id: String { code }
    static let all = [
        MusicSMSRegion(title: "中国大陆", code: "86"), MusicSMSRegion(title: "中国香港", code: "852"),
        MusicSMSRegion(title: "中国澳门", code: "853"), MusicSMSRegion(title: "中国台湾", code: "886")
    ]
}

@MainActor
private struct MusicSMSLoginForm: View {
    @ObservedObject var fields: MusicSMSLoginFieldsState
    @ObservedObject var actions: MusicSMSLoginActionsState
    let platform: MusicAccountPlatform
    let theme: PageTheme

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("手机号").font(.subheadline.weight(.semibold))
            MusicAccountInputCapsule(theme: theme) {
                countryCode
                divider
                TextField("", text: Binding(get: { fields.value.phone }, set: { fields.setPhone($0) }))
                    .keyboardType(.numberPad)
                    .textContentType(.telephoneNumber)
                    .accessibilityLabel("手机号")
                    .padding(.horizontal, 14)
                    .frame(minHeight: 48)
            }
            Text("验证码").font(.subheadline.weight(.semibold)).padding(.top, 6)
            MusicAccountInputCapsule(theme: theme) {
                TextField("", text: Binding(get: { fields.value.code }, set: { fields.setCode($0) }))
                    .keyboardType(.numberPad)
                    .textContentType(.oneTimeCode)
                    .accessibilityLabel("验证码")
                    .padding(.leading, 18)
                    .frame(minHeight: 48)
                divider
                Button(action: actions.send) {
                    Text(sendTitle)
                        .font(.subheadline)
                        .monospacedDigit()
                        .padding(.horizontal, 16)
                        .frame(minWidth: 44, minHeight: 48)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(Color(uiColor: UIColor(actions.value.canSend ? theme.accent : theme.muted)))
                .disabled(!actions.value.canSend)
            }
            Button("登录", action: actions.submit)
                .buttonStyle(MusicAccountPrimaryButtonStyle(theme: theme))
                .disabled(!actions.value.canSubmit)
                .padding(.top, 10)
        }
    }

    private var sendTitle: String {
        if actions.value.resendSeconds > 0 { return "\(actions.value.resendSeconds) 秒后重发" }
        return actions.value.sent ? "重新获取" : "获取验证码"
    }

    private var divider: some View {
        Rectangle().fill(Color(uiColor: UIColor(theme.border))).frame(width: 1, height: 22)
    }

    @ViewBuilder
    private var countryCode: some View {
        let label = Text("+" + fields.value.countryCode).monospacedDigit()
        if platform == .kugou {
            label
                .padding(.horizontal, 18)
                .frame(minHeight: 48)
                .accessibilityLabel("区号 +" + fields.value.countryCode)
        } else {
            Menu {
                ForEach(MusicSMSRegion.all) { region in
                    Button { fields.setCountryCode(region.code) } label: {
                        if region.code == fields.value.countryCode {
                            Label(region.title + "  +" + region.code, systemImage: "checkmark")
                        } else {
                            Text(region.title + "  +" + region.code)
                        }
                    }
                }
            } label: {
                HStack(spacing: 4) {
                    label
                    Image(systemName: "chevron.down").font(.caption2.weight(.semibold))
                }
                .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
                .padding(.horizontal, 16)
                .frame(minWidth: 44, minHeight: 48)
                .contentShape(Rectangle())
            }
            .accessibilityLabel("区号 +" + fields.value.countryCode)
        }
    }
}
#endif
