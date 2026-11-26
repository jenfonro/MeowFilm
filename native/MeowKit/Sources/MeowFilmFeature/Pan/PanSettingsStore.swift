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
        return PanProviderID.allCases.map { id in
            let record = byProvider[id.rawValue]
            let account = Self.snapshot(record, id)
            let sig = Self.signature(record, id)
            lock.lock(); defer { lock.unlock() }
            guard let prior = verified[id], prior.signature == sig else {
                verified[id] = nil
                return account
            }
            return prior.account
        }
    }

    func replace(_ provider: PanProviderID, credentials: RuntimeValue) async throws -> PanAccount {
        guard let supplied = credentials.objectValue else { throw RuntimeHTTPError.remote("INVALID_REQUEST") }
        let fields = PanProvider.all.first { $0.id == provider }!.credentialFields
        let filtered = RuntimeValue.object(RuntimeObject(uniqueKeysWithValues: fields.compactMap { field in
            supplied[field.rawValue].map { (field.rawValue, $0) }
        }))
        try Self.validateCredentials(provider, filtered)
        let allowed = Set(fields.map(\.rawValue))
        let cookie = allowed.contains("cookie") ? supplied["cookie"]?.stringValue : nil
        let username = allowed.contains("username") ? supplied["username"]?.stringValue : nil
        let password = allowed.contains("password") ? supplied["password"]?.stringValue : nil
        let authorization = allowed.contains("authorization") ? supplied["authorization"]?.stringValue : nil
        let refreshedAtMs = allowed.contains("refreshedAt") ? supplied["refreshedAt"]?.panInteger : nil
        let updated: [FilmPanAccountRecord]
        do {
            let expected = try await repository.readPanRevisions(provider: provider.rawValue)
            updated = try await repository.replacePan(
                provider: provider.rawValue,
                expectedAccountRevision: expected.accountRevision,
                expectedRowRevision: expected.rowRevision,
                cookie: cookie,
                username: username,
                password: password,
                authorization: authorization,
                refreshedAtMs: refreshedAtMs
            ).accounts
        } catch { throw Self.storageError(error) }
        let record = updated.first { $0.provider == provider.rawValue }
        return Self.snapshot(record, provider)
    }

    func signature(_ provider: PanProviderID) async throws -> Data {
        let byProvider = try await readMap()
        return Self.signature(byProvider[provider.rawValue], provider)
    }

    /// The runtime's success is not sufficient: its credential CAS must already
    /// be visible in these same rows, at the receipt's revision and fields.
    func accept(_ account: PanAccount) async throws -> PanAccount {
        if let error = account.errorCode { throw RuntimeHTTPError.remote(error.rawValue) }
        guard let fields = account.fields else { throw PanAccountError.unconfirmed }
        let byProvider = try await readMap()
        let record = byProvider[account.provider.rawValue]
        let current = Self.snapshot(record, account.provider)
        guard current.revision == account.revision else { throw RuntimeHTTPError.remote("STALE_ACCOUNT") }
        guard current.configured == account.configured, let currentFields = current.fields,
              Set(currentFields) == Set(fields) else {
            throw PanAccountError.unconfirmed
        }
        let signature = Self.signature(record, account.provider)
        lock.withLock { verified[account.provider] = Verified(signature: signature, account: account) }
        return account
    }

    func expire(_ provider: PanProviderID, expected: Data) async throws {
        let byProvider = try await readMap()
        let record = byProvider[provider.rawValue]
        guard Self.signature(record, provider) == expected else { throw RuntimeHTTPError.remote("STALE_ACCOUNT") }
        let current = Self.snapshot(record, provider)
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
            case .closed: return RuntimeHTTPError.remote("CLOSED")
            default: return RuntimeHTTPError.remote("STORAGE_ERROR")
            }
        }
        guard let error = error as? FilmSettingsStorageError else { return error }
        switch error {
        case .conflict: return RuntimeHTTPError.remote("STALE_ACCOUNT")
        case .invalidData, .corruptStorage: return RuntimeHTTPError.remote("CORRUPT_STORAGE")
        case .closed: return RuntimeHTTPError.remote("CLOSED")
        case .storageFailure: return RuntimeHTTPError.remote("STORAGE_ERROR")
        }
    }

    public static func validateCredentials(_ provider: PanProviderID, _ credentials: RuntimeValue) throws {
        guard let fields = credentials.objectValue else { throw RuntimeHTTPError.remote("INVALID_REQUEST") }
        let allowed = PanProvider.all.first { $0.id == provider }!.credentialFields
        for (name, value) in fields {
            guard let field = allowed.first(where: { $0.rawValue == name }) else { continue }
            if field.isNumber {
                guard value.panInteger != nil else { throw RuntimeHTTPError.remote("INVALID_REQUEST") }
            } else {
                guard let text = value.stringValue, !text.utf8.contains(0),
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

    static func snapshot(_ record: FilmPanAccountRecord?, _ provider: PanProviderID) -> PanAccount {
        let allowed = PanProvider.all.first { $0.id == provider }!.credentialFields
        let fields = allowed.compactMap { field -> String? in
            guard let val = fieldValue(record, field) else { return nil }
            if let text = val.stringValue { return text.isEmpty ? nil : field.rawValue }
            return (val.panInteger ?? 0) > 0 ? field.rawValue : nil
        }.sorted()
        return PanAccount(provider: provider, revision: record?.accountRevision ?? 0,
                          fields: fields, status: fields.isEmpty ? .signedOut : .unverified)
    }

    private static func signature(_ record: FilmPanAccountRecord?, _ provider: PanProviderID) -> Data {
        let fields = PanProvider.all.first { $0.id == provider }!.credentialFields
        var hasher = SHA256()
        func feed(_ text: String?) {
            if let text {
                var len = UInt64(text.utf8.count).littleEndian
                withUnsafeBytes(of: &len) { hasher.update(bufferPointer: $0) }
                hasher.update(data: Data(text.utf8))
            } else {
                var sentinel = UInt64.max.littleEndian
                withUnsafeBytes(of: &sentinel) { hasher.update(bufferPointer: $0) }
            }
        }
        var rev = (record?.accountRevision ?? 0).littleEndian
        withUnsafeBytes(of: &rev) { hasher.update(bufferPointer: $0) }
        for field in fields {
            switch field {
            case .cookie: feed(record?.cookie)
            case .username: feed(record?.username)
            case .password: feed(record?.password)
            case .authorization: feed(record?.authorization)
            case .refreshedAt: feed(record?.refreshedAtMs.map(String.init))
            }
        }
        return Data(hasher.finalize())
    }
}
