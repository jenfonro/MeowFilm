import Foundation
import CryptoKit
import MeowRuntime
import MeowStorage

/// Typed account rows are the credential authority. This cache holds only online
/// display outcomes, bound to the exact native record, and is never persisted.
final class PanSettingsStore: @unchecked Sendable {
    private struct Verified {
        let signature: Data
        let account: PanAccount
    }
    private let repository: FilmSettingsRepository
    private let lock = NSLock()
    private var verified: [PanProviderID: Verified] = [:]

    init(repository: FilmSettingsRepository) { self.repository = repository }

    func accounts() async throws -> [PanAccount] {
        let byProvider = try await readMap()
        return try PanProviderID.allCases.map { id in
            let account: PanAccount, signature: Data
            do {
                account = try Self.snapshot(byProvider[id.rawValue], id)
                signature = try Self.signature(byProvider[id.rawValue], id)
            } catch {
                guard error as? RuntimeHTTPError == .remote("CORRUPT_STORAGE") else { throw error }
                lock.lock(); verified[id] = nil; lock.unlock()
                return PanAccount(corrupt: id)
            }
            lock.lock(); defer { lock.unlock() }
            guard let prior = verified[id], prior.signature == signature else {
                verified[id] = nil
                return account
            }
            return prior.account
        }
    }

    func replace(_ provider: PanProviderID, credentials: RuntimeValue) async throws -> PanAccount {
        guard let supplied = credentials.panObject else { throw RuntimeHTTPError.remote("INVALID_REQUEST") }
        let fields = PanProvider.all.first { $0.id == provider }!.credentialFields
        let filtered = RuntimeValue.object(RuntimeObject(uniqueKeysWithValues: fields.compactMap { field in
            supplied[field.rawValue].map { (field.rawValue, $0) }
        }))
        try Self.validateCredentials(provider, filtered)
        let allowed = Set(fields.map(\.rawValue))
        let cookie = allowed.contains("cookie") ? supplied["cookie"]?.panString : nil
        let username = allowed.contains("username") ? supplied["username"]?.panString : nil
        let password = allowed.contains("password") ? supplied["password"]?.panString : nil
        let authorization = allowed.contains("authorization") ? supplied["authorization"]?.panString : nil
        let refreshedAtMs = allowed.contains("refreshedAt") ? supplied["refreshedAt"]?.panInteger : nil
        let updated: [FilmPanAccountRecord]
        do {
            updated = try await repository.replacePan(
                provider: provider.rawValue,
                cookie: cookie,
                username: username,
                password: password,
                authorization: authorization,
                refreshedAtMs: refreshedAtMs
            ).accounts
        } catch { throw Self.storageError(error) }
        let record = updated.first { $0.provider == provider.rawValue }
        return try Self.snapshot(record, provider)
    }

    func signature(_ provider: PanProviderID) async throws -> Data {
        let byProvider = try await readMap()
        return try Self.signature(byProvider[provider.rawValue], provider)
    }

    /// The runtime's success is not sufficient: its credential CAS must already
    /// be visible in these same rows, at the receipt's revision and fields.
    func accept(_ account: PanAccount) async throws -> PanAccount {
        if let error = account.errorCode { throw RuntimeHTTPError.remote(error.rawValue) }
        guard let fields = account.fields else { throw PanAccountError.unconfirmed }
        let byProvider = try await readMap()
        let record = byProvider[account.provider.rawValue]
        let current = try Self.snapshot(record, account.provider)
        guard current.revision == account.revision else { throw RuntimeHTTPError.remote("STALE_ACCOUNT") }
        guard current.configured == account.configured, let currentFields = current.fields,
              Set(currentFields) == Set(fields) else {
            throw PanAccountError.unconfirmed
        }
        let signature = try Self.signature(record, account.provider)
        lock.withLock { verified[account.provider] = Verified(signature: signature, account: account) }
        return account
    }

    func expire(_ provider: PanProviderID, expected: Data) async throws {
        let byProvider = try await readMap()
        let record = byProvider[provider.rawValue]
        guard try Self.signature(record, provider) == expected else { throw RuntimeHTTPError.remote("STALE_ACCOUNT") }
        let current = try Self.snapshot(record, provider)
        guard current.configured == true, let revision = current.revision, let fields = current.fields else { return }
        let account = PanAccount(provider: provider, revision: revision, fields: fields, status: .expired)
        lock.withLock { verified[provider] = Verified(signature: expected, account: account) }
    }

