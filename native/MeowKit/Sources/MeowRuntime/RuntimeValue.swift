import Foundation

/// Script values use Unicode scalar identity, including object member names.
public indirect enum RuntimeValue: Sendable, Equatable {
    case null, bool(Bool), number(Double), string(String)
    case array([RuntimeValue]), object(RuntimeObject)

    public static func == (lhs: RuntimeValue, rhs: RuntimeValue) -> Bool {
        switch (lhs, rhs) {
        case (.null, .null): return true
        case let (.bool(a), .bool(b)): return a == b
        case let (.number(a), .number(b)): return a == b
        case let (.string(a), .string(b)): return a.utf8.elementsEqual(b.utf8)
        case let (.array(a), .array(b)): return a == b
        case let (.object(a), .object(b)): return a == b
        default: return false
        }
    }
}

/// Swift String equality normalizes canonically equivalent spellings. JSON
/// members instead use their original UTF-8 bytes as their identity.
public struct RuntimeObject: Sendable, Equatable, ExpressibleByDictionaryLiteral, Sequence {
    public typealias Element = (key: String, value: RuntimeValue)
    private var members: [Data: RuntimeValue] = [:]

    public init() {}

    /// For ordinary, locally constructed fields. Wire decoding never passes
    /// through a String-keyed dictionary.
    public init(_ fields: [String: RuntimeValue]) {
        for (key, value) in fields { self[key] = value }
    }

    public init<S: Sequence>(uniqueKeysWithValues fields: S) where S.Element == (String, RuntimeValue) {
        for (key, value) in fields { self[key] = value }
    }

    public init(dictionaryLiteral elements: (String, RuntimeValue)...) {
        self.init(uniqueKeysWithValues: elements)
    }

    public subscript(_ key: String) -> RuntimeValue? {
        get { members[Data(key.utf8)] }
        set { members[Data(key.utf8)] = newValue }
    }

    public var keys: [String] { members.keys.map { String(decoding: $0, as: UTF8.self) } }
    public var values: [RuntimeValue] { Array(members.values) }
    public var count: Int { members.count }
    public var isEmpty: Bool { members.isEmpty }

    public struct Iterator: IteratorProtocol {
        private var iterator: Dictionary<Data, RuntimeValue>.Iterator
        fileprivate init(_ members: [Data: RuntimeValue]) { iterator = members.makeIterator() }
        public mutating func next() -> RuntimeObject.Element? {
            guard let (key, value) = iterator.next() else { return nil }
            return (String(decoding: key, as: UTF8.self), value)
        }
    }

    public func makeIterator() -> Iterator { Iterator(members) }
}
