import Foundation
import Combine
import MeowDebug

public struct PanAccountRow: Identifiable, Equatable {
    public let provider: PanProvider
    public let account: PanAccount?
    public let busy: Bool
    public var id: PanProviderID { provider.id }
    public var actions: [PanAccountAction] { account?.status?.actions ?? [] }
    public var statusTitle: String {
        if account?.errorCode == .corruptStorage { return "账号数据损坏" }
        return account?.status?.title ?? "不可用"
    }
}

@MainActor
public final class PanAccountsModel: ObservableObject {
    @Published public private(set) var rows: [PanAccountRow] = []
    @Published private(set) var notice: PanAccountNotice?
    public private(set) var login: PanLoginModel?
    private(set) var closing: Task<Void, Never>?
    private let service: PanAccountService
    private let accountOperations = PanAccountOperations()
    private var providers: [PanProvider] = []
    private var accounts: [PanProviderID: PanAccount] = [:]
    private var tickets: [PanProviderID: UUID] = [:]
    private var pending = Set<PanProviderID>()
    private var operations: [PanProviderID: Task<Void, Never>] = [:]
    private var active = false
    private var loading: Task<Void, Never>?
    private var loadID = UUID()

    public init(service: PanAccountService) { self.service = service }

    /// Entry and foreground read the native owner off the main thread. They neither
    /// wait for retired online operations nor validate credentials upstream.
    public func start() {
        active = true
        notice = nil
        providers = service.providers()
        loading?.cancel()
        let id = UUID(); loadID = id
        loading = Task { [weak self, service] in
            do {
                let current = try await service.accounts()
                guard let self, active, loadID == id else { return }
                accounts = Dictionary(uniqueKeysWithValues: current.map { ($0.provider, $0) })
            } catch {
                guard let self, active, loadID == id else { return }
                accounts.removeAll()
                if !panCancelled(error) { notice = PanAccountNotice(error) }
            }
            self?.publish()
        }
        publish()
    }

    public func close() {
        active = false
        loading?.cancel(); loading = nil; loadID = UUID()
        login?.close(); login = nil
        for task in operations.values { task.cancel() }
        closing = accountOperations.drainAll()
        operations.removeAll(); tickets.removeAll(); pending.removeAll(); publish()
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
                // Only a real authentication failure changes validity. Local
                // projection also observes a newer native edit after a late RPC.
                do {
                    if let account = try await service.accounts().first(where: { $0.provider == provider }) {
                        guard current(provider, ticket) else { return }
                        accounts[provider] = account
                    }
                } catch let reloadError {
                    DebugRuntime.logError(.storage, sub: "pan.accounts", event: "refresh.reload.error", error: reloadError,
                                          data: .object(["provider": .string(provider.rawValue)]))
                }
                if !panCancelled(error) { notice = PanAccountNotice(error) }
            }
            self?.finish(provider, ticket)
        }
        operations[provider] = task
        return task
    }

    public func logout(_ provider: PanProviderID) {
        guard active, accounts[provider] != nil else { return }
        // A deliberate clear supersedes any in-flight refresh/login, including
        // non-cooperative receipts. It is not an optimistic signed-out update.
        if login?.provider.id == provider { closeLogin() }
        operations[provider]?.cancel()
        let ticket = begin(provider)
        let receipt = accountOperations.performLocal(provider) { [weak self, service] in
            guard self?.current(provider, ticket) == true else { return }
            do {
                let account = try await service.clear(provider)
                guard let self, current(provider, ticket) else { return }
                accept(account)
            } catch {
                guard let self, current(provider, ticket) else { return }
                if !panCancelled(error) { notice = PanAccountNotice(error) }
            }
            self?.finish(provider, ticket)
        }
        operations[provider] = Task { _ = await receipt.result }
    }

    public func openLogin(_ provider: PanProviderID) -> PanLoginModel? {
        guard active, login == nil, !pending.contains(provider),
              accounts[provider]?.status != nil,
              let definition = providers.first(where: { $0.id == provider }) else { return nil }
        let ticket = begin(provider)
        let model = PanLoginModel(provider: definition, service: service, operations: accountOperations) { [weak self] account in
            guard let self, current(provider, ticket) else { return }
            accept(account)
            publish()
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
        operations[provider] = nil; pending.remove(provider); publish()
    }
    private func accept(_ account: PanAccount) {
        if let old = accounts[account.provider]?.revision, let revision = account.revision, old > revision { return }
        accounts[account.provider] = account
    }
    private func publish() {
        rows = providers.map { .init(provider: $0, account: accounts[$0.id], busy: pending.contains($0.id)) }
    }
    deinit { loading?.cancel(); for task in operations.values { task.cancel() } }
}
