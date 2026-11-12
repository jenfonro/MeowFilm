#if os(iOS)
import SwiftUI
import UIKit
import MeowUI

@MainActor
final class PanLoginViewController: HostingPageViewController<PanLoginContent> {
    let model: PanLoginModel
    private let close: () -> Void
    private let qrImage: PanLoginImage
    private var displayedNotice: UUID?
    init(model: PanLoginModel, themeProvider: any PageThemeProviding, close: @escaping () -> Void) {
        self.model = model; self.close = close
        let image = PanLoginImage(); qrImage = image
        model.validateChallenge = { image.value = try PanQRCode.image($0) }
        super.init(themeProvider: themeProvider, content: PanLoginContent(
            model: model, qrImage: image, theme: PageTheme(palette: themeProvider.pageColorway), close: close))
        modalPresentationStyle = .overFullScreen; modalTransitionStyle = .crossDissolve
    }
    override func loadView() { view = AccountFloatingBackdrop() }
    override var usesPageHeader: Bool { false }
    override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "pan.login"
        observe(model) { [weak self] in
            guard let self else { return }
            if model.challenge == nil { qrImage.value = nil }
            if model.confirmed, isPageVisible { close() } else { showNotice() }
        }
    }
    override func pageDidBecomeVisible() {
        if model.confirmed { close() } else { model.setVisible(true); showNotice() }
    }
    override func pageDidBecomeHidden() { model.setVisible(false); qrImage.value = nil }
    override func pageDidBecomeInactive() { model.close(); qrImage.value = nil }
    override func pageDidFinishAppearing() { showNotice() }
    override func applyTheme() {
        super.applyTheme()
        if isViewLoaded { setContent(PanLoginContent(model: model, qrImage: qrImage, theme: theme, close: close)) }
    }
    private func showNotice() {
        guard let notice = model.notice, displayedNotice != notice.id else { return }
        if showError(notice.message) { displayedNotice = notice.id }
    }
}

@MainActor
final class PanLoginImage {
    var value: UIImage?
}

@MainActor
struct PanLoginContent: View {
    @ObservedObject var model: PanLoginModel
    let qrImage: PanLoginImage
    let theme: PageTheme
    let close: () -> Void
    var body: some View {
        AccountFloatingCard(theme: theme) {
            VStack(alignment: .leading, spacing: 20) {
                AccountCardHeader(title: model.provider.id.title + "登录", theme: theme, close: close) {
                    AccountPlatformMark(symbol: "externaldrive", theme: theme)
                } trailing: { EmptyView() }
                if model.provider.login {
                    Picker("登录方式", selection: Binding(get: { model.method }, set: { model.selectMethod($0) })) {
                        Text("扫码登录").tag(PanLoginMethod.qr)
                        Text("Cookie 登录").tag(PanLoginMethod.manual)
                    }
                    .pickerStyle(.segmented).accessibilityIdentifier("pan.login.method")
                    .disabled(model.method == .manual && model.busy)
                }
                if model.method == .qr { qr } else { manual }
            }
        }
    }
    private var qr: some View {
        VStack(spacing: 16) {
            if model.challenge != nil, let image = qrImage.value {
                Image(uiImage: image).resizable().interpolation(.none).scaledToFit()
                    .frame(width: 200, height: 200).accessibilityLabel("登录二维码")
                    .accessibilityIdentifier("pan.login.qr")
            } else if model.busy {
                ProgressView().frame(width: 200, height: 200).accessibilityLabel("读取二维码")
            }
            if let progress = model.progress {
                Text(progress.title).font(.subheadline).foregroundStyle(Color(uiColor: UIColor(theme.muted)))
            }
            if model.canRetry {
                Button("重试") { model.retry() }.buttonStyle(AccountPrimaryButtonStyle(theme: theme))
                    .accessibilityIdentifier("pan.login.retry")
            }
        }
        .frame(maxWidth: .infinity)
    }
    private var manual: some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(model.provider.credentialFields) { field in
                Text(field.title).font(.subheadline.weight(.semibold))
                AccountInputCapsule(theme: theme) {
                    input(field).padding(.horizontal, 18).frame(minHeight: 48)
                }
            }
            Button("登录") { model.save() }.buttonStyle(AccountPrimaryButtonStyle(theme: theme))
                .disabled(model.busy).accessibilityIdentifier("pan.login.save")
            if model.busy { ProgressView().frame(maxWidth: .infinity).accessibilityLabel("验证账号") }
        }
        .disabled(model.busy)
    }
    @ViewBuilder private func input(_ field: PanCredentialField) -> some View {
        let binding = Binding(get: { model.draft[field.rawValue] ?? "" }, set: { model.draft[field.rawValue] = $0 })
        Group {
            if field.isSecret { SecureField("", text: binding) }
            else { TextField("", text: binding).keyboardType(field.isNumber ? .numberPad : .default) }
        }
        .textInputAutocapitalization(.never).autocorrectionDisabled()
        .accessibilityLabel(field.title).accessibilityIdentifier("pan.login.field." + field.rawValue)
    }
}
#endif