    private func readMap() async throws -> [String: FilmPanAccountRecord] {
        do {
            let accounts = try await repository.readPanAccounts().accounts
            return Dictionary(uniqueKeysWithValues: accounts.map { ($0.provider, $0) })
        } catch { throw Self.storageError(error) }
    }

    private static func storageError(_ error: Error) -> Error {
        if let error = error as? SQLiteStorageError {
            switch error {
            case .conflict: return RuntimeHTTPError.remote("STALE_ACCOUNT")
            case .invalidSchema, .corrupt, .invalidValue: return RuntimeHTTPError.remote("CORRUPT_STORAGE")
            default: return RuntimeHTTPError.remote("STORAGE_ERROR")
            }
        }
        guard let error = error as? FilmSettingsStorageError else { return error }
        switch error {
        case .conflict: return RuntimeHTTPError.remote("STALE_ACCOUNT")
        case .invalidDocument: return RuntimeHTTPError.remote("CORRUPT_STORAGE")
        case .storageFailure: return RuntimeHTTPError.remote("STORAGE_ERROR")
        }
    }

    static func validate(_ root: RuntimeValue) throws {
        guard let fields = root.panObject, fields["version"] == .number(1),
              fields["accounts"]?.panObject != nil else {
            throw RuntimeHTTPError.remote("CORRUPT_STORAGE")
        }
    }

    public static func validateCredentials(_ provider: PanProviderID, _ credentials: RuntimeValue) throws {
        guard let fields = credentials.panObject else { throw RuntimeHTTPError.remote("INVALID_REQUEST") }
        let allowed = PanProvider.all.first { $0.id == provider }!.credentialFields
        for (name, value) in fields {
            guard let field = allowed.first(where: { $0.rawValue == name }) else { continue }
            if field.isNumber {
                guard value.panInteger != nil else { throw RuntimeHTTPError.remote("INVALID_REQUEST") }
            } else {
                guard let text = value.panString, !text.utf8.contains(0),
                      field == .password || !text.utf8.contains(where: { $0 == 13 || $0 == 10 }) else {
                    throw RuntimeHTTPError.remote("INVALID_REQUEST")
                }
            }
        }
    }

    private static func fieldValue(_ record: FilmPanAccountRecord?, _ field: PanCredentialField) -> RuntimeValue? {
        guard let record else { return nil }
        switch field {
        case .cookie: return record.cookie.map(RuntimeValue.string)
        case .username: return record.username.map(RuntimeValue.string)
        case .password: return record.password.map(RuntimeValue.string)
        case .authorization: return record.authorization.map(RuntimeValue.string)
        case .refreshedAt: return record.refreshedAtMs.map { .number(Double($0)) }
        }
    }

    static func snapshot(_ record: FilmPanAccountRecord?, _ provider: PanProviderID) throws -> PanAccount {
        let allowed = PanProvider.all.first { $0.id == provider }!.credentialFields
        var credentials = RuntimeObject()
        if let record {
            guard record.accountRevision > 0 else { throw RuntimeHTTPError.remote("CORRUPT_STORAGE") }
            for field in allowed {
                if let val = fieldValue(record, field) { credentials[field.rawValue] = val }
            }
            do { try validateCredentials(provider, .object(credentials)) }
            catch { throw RuntimeHTTPError.remote("CORRUPT_STORAGE") }
        }
        let fields = credentials.filter { _, value in
            if let text = value.panString { return !text.isEmpty }
            return (value.panInteger ?? 0) > 0
        }.map(\.key).sorted()
        return PanAccount(provider: provider, revision: record?.accountRevision ?? 0,
                          fields: fields, status: fields.isEmpty ? .signedOut : .unverified)
    }

    private static func signature(_ record: FilmPanAccountRecord?, _ provider: PanProviderID) throws -> Data {
        _ = try snapshot(record, provider)
        let fields = PanProvider.all.first { $0.id == provider }!.credentialFields
        let value = RuntimeValue.array([.number(Double(record?.accountRevision ?? 0))] +
            fields.map { fieldValue(record, $0) ?? .null })
        return Data(SHA256.hash(data: try RuntimeJSON.encode(value)))
    }
}

extension RuntimeValue {
    var panObject: RuntimeObject? { if case .object(let fields) = self { return fields }; return nil }
}
