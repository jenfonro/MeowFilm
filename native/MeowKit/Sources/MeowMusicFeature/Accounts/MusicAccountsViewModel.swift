#if os(iOS) || os(macOS)
import Foundation
import Combine

/// Owns only this page's tasks and login attempt. Product access and the native
/// service remain authoritative; leaving this page never logs out an account.
@MainActor public final class MusicAccountsViewModel: ObservableObject {
    @Published public private(set) var rows: [MusicAccountStatus] = []
    @Published public private(set) var challenge: MusicLoginChallenge?
    @Published public private(set) var progress = MusicLoginProgress.waiting
    @Published public private(set) var isBusy = false
    @Published public private(set) var refreshingPlatform: MusicAccountPlatform?
    @Published public var error: String?
    @Published public private(set) var loginPlatform: MusicAccountPlatform?
    @Published public private(set) var loginMethod = MusicAccountLoginMethod.qr
    @Published public var countryCode = "86"
    @Published public var phone = ""
    @Published public var code = ""
    @Published public private(set) var sendingSMS = false
    @Published public private(set) var submittingSMS = false
    @Published public private(set) var resendSeconds = 0
    @Published private(set) var statusesRevision = UUID()
    private let service: any MusicAccountsServing
    private let sessionRefreshing: (any MusicAccountsRefreshing)?
    private let smsService: (any MusicAccountsSMSServing)?
    private let profileService: (any MusicAccountsProfileServing)?
    private let now: @Sendable () -> Date
    private let sleep: @Sendable (TimeInterval) async throws -> Void
    private let onSessionChanged: @MainActor (MusicAccountPlatform) -> Void
    private var active = false, available = false
    private var foreground = true
    private var smsAttempt: UUID?
    private var retryDates: [MusicAccountPlatform: Date] = [:]
    private var countdown: Task<Void, Never>?, cleanup: Task<Void, Never>?
    private var externalGeneration: UUID?, epoch = UUID(), refreshID = UUID()
    private var operation: Task<Void, Never>?, polling: Task<Void, Never>?
    private var statusReloadRequested = false

