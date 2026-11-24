import Foundation

public struct RuntimeSettingsSnapshot: Sendable {
    public let document: RuntimeValue?
    public let revision: Int64
    public init(document: RuntimeValue?, revision: Int64) {
        self.document = document; self.revision = revision
    }
}

public enum RuntimeSettingsAccessError: Error, Equatable, Sendable {
    case storageFailure, conflict, invalidRequest, corruptStorage, closed
}

/// A private native capability, not an HTTP method or a script capability.
/// Product assembly explicitly selects readable and writable settings blocks.
public struct RuntimeSettingsAccess: Sendable {
    public let blocks: Set<String>
    public let writableBlocks: Set<String>
    let read: @Sendable (String) throws -> RuntimeSettingsSnapshot
    let update: @Sendable (String, RuntimeValue) throws -> RuntimeSettingsSnapshot

    public init(blocks: Set<String>, writableBlocks: Set<String>,
                read: @escaping @Sendable (String) throws -> RuntimeSettingsSnapshot,
                update: @escaping @Sendable (String, RuntimeValue) throws -> RuntimeSettingsSnapshot) {
        self.blocks = blocks; self.writableBlocks = writableBlocks
        self.read = read; self.update = update
    }
}
