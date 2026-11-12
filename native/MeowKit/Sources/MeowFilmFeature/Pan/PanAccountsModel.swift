import Foundation
import Combine
import MeowRuntime

public struct PanAccountRow: Identifiable, Equatable {
    public let provider: PanProvider
    public let account: PanAccount?
    public let busy: Bool
    public var id: PanProviderID { provider.id }
    public var actions: [PanAccountAction] { account?.status.actions ?? [] }
}

@MainActor
public final class PanAccountsModel: ObservableObject {
    @Published public private(set) var rows: [PanAccountRow] = []
    @Published public private(set) var loading = false
    @Published private(set) var notice: PanAccountNotice?
    public private(set) var login: PanLoginModel?
    private(set) var closing: Task<Void, Never>?
    private let service: PanAccountService
    private let accountOperations = PanAccountOperations()
    private var providers: [PanProvider] = []
    private var accounts: [PanProviderID: PanAccount] = [:]
    private var tickets: [PanProviderID: UUID] = [:]
    private var pending = Set<PanProviderID>()
    private var clearing = Set<PanProviderID>()
    private var operations: [PanProviderID: Task<Void, Never>] = [:]
    private var reading: Task<Void, Never>?
    private var readTicket = UUID()
    private var active = false

    public init(service: PanAccountService) { self.service = service }

    /// Re-read on entry/foreground. Only configured rows perform account validation.
    @discardableResult
    public func start() -> Task<Void, Never>? {
        active = true
        guard reading == nil else { return reading }
        notice = nil; loading = true
        let ticket = UUID(); readTicket = ticket
        let initialTickets = tickets
        let initialPending = pending
        let drain = accountOperations.drainAll()
        let task = Task { [weak self, service] in
            await drain?.value
            guard self?.active == true, self?.readTicket == ticket, !Task.isCancelled else { return }
            do {
                async let definitions = service.providers()
                async let states = service.accounts()
                let (providers, accounts) = try await (definitions, states)
                guard let self, active, readTicket == ticket, !Task.isCancelled else { return }
                let indexed = Dictionary(uniqueKeysWithValues: accounts.map { ($0.provider, $0) })
                guard providers.allSatisfy({ indexed[$0.id] != nil }) else { throw PanAccountError.invalidResponse }
                self.providers = providers
                for account in accounts where tickets[account.provider] == initialTickets[account.provider] &&
                    !initialPending.contains(account.provider) && !pending.contains(account.provider) {
                    accept(account)
                }
                loading = false; reading = nil; publish()
                let checks = providers.compactMap { provider -> Task<Void, Never>? in
                    guard self.accounts[provider.id]?.configured == true,
                          !initialPending.contains(provider.id),
                          tickets[provider.id] == initialTickets[provider.id] else { return nil }
                    return refresh(provider.id)
                }
                for check in checks { await check.value }
            } catch {
                guard let self, active, readTicket == ticket, !Task.isCancelled else { return }
                loading = false; reading = nil
                if !panCancelled(error) { notice = PanAccountNotice(error) }
                publish()
            }
        }
        reading = task
        return task
    }

    public func close() {
        active = false; readTicket = UUID(); reading?.cancel(); reading = nil; loading = false
        login?.close(); login = nil
        for task in operations.values { task.cancel() }
        closing = accountOperations.drainAll()
        operations.removeAll(); tickets.removeAll(); pending.removeAll(); clearing.removeAll(); publish()
    }

    @discardableResult
    public func refresh(_ provider: PanProviderID) -> Task<Void, Never>? {
        guard active, !pending.contains(provider), accounts[provider]?.configured == true else { return nil }
        let ticket = begin(provider)
        let drain = accountOperations.drain(provider)
        let task = Task { [weak self, service] in
            await drain?.value
            guard self?.current(provider, ticket) == true, !Task.isCancelled else { return }
            do {
                let account = try await service.refresh(provider)
                guard let self, current(provider, ticket) else { return }
                accept(account)
            } catch {
                guard let self, current(provider, ticket) else { return }
                // Runtime owns the classification. An ordinary network error never
                // converts configured credentials into an expired account locally.
                if panAuthenticationFailed(error) {
                    let account = try? await service.accounts().first { $0.provider == provider }
                    guard current(provider, ticket) else { return }
                    if let account { accept(account) }
                }
                if !panCancelled(error) { notice = PanAccountNotice(error) }
            }
            self?.finish(provider, ticket)
        }
        operations[provider] = task
        return task
    }

    @discardableResult
    public func logout(_ provider: PanProviderID) -> Task<Void, Never>? {
        guard active, accounts[provider] != nil, !clearing.contains(provider) else { return nil }
        // A deliberate clear supersedes any in-flight refresh/login, including
        // non-cooperative receipts. It is not an optimistic signed-out update.
        if login?.provider.id == provider { closeLogin() }
        operations[provider]?.cancel()
        let ticket = begin(provider)
        clearing.insert(provider)
        let receipt = accountOperations.perform(provider) { [service] in try await service.clear(provider) }
        let task = Task { [weak self] in
            do {
                let account = try await receipt.value
                guard let self, current(provider, ticket) else { return }
                accept(account)
            } catch {
                guard let self, current(provider, ticket) else { return }
                if !panCancelled(error) { notice = PanAccountNotice(error) }
            }
            self?.finish(provider, ticket)
        }
        operations[provider] = task
        return task
    }

    public func openLogin(_ provider: PanProviderID) -> PanLoginModel? {
        guard active, login == nil, !pending.contains(provider),
              let definition = providers.first(where: { $0.id == provider }) else { return nil }
        let ticket = begin(provider)
        let model = PanLoginModel(provider: definition, service: service, operations: accountOperations) { [weak self] account in
            guard let self, current(provider, ticket) else { return }
            accept(account); publish()
        }
        login = model
        return model
    }

    public func closeLogin() {
        guard let login else { return }
        let provider = login.provider.id
        login.close(); self.login = nil
        closing = accountOperations.drainAll()
        tickets[provider] = UUID(); pending.remove(provider); publish()
    }

    private func begin(_ provider: PanProviderID) -> UUID {
        notice = nil
        let ticket = UUID(); tickets[provider] = ticket; pending.insert(provider); publish()
        return ticket
    }
    private func current(_ provider: PanProviderID, _ ticket: UUID) -> Bool { active && tickets[provider] == ticket }
    private func finish(_ provider: PanProviderID, _ ticket: UUID) {
        guard current(provider, ticket) else { return }
        operations[provider] = nil; pending.remove(provider); clearing.remove(provider); publish()
    }
    private func accept(_ account: PanAccount) {
        if let old = accounts[account.provider], old.revision > account.revision { return }
        accounts[account.provider] = account
    }
    private func publish() {
        rows = providers.map { .init(provider: $0, account: accounts[$0.id], busy: pending.contains($0.id)) }
    }
    deinit { reading?.cancel(); for task in operations.values { task.cancel() } }
}
