import Foundation
import CryptoKit
import MeowRuntime

/// The document is the credential authority. This cache holds only online
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

    func accounts() throws -> [PanAccount] {
        let root = try read()
        return try PanProviderID.allCases.map { id in
            let account = Self.snapshot(root, id)
            let signature = try Self.signature(root, id)
            lock.lock(); defer { lock.unlock() }
            guard let prior = verified[id], prior.signature == signature else {
                verified[id] = nil
                return account
            }
            return prior.account
        }
    }

    func replace(_ provider: PanProviderID, credentials: RuntimeValue) throws -> PanAccount {
        try Self.validateCredentials(provider, credentials)
        let root: RuntimeValue
        do {
            root = try document.update { value in
                let root = value ?? Self.empty
                try Self.validate(root)
                var accounts = root.panField("accounts")!.panObject!
                let revision = Self.snapshot(root, provider).revision
                guard revision < 9_007_199_254_740_991 else { throw RuntimeHTTPError.remote("STORAGE_ERROR") }
                accounts[provider.rawValue] = .object([
                    "revision": .number(Double(revision + 1)), "credentials": credentials,
                ])
                return .object(["version": .number(1), "accounts": .object(accounts)])
            }
        } catch { throw Self.storageError(error) }
        return Self.snapshot(root, provider)
    }

    func signature(_ provider: PanProviderID) throws -> Data { try Self.signature(read(), provider) }

    /// The runtime's success is not sufficient: its credential CAS must already
    /// be visible in this same document, at the receipt's revision and fields.
    func accept(_ account: PanAccount) throws -> PanAccount {
        let root = try read(), current = Self.snapshot(root, account.provider)
        guard current.revision == account.revision else { throw RuntimeHTTPError.remote("STALE_ACCOUNT") }
        guard current.configured == account.configured, Set(current.fields) == Set(account.fields) else {
            throw PanAccountError.unconfirmed
        }
        let signature = try Self.signature(root, account.provider)
        lock.lock(); defer { lock.unlock() }
        verified[account.provider] = Verified(signature: signature, account: account)
        return account
    }

    func expire(_ provider: PanProviderID, expected: Data) throws {
        let root = try read()
        guard try Self.signature(root, provider) == expected else { throw RuntimeHTTPError.remote("STALE_ACCOUNT") }
        let current = Self.snapshot(root, provider)
        guard current.configured else { return }
        let account = PanAccount(provider: provider, revision: current.revision, fields: current.fields, status: .expired)
        lock.lock(); defer { lock.unlock() }
        verified[provider] = Verified(signature: expected, account: account)
    }

    private func read() throws -> RuntimeValue {
        do {
            let root = try document.read() ?? Self.empty
            try Self.validate(root)
            return root
        } catch { throw Self.storageError(error) }
    }

    private static func storageError(_ error: Error) -> Error {
        guard let error = error as? FilmSettingsStorageError else { return error }
        switch error {
        case .conflict: return RuntimeHTTPError.remote("STALE_ACCOUNT")
        case .invalidDocument: return RuntimeHTTPError.remote("CORRUPT_STORAGE")
        case .storageFailure: return RuntimeHTTPError.remote("STORAGE_ERROR")
        }
    }

    static func validate(_ root: RuntimeValue) throws {
        guard let fields = root.panObject, Set(fields.keys) == ["version", "accounts"],
              fields["version"] == .number(1), let accounts = fields["accounts"]?.panObject else {
            throw RuntimeHTTPError.remote("CORRUPT_STORAGE")
        }
        for (id, record) in accounts {
            guard let provider = PanProviderID(rawValue: id), let fields = record.panObject,
                  Set(fields.keys) == ["revision", "credentials"],
                  let revision = fields["revision"]?.panInteger, revision > 0,
                  let credentials = fields["credentials"] else { throw RuntimeHTTPError.remote("CORRUPT_STORAGE") }
            do { try validateCredentials(provider, credentials) }
            catch { throw RuntimeHTTPError.remote("CORRUPT_STORAGE") }
        }
    }

    static func validateCredentials(_ provider: PanProviderID, _ credentials: RuntimeValue) throws {
        guard let fields = credentials.panObject else { throw RuntimeHTTPError.remote("INVALID_REQUEST") }
        let allowed = PanProvider.all.first { $0.id == provider }!.credentialFields
        for (name, value) in fields {
            guard let field = allowed.first(where: { $0.rawValue == name }) else {
                throw RuntimeHTTPError.remote("INVALID_REQUEST")
            }
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

    private static func snapshot(_ root: RuntimeValue, _ provider: PanProviderID) -> PanAccount {
        let record = root.panField("accounts")?.panField(provider.rawValue)
        let credentials = record?.panField("credentials")?.panObject ?? RuntimeObject()
        let fields = credentials.filter { _, value in
            if let text = value.panString { return !text.isEmpty }
            return (value.panInteger ?? 0) > 0
        }.map(\.key).sorted()
        return PanAccount(provider: provider, revision: record?.panField("revision")?.panInteger ?? 0,
                          fields: fields, status: fields.isEmpty ? .signedOut : .unverified)
    }

    private static func signature(_ root: RuntimeValue, _ provider: PanProviderID) throws -> Data {
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
