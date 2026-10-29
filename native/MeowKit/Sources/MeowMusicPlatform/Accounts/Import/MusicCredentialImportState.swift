import Foundation
import MeowAccounts
import MeowMusicFeature

/// Keeps the explicit-validation requirement in Keychain across process death.
/// The serialized kind distinguishes original credentials from an imported archive.
enum MusicCredentialImportStored {
    static let kind = "music.credentials.pending-import"
    static func pending(_ credential: AccountCredential, platform: MusicAccountPlatform) throws -> AccountCredential {
        let raw = try MusicCredentialImportJSON.object(credential.dataRepresentation())
        return try .init(data: JSONSerialization.data(withJSONObject: [
            "kind": kind, "version": 1, "platform": platform.rawValue, "credential": raw
        ], options: [.sortedKeys, .withoutEscapingSlashes]))
    }
    static func forValidation(_ credential: AccountCredential, platform: MusicAccountPlatform,
                              explicit: Bool) throws -> AccountCredential {
        let bytes = credential.dataRepresentation()
        let raw = try MusicQQWire.decode(bytes)
        guard raw["kind"] != nil else { return credential }
        guard bytes.count <= 65_536, Set(raw.keys) == ["kind", "version", "platform", "credential"],
              raw["kind"] as? String == kind, raw["platform"] as? String == platform.rawValue,
              try MusicQQWire.integer(raw["version"]) == 1,
              let body = raw["credential"] as? [String: Any],
              body["platform"] as? String == platform.rawValue,
              body["kind"] == nil else { throw MusicServiceError.invalidResponse }
        guard explicit else { throw MusicServiceError.verificationRequired }
        let data = try JSONSerialization.data(withJSONObject: body, options: [.sortedKeys, .withoutEscapingSlashes])
        guard data.count <= 32_768 else { throw MusicServiceError.limit }
        return try .init(data: data)
    }
}

/// Bounded native-only preview. Cancellation can revoke an installed Accounts
/// lease synchronously even while the service is awaiting its commit.
final class MusicCredentialImportDraft: @unchecked Sendable, CustomStringConvertible, CustomReflectable {
    let id: UUID
    let platform: MusicAccountPlatform
    let epoch: UUID
    let binding: UUID?
    let revision: Int
    let expires: Date
    let deviceState: MusicCredentialImportPreview.DeviceState
    private let lock = NSLock()
    private var material: MusicCredentialImportMaterial?
    private var lease: AccountCredentialImportLease?
    private var revoked = false
    init(id: UUID, platform: MusicAccountPlatform, epoch: UUID, binding: UUID?, revision: Int,
         expires: Date, material: MusicCredentialImportMaterial) {
        self.id = id; self.platform = platform; self.epoch = epoch; self.binding = binding
        self.revision = revision; self.expires = expires; self.material = material; deviceState = material.deviceState
    }
    func install(_ value: AccountCredentialImportLease) throws {
        lock.lock()
        let accepted = !revoked && lease == nil && material != nil
        if accepted { lease = value }
        lock.unlock()
        if !accepted { value.revoke(); throw MusicCredentialImportError.cancelled }
    }
    func preview() throws -> MusicCredentialImportPreview {
        lock.lock(); defer { lock.unlock() }
        guard !revoked, material != nil, let lease else { throw MusicCredentialImportError.expired }
        return .init(id: id, platform: platform, deviceState: deviceState,
                     requiresReplacement: lease.requiresReplacement, expiresAt: expires)
    }
    func take(replaceExisting: Bool) throws -> (AccountCredentialImportLease, MusicCredentialImportMaterial) {
        lock.lock(); defer { lock.unlock() }
        guard !revoked, let lease, let material else { throw MusicCredentialImportError.expired }
        guard !lease.requiresReplacement || replaceExisting else { throw MusicCredentialImportError.confirmationRequired }
        self.material = nil
        return (lease, material)
    }
    func revoke() {
        lock.lock()
        revoked = true; material = nil
        let value = lease; lease = nil
        lock.unlock()
        value?.revoke()
    }
    var description: String { "MusicCredentialImportDraft(<redacted>)" }
    var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}
