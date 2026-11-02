#if os(iOS)
import Foundation
import UIKit
import Combine

struct MusicSMSLoginInputs: Equatable {
    let countryCode, phone, code: String
}

@MainActor
final class MusicSMSLoginFieldsState: ObservableObject {
    @Published private(set) var value: MusicSMSLoginInputs
    private let model: MusicAccountsViewModel
    private var subscription: AnyCancellable?

    init(model: MusicAccountsViewModel) {
        self.model = model
        value = .init(countryCode: model.countryCode, phone: model.phone, code: model.code)
        subscription = Publishers.CombineLatest3(model.$countryCode, model.$phone, model.$code)
            .map { country, phone, code in MusicSMSLoginInputs(countryCode: country, phone: phone, code: code) }
            .removeDuplicates()
            .sink { [weak self] in self?.value = $0 }
    }

    func setCountryCode(_ value: String) { model.countryCode = String(value.prefix(4)) }
    func setPhone(_ value: String) { model.phone = String(value.prefix(15)) }
    func setCode(_ value: String) { model.code = String(value.prefix(8)) }
}

struct MusicSMSLoginActionsSnapshot: Equatable {
    let resendSeconds: Int
    let canSend, canSubmit: Bool
    /// A code was requested for the current phone number, so the action reads as a resend.
    var sent = false
}

@MainActor
final class MusicSMSLoginActionsState: ObservableObject {
    @Published private(set) var value: MusicSMSLoginActionsSnapshot
    private let model: MusicAccountsViewModel
    private var subscription: AnyCancellable?
    private var sentPhone: String?

    init(model: MusicAccountsViewModel) {
        self.model = model
        value = .init(resendSeconds: model.resendSeconds, canSend: model.canSendSMS, canSubmit: model.canSubmitSMS)
        let changes = [
            model.$resendSeconds.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$isBusy.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$loginMethod.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$loginPlatform.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$countryCode.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$phone.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$code.map { _ -> Void in }.eraseToAnyPublisher()
        ]
        subscription = Publishers.MergeMany(changes).receive(on: RunLoop.main)
            .sink { [weak self] in self?.refresh() }
    }

    func send() { model.sendSMS() }
    func submit() { model.submitSMS() }

    func refresh() {
        if model.resendSeconds > 0 { sentPhone = model.phone }
        if model.loginPlatform == nil || sentPhone != model.phone { sentPhone = nil }
        let next = MusicSMSLoginActionsSnapshot(
            resendSeconds: model.resendSeconds, canSend: model.canSendSMS, canSubmit: model.canSubmitSMS,
            sent: sentPhone != nil
        )
        if value != next { value = next }
    }
}

@MainActor
final class MusicAccountLoginState: ObservableObject {
    @Published private(set) var method = MusicAccountLoginMethod.qr
    @Published private(set) var image: UIImage?
    @Published private(set) var status: String?
    @Published private(set) var available = false
    @Published private(set) var canRetry = false
    @Published private(set) var signedIn = false
    @Published private(set) var checkingAccount = false
    let fields: MusicSMSLoginFieldsState
    let actions: MusicSMSLoginActionsState
    let supportsSMS: Bool
    let model: MusicAccountsViewModel
    let platform: MusicAccountPlatform
    private let owner: MusicViewModel
    private var started = false
    private var generation: UUID?
    private var imageData: Data?
    private var cachedImage: UIImage?
    private var subscriptions: Set<AnyCancellable> = []

    init(
        owner: MusicViewModel,
        service: any MusicAccountsServing,
        platform: MusicAccountPlatform
    ) {
        self.owner = owner; self.platform = platform
        model = MusicAccountsViewModel(service: service, onSessionChanged: { [weak owner] platform in
            owner?.accountSessionsChanged(platform)
        })
        fields = MusicSMSLoginFieldsState(model: model)
        actions = MusicSMSLoginActionsState(model: model)
        supportsSMS = model.supportsSMS(platform)
        model.$challenge.map { $0?.image }.removeDuplicates()
            .sink { [weak self] in self?.updateImage($0) }
            .store(in: &subscriptions)
        let changes = [
            model.$loginMethod.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$progress.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$isBusy.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$loginPlatform.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$challenge.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$rows.map { _ -> Void in }.eraseToAnyPublisher(),
            model.$submittingSMS.map { _ -> Void in }.eraseToAnyPublisher()
        ]
        Publishers.MergeMany(changes).receive(on: RunLoop.main)
            .sink { [weak self] in self?.refreshPresentation() }
            .store(in: &subscriptions)
        model.$statusesRevision.dropFirst().receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.finishAccountCheck() }
            .store(in: &subscriptions)
        model.$error.compactMap { $0 }.receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.finishAccountCheck() }
            .store(in: &subscriptions)
    }

    var canFinish: Bool {
        started && available && signedIn && !checkingAccount && owner.isAvailable && !owner.isClosed &&
            generation == owner.accountAccessGeneration
    }

    func start() {
        guard !started else { reconcileAccess(); return }
        started = true
        reconcileAccess()
        if available { model.selectLogin(platform) }
        actions.refresh()
        refreshPresentation()
    }

    func reconcileAccess() {
        guard started else { return }
        let next = owner.isAvailable && !owner.isClosed
        let access = owner.accountAccessGeneration
        guard available != next || generation != access else { return }
        let first = generation == nil
        generation = access; available = next
        checkingAccount = false
        if first { model.activate(isAvailable: next, accessGeneration: access) }
        else { model.setAccess(isAvailable: next, accessGeneration: access) }
        actions.refresh()
        refreshPresentation()
    }

    func selectMethod(_ value: MusicAccountLoginMethod) {
        guard available, generation == owner.accountAccessGeneration else { return }
        model.selectMethod(value)
    }

    func retry() {
        guard canRetry, available, generation == owner.accountAccessGeneration else { return }
        model.selectLogin(platform)
    }

    func accountSessionChanged(_ changed: MusicAccountPlatform) {
        guard changed == platform, started, available, owner.isAvailable, !owner.isClosed,
              generation == owner.accountAccessGeneration else { return }
        checkingAccount = true
        model.reloadStatuses()
    }

    func close() {
        started = false; available = false; checkingAccount = false
        model.deactivate()
        imageData = nil; cachedImage = nil; image = nil
        actions.refresh()
    }

    private func finishAccountCheck() {
        guard checkingAccount else { return }
        checkingAccount = false
        refreshPresentation()
    }

    private func updateImage(_ data: Data?) {
        guard let data else { image = nil; return }
        if imageData != data {
            imageData = data
            cachedImage = UIImage(data: data)
        }
        image = cachedImage
    }

    private func refreshPresentation() {
        if method != model.loginMethod { method = model.loginMethod }
        let nextSignedIn = model.rows.first { $0.platform == platform }?.phase == .signedIn
        if signedIn != nextSignedIn { signedIn = nextSignedIn }
        let nextStatus: String? = !available ? MusicAccountStatus.Phase.unavailable.title
            : signedIn ? MusicLoginProgress.confirmed.title
            : method == .qr ? (model.challenge == nil ? nil : model.progress.title)
            : model.submittingSMS ? MusicAccountStatus.Phase.signingIn.title : nil
        if status != nextStatus { status = nextStatus }
        let retry = started && available && !model.isBusy && !signedIn &&
            (model.loginPlatform == nil || method == .qr && (model.challenge == nil || model.progress == .expired))
        if canRetry != retry { canRetry = retry }
    }
}
#endif
