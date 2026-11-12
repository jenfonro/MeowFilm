import Foundation
import Combine
import MeowRuntime

public enum PanLoginMethod: Sendable, Hashable { case qr, manual }

@MainActor
public final class PanLoginModel: ObservableObject {
    public let provider: PanProvider
    @Published public private(set) var method: PanLoginMethod
    @Published public var draft: [String: String] = [:]
    @Published public private(set) var challenge: PanLoginChallenge?
    @Published public private(set) var progress: PanLoginProgress?
    @Published public private(set) var busy = false
    @Published public private(set) var confirmed = false
    @Published private(set) var notice: PanAccountNotice?
    public var validateChallenge: (PanLoginChallenge.Payload) throws -> Void = { _ in }
    private let service: PanAccountService
    private let operations: PanAccountOperations
    private let update: (PanAccount) -> Void
    private let pause: @Sendable () async throws -> Void
    private var generation = UUID()
    private var visible = false
    private var closed = false
    private var starting: Task<Void, Never>?
    private(set) var polling: Task<Void, Never>?
    private var saving: Task<Void, Never>?
    private(set) var closing: Task<Void, Never>?

    public convenience init(provider: PanProvider, service: PanAccountService,
                            pause: @escaping @Sendable () async throws -> Void = { try await Task.sleep(for: .seconds(2)) },
                            update: @escaping (PanAccount) -> Void) {
        self.init(provider: provider, service: service, operations: PanAccountOperations(), pause: pause, update: update)
    }
    init(provider: PanProvider, service: PanAccountService, operations: PanAccountOperations,
         pause: @escaping @Sendable () async throws -> Void = { try await Task.sleep(for: .seconds(2)) },
         update: @escaping (PanAccount) -> Void) {
        self.provider = provider; self.service = service; self.operations = operations
        self.pause = pause; self.update = update
        method = provider.login ? .qr : .manual
    }
    public var canRetry: Bool { method == .qr && !busy && !confirmed && (progress == .expired || progress == .cancelled || notice != nil) }

    public func setVisible(_ value: Bool) {
        guard !closed else { return }
        visible = value
        if value {
            if method == .qr, !confirmed, challenge == nil { start() }
        } else if method == .qr { stopQR() }
    }
    public func selectMethod(_ value: PanLoginMethod) {
        guard !closed, !confirmed, saving == nil, value != method,
              value == .manual || provider.login else { return }
        stopQR(); method = value; notice = nil; progress = nil; draft.removeAll()
        if visible, method == .qr { start() }
    }
    @discardableResult
    public func retry() -> Task<Void, Never>? {
        guard visible, !closed, method == .qr, !confirmed else { return nil }
        stopQR(); return start()
    }
    @discardableResult
    public func start() -> Task<Void, Never>? {
        guard visible, !closed, method == .qr, !confirmed, starting == nil, polling == nil else { return starting }
        notice = nil; busy = true; progress = nil
        let ticket = UUID(); generation = ticket
        let provider = provider.id
        // The shared drain includes a retired start's receipt AND exact cancel.
        // A successor must not reach runtime first and be revoked by this start.
        let receipt = operations.perform(provider) { [weak self, service] in
            guard self?.isCurrent(ticket) == true else { return }
            do {
                let challenge = try await service.start(provider)
                guard let self, isCurrent(ticket) else { try? await service.cancel(challenge.sessionID); return }
                starting = nil
                do { try validateChallenge(challenge.payload) }
                catch { try? await service.cancel(challenge.sessionID); throw error }
                self.challenge = challenge; progress = .pending; busy = false
                beginPolling(challenge, ticket: ticket)
            } catch {
                guard let self, isCurrent(ticket) else { return }
                starting = nil; busy = false
                if panCancelled(error) { progress = .cancelled }
                else { notice = PanAccountNotice(error) }
            }
        }
        let task = Task { _ = await receipt.result }
        starting = task
        return task
    }

