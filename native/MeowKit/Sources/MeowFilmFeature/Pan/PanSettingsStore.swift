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
    private let document: FilmSettingsDocument
    private let lock = NSLock()
    private var verified: [PanProviderID: Verified] = [:]
    static let empty: RuntimeValue = .object(["version": .number(1), "accounts": .object([:])])

    init(document: FilmSettingsDocument) { self.document = document }

    func accounts() async throws -> [PanAccount] {
        let root = try await read()
        return try PanProviderID.allCases.map { id in
            let account: PanAccount, signature: Data
            do {
                account = try Self.snapshot(root, id); signature = try Self.signature(root, id)
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
        let credentials = RuntimeValue.object(RuntimeObject(uniqueKeysWithValues: fields.compactMap { field in
            supplied[field.rawValue].map { (field.rawValue, $0) }
        }))
        try Self.validateCredentials(provider, credentials)
        let root: RuntimeValue
        do {
            root = try await document.replacePan(provider, credentials: credentials).document!
        } catch { throw Self.storageError(error) }
        return try Self.snapshot(root, provider)
    }

    func signature(_ provider: PanProviderID) async throws -> Data { try await Self.signature(read(), provider) }

    /// The runtime's success is not sufficient: its credential CAS must already
    /// be visible in these same rows, at the receipt's revision and fields.
    func accept(_ account: PanAccount) async throws -> PanAccount {
        if let error = account.errorCode { throw RuntimeHTTPError.remote(error.rawValue) }
        guard let fields = account.fields else { throw PanAccountError.unconfirmed }
        let root = try await read(), current = try Self.snapshot(root, account.provider)
        guard current.revision == account.revision else { throw RuntimeHTTPError.remote("STALE_ACCOUNT") }
        guard current.configured == account.configured, let currentFields = current.fields,
              Set(currentFields) == Set(fields) else {
            throw PanAccountError.unconfirmed
        }
        let signature = try Self.signature(root, account.provider)
        lock.withLock { verified[account.provider] = Verified(signature: signature, account: account) }
        return account
    }

    func expire(_ provider: PanProviderID, expected: Data) async throws {
        let root = try await read()
        guard try Self.signature(root, provider) == expected else { throw RuntimeHTTPError.remote("STALE_ACCOUNT") }
        let current = try Self.snapshot(root, provider)
        guard current.configured == true, let revision = current.revision, let fields = current.fields else { return }
        let account = PanAccount(provider: provider, revision: revision, fields: fields, status: .expired)
        lock.withLock { verified[provider] = Verified(signature: expected, account: account) }
    }

    private func read() async throws -> RuntimeValue {
        do {
            let root = try await document.read() ?? Self.empty
            try Self.validate(root)
            return root
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

    static func validateCredentials(_ provider: PanProviderID, _ credentials: RuntimeValue) throws {
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

    static func snapshot(_ root: RuntimeValue, _ provider: PanProviderID) throws -> PanAccount {
        let record = root.panField("accounts")?.panField(provider.rawValue)
        if let record {
            guard record.panField("revision")?.panInteger.map({ $0 > 0 }) == true,
                  let credentials = record.panField("credentials") else { throw RuntimeHTTPError.remote("CORRUPT_STORAGE") }
            do { try validateCredentials(provider, credentials) }
            catch { throw RuntimeHTTPError.remote("CORRUPT_STORAGE") }
        }
        let credentials = record?.panField("credentials")?.panObject ?? RuntimeObject()
        let allowed = PanProvider.all.first { $0.id == provider }!.credentialFields.map(\.rawValue)
        let fields = credentials.filter { key, value in
            guard allowed.contains(key) else { return false }
            if let text = value.panString { return !text.isEmpty }
            return (value.panInteger ?? 0) > 0
        }.map(\.key).sorted()
        return PanAccount(provider: provider, revision: record?.panField("revision")?.panInteger ?? 0,
                          fields: fields, status: fields.isEmpty ? .signedOut : .unverified)
    }

    private static func signature(_ root: RuntimeValue, _ provider: PanProviderID) throws -> Data {
        _ = try snapshot(root, provider)
        let record = root.panField("accounts")?.panField(provider.rawValue)
        let fields = PanProvider.all.first { $0.id == provider }!.credentialFields
        let value = RuntimeValue.array([record?.panField("revision") ?? .number(0)] +
            fields.map { record?.panField("credentials")?.panField($0.rawValue) ?? .null })
        return Data(SHA256.hash(data: try RuntimeJSON.encode(value)))
    }
}

extension RuntimeValue {
    var panObject: RuntimeObject? { if case .object(let fields) = self { return fields }; return nil }
}