    public init(service: any MusicAccountsServing,
                onSessionChanged: @escaping @MainActor (MusicAccountPlatform) -> Void = { _ in },
                clock: @escaping @Sendable () -> Date = { Date() },
                sleep: @escaping @Sendable (TimeInterval) async throws -> Void = {
                    try await Task.sleep(nanoseconds: UInt64($0 * 1_000_000_000))
                }) {
        self.service = service
        self.now = clock; self.sleep = sleep
        self.onSessionChanged = onSessionChanged
        sessionRefreshing = service as? any MusicAccountsRefreshing
        smsService = service as? any MusicAccountsSMSServing
        profileService = service as? any MusicAccountsProfileServing
    }
    public func supportsSMS(_ platform: MusicAccountPlatform) -> Bool { platform != .qq && smsService != nil }
    public var supportsProfileRefresh: Bool { profileService != nil }
    public var supportsSessionRefresh: Bool { sessionRefreshing != nil }
    public func showsSessionRefresh(_ platform: MusicAccountPlatform) -> Bool {
        guard supportsSessionRefresh, active, available,
              let row = rows.first(where: { $0.platform == platform }) else { return false }
        return row.phase == .signedIn || row.phase == .requiresValidation
    }
    public func canRefreshSession(_ platform: MusicAccountPlatform) -> Bool {
        showsSessionRefresh(platform) && !isBusy && loginPlatform == nil && challenge == nil
    }
    public func activate(isAvailable: Bool = true, accessGeneration: UUID? = nil) {
        active = true
        setAccess(isAvailable: isAvailable, accessGeneration: accessGeneration)
        if available { reload() }
    }
    public func setAccess(isAvailable: Bool, accessGeneration: UUID? = nil) {
        guard available != isAvailable || externalGeneration != accessGeneration else { return }
        available = isAvailable; externalGeneration = accessGeneration; invalidate()
        if active && available { reload() }
    }
    public func deactivate() { active = false; invalidate() }
    private func invalidate(keepingRows: Bool = false, keepingPhone: Bool = false) {
        let old = challenge?.id, sms = smsAttempt, service = service, smsService = smsService, previous = cleanup
        epoch = UUID(); refreshID = UUID()
        operation?.cancel(); polling?.cancel(); countdown?.cancel(); operation = nil; polling = nil; countdown = nil
        challenge = nil; if !keepingRows { rows = [] }
        loginPlatform = nil; smsAttempt = nil; code = ""; sendingSMS = false; submittingSMS = false
        resendSeconds = 0
        if !keepingPhone { phone = ""; countryCode = "86" }
        refreshingPlatform = nil; isBusy = false; progress = .waiting; error = nil
        statusReloadRequested = false
        cleanup = Task {
            await previous?.value
            if let old { await service.cancelLogin(challengeID: old) }
            if let sms { await smsService?.cancelSMSLogin(attemptID: sms) }
        }
    }
    private func current(_ own: UUID) -> Bool { active && available && own == epoch && !Task.isCancelled }
    private func refresh(_ own: UUID, enrich: Bool = true) async throws {
        let request = UUID(); refreshID = request
        let values = try await service.statuses()
        guard current(own), refreshID == request else { return }
        rows = values.map {
            $0.phase == .unavailable ? .init(platform: $0.platform, phase: .unavailable) : $0
        }
        if let old = challenge, progress != .expired,
           rows.first(where: { $0.platform == old.platform })?.phase != .signingIn {
            challenge = nil; polling?.cancel(); polling = nil
            let service = service
            Task { await service.cancelLogin(challengeID: old.id) }
        }
        if let platform = loginPlatform, let row = rows.first(where: { $0.platform == platform }),
           [.signedIn, .unavailable, .removalPending].contains(row.phase) {
            let id = smsAttempt; smsAttempt = nil; code = ""; phone = ""; loginPlatform = nil
            countdown?.cancel(); resendSeconds = 0
            if let id { Task { await smsService?.cancelSMSLogin(attemptID: id) } }
        }
        // Local statuses remain read-only in the service. The visible account
        // page explicitly enriches signed-in rows once, never sends an SMS.
        if enrich, foreground, let profileService {
            for row in rows where row.phase == .signedIn && row.profile == nil {
                let trace = MusicRequestTrace(.accountAPI, platform: .init(accountPlatform: row.platform))
                do {
                    let value = try await MusicRequestTrace.$current.withValue(trace) {
                        try await profileService.refreshProfile(platform: row.platform)
                    }
                    guard current(own), refreshID == request else { return }
                    guard value.platform == row.platform else { throw MusicServiceError.invalidResponse }
                    if let i = rows.firstIndex(where: { $0.platform == row.platform }) { rows[i] = value }
                } catch {
                    guard current(own), refreshID == request else { return }
                    trace.failed(error)
                    let failure = MusicServiceError.sanitized(error)
                    if failure != .cancelled {
                        self.error = failure.message(operation: "资料读取", platform: row.platform.title,
                                                     diagnosticID: trace.logicalID)
                    }
                    // Authentication rejection can revoke the coordinator
                    // while loading a profile. Never retain the old signed-in
                    // row; do not retry the failing profile while reconciling.
                    try await refresh(own, enrich: false)
                    return
                }
            }
        }
    }
    private func perform(
        name: String = "读取账号", platform target: MusicAccountPlatform? = nil,
        refreshing platform: MusicAccountPlatform? = nil,
        reconcileOnFailure: Bool = false,
        enrich: Bool = true, clearError: Bool = true,
        sessionChangedPlatform: MusicAccountPlatform? = nil,
        _ body: @escaping @MainActor (UUID) async throws -> Void
    ) {
        guard active, available, !isBusy, !Task.isCancelled else { return }
        let own = epoch
        let selected = target ?? platform ?? loginPlatform
        let trace = MusicRequestTrace(.accountAPI, platform: selected.map { .init(accountPlatform: $0) })
        trace.record(.started)
        isBusy = true
        guard current(own) else { return }
        if clearError { error = nil }
        guard current(own) else { return }
        refreshingPlatform = platform
        guard current(own) else { return }
        operation = Task { [weak self] in
            guard let self, current(own) else { return }
            await MusicRequestTrace.$current.withValue(trace) { [self] in
            await self.cleanup?.value
            guard self.current(own) else { return }
            var startedStatusRead = false
            var dispatched = false, readStatuses = false
            do {
                dispatched = true
                try await body(own)
                if self.current(own) {
                    startedStatusRead = true
                    try await self.refresh(own, enrich: enrich)
                    readStatuses = true
                }
                trace.record(.completed)
            }
            catch {
                if self.current(own) {
                    trace.failed(error)
                    if let failure = error as? MusicSMSFailure {
                        if let target = self.loginPlatform, let date = failure.retryAt { self.retryDates[target] = date; self.updateCountdown() }
                        self.error = [selected?.title, name].compactMap { $0 }.joined(separator: " · ") +
                            "：" + failure.localizedDescription + "（诊断 \(trace.logicalID)）"
                    } else {
                        let failure = MusicServiceError.sanitized(error)
                        if failure != .cancelled {
                            self.error = failure.message(operation: name, platform: selected?.title, diagnosticID: trace.logicalID)
                        }
                    }
                    // Renewal can revoke a rejected/uncertain session. Read
                    // only its status; never retry renewal or auto-validate.
                    if platform != nil || reconcileOnFailure, !startedStatusRead, self.current(own) {
                        try? await self.refresh(own, enrich: false)
                    }
                }
            }
            let queuedStatuses = self.statusReloadRequested
            if self.current(own) {
                self.operation = nil
                self.sendingSMS = false; self.submittingSMS = false
                self.refreshingPlatform = nil
                self.statusReloadRequested = false
                if self.current(own) {
                    self.isBusy = false
                    if !enrich, readStatuses, !queuedStatuses { self.statusesRevision = UUID() }
                }
            }
            if dispatched, let sessionChangedPlatform { self.onSessionChanged(sessionChangedPlatform) }
            if queuedStatuses, self.current(own), !self.isBusy {
                self.reloadStatuses()
            }
            }
        }
    }
    public func reload() { perform { _ in } }
    public func reloadStatuses() {
        guard active, available, !Task.isCancelled else { return }
        if isBusy { statusReloadRequested = true; return }
        perform(enrich: false, clearError: false) { _ in }
    }
    public func refreshProfile(_ platform: MusicAccountPlatform) {
        guard let profileService, loginPlatform == nil else { return }
        perform(name: "资料刷新", platform: platform, reconcileOnFailure: true) { [profileService] _ in
            _ = try await profileService.refreshProfile(platform: platform)
        }
    }
    public func validate(_ platform: MusicAccountPlatform) {
        perform(name: "验证登录会话", platform: platform, reconcileOnFailure: true,
                sessionChangedPlatform: platform) { [service] _ in
            _ = try await service.validate(platform: platform)
        }
    }
    public func refreshSession(_ platform: MusicAccountPlatform) {
        guard canRefreshSession(platform), let sessionRefreshing else { return }
        perform(name: "刷新登录会话", refreshing: platform, sessionChangedPlatform: platform) { [weak self, sessionRefreshing] own in
            guard let self, current(own), showsSessionRefresh(platform), challenge == nil else { return }
            let result = try await sessionRefreshing.refresh(platform: platform)
            guard current(own) else { return }
            guard result.platform == platform else { throw MusicServiceError.invalidResponse }
            // The normal perform -> statuses path owns UI state. Never apply
            // a returned account row directly or revive a cancelled epoch.
        }
    }
    public func cancelSessionRefresh() {
        guard refreshingPlatform != nil else { return }
        invalidate()
        if active && available { reload() }
    }
    public func beginLogin(_ platform: MusicAccountPlatform) {
        guard active, available, foreground, !isBusy else { return }
        if challenge != nil || smsAttempt != nil { invalidate(keepingRows: true, keepingPhone: true) }
        loginPlatform = platform; loginMethod = .qr; code = ""
        perform(name: "获取登录二维码", platform: platform) { [weak self, service] own in
            let value = try await service.beginLogin(platform: platform)
            guard let self, current(own) else { await service.cancelLogin(challengeID: value.id); return }
            guard value.platform == platform else { await service.cancelLogin(challengeID: value.id); throw MusicServiceError.invalidResponse }
            challenge = value; progress = .waiting
            if foreground { poll(value, own: own) }
        }
    }
    public func selectLogin(_ platform: MusicAccountPlatform) {
        guard active, available, foreground else { return }
        invalidate(keepingRows: true)
        loginPlatform = platform; loginMethod = .qr
        beginLogin(platform)
    }
    public func selectMethod(_ method: MusicAccountLoginMethod) {
        guard active, available, foreground, let platform = loginPlatform, method != loginMethod,
              method != .sms || supportsSMS(platform) else { return }
        invalidate(keepingRows: true, keepingPhone: true)
        loginPlatform = platform; loginMethod = method
        if method == .qr { beginLogin(platform) } else { updateCountdown() }
    }
    private var validPhone: Bool {
        guard let platform = loginPlatform,
              digits(countryCode, count: 1...4), countryCode.first != "0", digits(phone, count: 5...15),
              countryCode != "86" || phone.utf8.count == 11 && phone.first == "1" else { return false }
        return platform != .kugou || countryCode == "86"
    }
    private func digits(_ value: String, count: ClosedRange<Int>) -> Bool {
        count.contains(value.utf8.count) && value.utf8.allSatisfy { (48...57).contains($0) }
    }
    public var canSendSMS: Bool {
        active && available && foreground && loginMethod == .sms && validPhone && !isBusy &&
        (loginPlatform.flatMap { retryDates[$0] } ?? .distantPast) <= now()
    }
    public var canSubmitSMS: Bool {
        active && available && foreground && loginMethod == .sms && validPhone && !isBusy &&
        digits(code, count: 4...8)
    }
    public func sendSMS() {
        guard canSendSMS, let platform = loginPlatform, let smsService else { return }
        let id = smsAttempt ?? UUID(); smsAttempt = id
        let country = countryCode, number = phone
        // Reserve UI time before dispatch: switching method during a send must
        // not enable duplicate user sends while its outcome is unknown.
        retryDates[platform] = now().addingTimeInterval(60); updateCountdown()
        sendingSMS = true
        perform(name: "发送验证码", platform: platform, reconcileOnFailure: true) { [weak self, smsService] own in
            let receipt = try await smsService.sendSMS(platform: platform, countryCode: country, phone: number, attemptID: id)
            guard let self, current(own), smsAttempt == id else { await smsService.cancelSMSLogin(attemptID: id); return }
            retryDates[platform] = receipt.retryAt; updateCountdown()
        }
    }
    public func submitSMS() {
        guard canSubmitSMS, let platform = loginPlatform, let smsService else { return }
        let id = smsAttempt ?? UUID(); smsAttempt = id
        let country = countryCode, number = phone, captcha = code
        submittingSMS = true
        perform(name: "验证码登录", platform: platform, reconcileOnFailure: true) { [weak self, smsService] own in
            let value = try await smsService.submitSMS(platform: platform, countryCode: country, phone: number, code: captcha, attemptID: id)
            if value.platform == platform, value.phase == .signedIn { self?.onSessionChanged(platform) }
            guard let self, current(own), smsAttempt == id else { await smsService.cancelSMSLogin(attemptID: id); return }
            guard value.platform == platform, value.phase == .signedIn else { throw MusicServiceError.invalidResponse }
            code = ""; phone = ""; smsAttempt = nil; loginPlatform = nil; countdown?.cancel()
        }
    }
    func updateCountdown() {
        countdown?.cancel(); countdown = nil
        guard active, available, let platform = loginPlatform else { resendSeconds = 0; return }
        resendSeconds = max(0, Int(ceil((retryDates[platform] ?? .distantPast).timeIntervalSince(now()))))
        guard foreground, resendSeconds > 0 else { return }
        let own = epoch
        countdown = Task { [weak self, sleep] in
            do { try await sleep(1) } catch { return }
            guard let self, current(own) else { return }
            updateCountdown()
        }
    }
    public func setForeground(_ value: Bool) {
        guard foreground != value else { return }
        foreground = value; polling?.cancel(); polling = nil; countdown?.cancel(); countdown = nil
        if !value, isBusy { invalidate(keepingRows: true, keepingPhone: true) }
        if value, active, available {
            updateCountdown()
            if let challenge, progress != .expired { poll(challenge, own: epoch, immediately: true) }
            else if loginPlatform == nil { reload() }
        }
    }
    public func cancelLogin() { invalidate(); if active && available { reload() } }
    public func logout(_ platform: MusicAccountPlatform) {
        // Explicit logout invalidates any prior validation/reload immediately,
        // rather than letting that callback refill the old account row.
        invalidate()
        perform(name: "退出登录", platform: platform, sessionChangedPlatform: platform) { [service] _ in
            try await service.logout(platform: platform)
        }
    }
    private func poll(_ value: MusicLoginChallenge, own: UUID, immediately: Bool = false) {
        polling?.cancel()
        polling = Task { [weak self, service, sleep, now] in
            var delay: TimeInterval = immediately ? 0 : 3
            while !Task.isCancelled {
                let trace = MusicRequestTrace(.accountAPI, platform: .init(accountPlatform: value.platform))
                do {
                    if delay > 0 { try await sleep(min(delay, max(0, value.expiresAt.timeIntervalSince(now())))) }
                    guard let self, current(own), foreground, challenge?.id == value.id else { return }
                    if value.expiresAt <= now() {
                        progress = .expired
                        await service.cancelLogin(challengeID: value.id)
                        if current(own) { try? await refresh(own) }
                        return
                    }
                    if immediately && delay == 0 {
                        try await refresh(own)
                        guard current(own), foreground, challenge?.id == value.id else { return }
                    }
                    trace.record(.started)
                    let result = try await MusicRequestTrace.$current.withValue(trace) {
                        try await service.checkLogin(challengeID: value.id)
                    }
                    if result == .confirmed { onSessionChanged(value.platform) }
                    guard current(own), foreground, challenge?.id == value.id else { return }
                    progress = result
                    trace.record(.completed)
                    if result == .confirmed || result == .expired {
                        if result == .confirmed { challenge = nil; loginPlatform = nil; code = ""; phone = "" }
                        if result == .expired { await service.cancelLogin(challengeID: value.id) }
                        if current(own) { try? await refresh(own) }
                        return
                    }
                    delay = 3
                } catch {
                    guard let self, current(own), foreground, challenge?.id == value.id else { return }
                    trace.failed(error)
                    let failure = MusicServiceError.sanitized(error)
                    if failure != .cancelled {
                        self.error = failure.message(operation: "检查二维码登录", platform: value.platform.title,
                                                     diagnosticID: trace.logicalID)
                    }
                    if error as? MusicServiceError == .staleResult {
                        try? await refresh(own)
                        guard current(own), challenge?.id == value.id else { return }
                    }
                    delay = 5 // bounded by the challenge deadline; never regenerate/relogin.
                }
            }
        }
    }
    // Deterministic lifecycle drain for tests; not a timer or a UI polling loop.
    func flush() async { while let pending = operation { await pending.value } }
    var pendingOperation: Task<Void, Never>? { operation }
    var pendingPoll: Task<Void, Never>? { polling }
    deinit { operation?.cancel(); polling?.cancel(); countdown?.cancel() }
}
#endif