    @discardableResult
    public func save() -> Task<Void, Never>? {
        guard visible, !closed, method == .manual, saving == nil, !confirmed else { return nil }
        let credentials: RuntimeValue
        do { credentials = try provider.credentials(from: draft) }
        catch { notice = PanAccountNotice(error); return nil }
        notice = nil; busy = true
        let ticket = UUID(); generation = ticket
        let provider = provider.id
        let receipt = operations.perform(provider) { [service] in
            try await service.replace(provider, credentials: credentials)
        }
        let task = Task { [weak self, service] in
            defer { self?.saving = nil; self?.busy = false }
            do {
                let saved = try await receipt.value
                guard let self, isCurrent(ticket), !Task.isCancelled else { return }
                update(saved)
                let checked = try await service.refresh(provider)
                guard isCurrent(ticket) else { return }
                update(checked)
                guard checked.status == .signedIn else { throw PanAccountError.unconfirmed }
                complete()
            } catch {
                guard let self, isCurrent(ticket) else { return }
                if panAuthenticationFailed(error) {
                    let account = try? await service.accounts().first { $0.provider == provider }
                    guard isCurrent(ticket) else { return }
                    if let account { update(account) }
                }
                if !panCancelled(error) { notice = PanAccountNotice(error) }
            }
        }
        saving = task
        return task
    }

    public func close() {
        guard !closed else { return }
        closed = true; visible = false; stopQR()
        draft.removeAll(); busy = false
    }

    private func beginPolling(_ challenge: PanLoginChallenge, ticket: UUID) {
        polling = Task { [weak self, service, pause] in
            do {
                while !Task.isCancelled {
                    guard self?.isCurrent(ticket) == true else { return }
                    if Date() >= challenge.expiresAt {
                        self?.expire(challenge, ticket: ticket); return
                    }
                    let receipt = try await service.poll(challenge.sessionID, provider: challenge.provider)
                    guard let self, isCurrent(ticket), !Task.isCancelled else { return }
                    progress = receipt.progress
                    switch receipt.progress {
                    case .confirmed:
                        guard let account = receipt.account else { throw PanAccountError.unconfirmed }
                        update(account); complete(); polling = nil; return
                    case .expired, .cancelled:
                        self.challenge = nil; polling = nil
                        cancel(challenge.sessionID); return
                    case .pending, .scanned: break
                    }
                    try await pause()
                }
            } catch {
                guard let self, isCurrent(ticket), !Task.isCancelled else { return }
                polling = nil; self.challenge = nil
                cancel(challenge.sessionID)
                if error as? RuntimeHTTPError == .remote("LOGIN_EXPIRED") { progress = .expired }
                else if panCancelled(error) { progress = .cancelled }
                else { notice = PanAccountNotice(error) }
            }
        }
    }
    private func complete() {
        confirmed = true; progress = .confirmed; challenge = nil; busy = false; draft.removeAll()
    }
    private func expire(_ challenge: PanLoginChallenge, ticket: UUID) {
        guard isCurrent(ticket) else { return }
        self.challenge = nil; polling = nil; progress = .expired; cancel(challenge.sessionID)
    }
    private func isCurrent(_ ticket: UUID) -> Bool { !closed && generation == ticket }
    private func stopQR() {
        generation = UUID(); starting = nil; polling?.cancel(); polling = nil
        if let challenge, !confirmed { cancel(challenge.sessionID) }
        challenge = nil
        closing = operations.drain(provider.id)
        if saving == nil { busy = false }
    }
    private func cancel(_ id: String) {
        let ticket = generation
        _ = operations.perform(provider.id) { [weak self, service] in
            do { try await service.cancel(id) }
            catch { if let self, isCurrent(ticket), !panCancelled(error) { notice = PanAccountNotice(error) } }
        }
    }
    deinit { polling?.cancel() }
}
