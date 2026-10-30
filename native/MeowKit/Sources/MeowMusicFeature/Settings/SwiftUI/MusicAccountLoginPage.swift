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
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(owner.accessChanges) { [weak self] in
            self?.invalidateErrorPresentation()
            self?.state.reconcileAccess()
        }
        observe(state.model.$error.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            self?.reportError(self?.state.model.error)
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
        }))
    }
}

@MainActor
struct MusicAccountLoginContent: View {
    @ObservedObject var state: MusicAccountLoginState
    let theme: PageTheme
    var shareQR: () -> Void = {}

    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                Picker("登录方式", selection: Binding(
                    get: { state.method },
                    set: { state.selectMethod($0) }
                )) {
                    Text(MusicAccountLoginMethod.qr.title).tag(MusicAccountLoginMethod.qr)
                    if state.supportsSMS {
                        Text(MusicAccountLoginMethod.sms.title).tag(MusicAccountLoginMethod.sms)
                    }
                }
                .pickerStyle(.segmented)
                .disabled(!state.available)
                if state.method == .qr {
                    if let image = state.image {
                        Image(uiImage: image)
                            .resizable()
                            .interpolation(.none)
                            .scaledToFit()
                            .aspectRatio(1, contentMode: .fit)
                            .clipShape(RoundedRectangle(cornerRadius: 12))
                            .accessibilityLabel("登录二维码")
                        Button(action: shareQR) {
                            Text("保存二维码").frame(maxWidth: .infinity, minHeight: 44).contentShape(Rectangle())
                        }
                    }
                } else {
                    MusicSMSLoginFieldsContent(state: state.fields)
                    MusicSMSLoginActionsContent(state: state.actions)
                }
                if state.canRetry {
                    Button(action: state.retry) {
                        Text("重试").frame(maxWidth: .infinity, minHeight: 44).contentShape(Rectangle())
                    }
                }
                if let status = state.status {
                    Text(status)
                        .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                        .multilineTextAlignment(.center)
                }
            }
            .padding(24)
        }
        .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
        .tint(Color(uiColor: UIColor(theme.accent)))
        .background(Color.clear)
    }
}

@MainActor
private struct MusicSMSLoginFieldsContent: View {
    @ObservedObject var state: MusicSMSLoginFieldsState

    var body: some View {
        VStack(spacing: 10) {
            TextField("国家／地区代码", text: Binding(
                get: { state.value.countryCode }, set: { state.setCountryCode($0) }
            ))
            .frame(minHeight: 44)
            TextField("手机号", text: Binding(
                get: { state.value.phone }, set: { state.setPhone($0) }
            ))
            .frame(minHeight: 44)
            TextField("验证码", text: Binding(
                get: { state.value.code }, set: { state.setCode($0) }
            ))
            .frame(minHeight: 44)
        }
        .textFieldStyle(.roundedBorder)
        .keyboardType(.numberPad)
    }
}

@MainActor
private struct MusicSMSLoginActionsContent: View {
    @ObservedObject var state: MusicSMSLoginActionsState

    var body: some View {
        VStack(spacing: 10) {
            Button(action: state.send) {
                Text(state.value.resendSeconds > 0 ? "\(state.value.resendSeconds)秒后重发" : "发送验证码")
                    .frame(maxWidth: .infinity, minHeight: 44)
                    .contentShape(Rectangle())
            }
            .disabled(!state.value.canSend)
            Button(action: state.submit) {
                Text("登录").frame(maxWidth: .infinity, minHeight: 44).contentShape(Rectangle())
            }
            .disabled(!state.value.canSubmit)
        }
    }
}
#endif
