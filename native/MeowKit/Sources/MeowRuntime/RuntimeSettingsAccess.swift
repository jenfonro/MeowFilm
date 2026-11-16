import Foundation

public struct RuntimeSettingsSnapshot: Sendable {
    public let document: RuntimeValue?
    public let fingerprint: RuntimeValue
    public init(document: RuntimeValue?, fingerprint: RuntimeValue) {
        self.document = document; self.fingerprint = fingerprint
    }
}

public enum RuntimeSettingsAccessError: Error, Equatable, Sendable {
    case storageFailure, conflict
}

/// A private native capability, not an HTTP method or a script capability.
/// Product assembly explicitly selects readable and writable settings blocks.
public struct RuntimeSettingsAccess: Sendable {
    public let blocks: Set<String>
    public let writableBlocks: Set<String>
    let read: @Sendable (String) throws -> RuntimeSettingsSnapshot
    let write: @Sendable (String, RuntimeValue, RuntimeValue) throws -> RuntimeSettingsSnapshot

    public init(blocks: Set<String>, writableBlocks: Set<String>,
                read: @escaping @Sendable (String) throws -> RuntimeSettingsSnapshot,
                write: @escaping @Sendable (String, RuntimeValue, RuntimeValue) throws -> RuntimeSettingsSnapshot) {
        self.blocks = blocks; self.writableBlocks = writableBlocks
        self.read = read; self.write = write
    }
}
