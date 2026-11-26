import Foundation
import MeowJSON

/// Script values use Unicode scalar identity, including object member names.
public indirect enum RuntimeValue: Sendable, Equatable {
    case null, bool(Bool), number(Double), string(String)
    case array([RuntimeValue]), object(RuntimeObject)
    case exactNumber(JSONNumber)

    /// Applies a typed projection's changes to its original object. Unchanged
    /// members (including fields outside that projection) retain exact JSON.
    public func applyingChanges(from previous: RuntimeValue, to next: RuntimeValue) -> RuntimeValue {
        if previous == next { return self }
        guard case .object(var original) = self, case .object(let before) = previous,
              case .object(let after) = next else { return next }
        for key in before.keys where after[key] == nil { original[key] = nil }
        for (key, value) in after where before[key] != value {
            original[key] = original[key]?.applyingChanges(from: before[key] ?? .null, to: value) ?? value
        }
        return .object(original)
    }

    public static func == (lhs: RuntimeValue, rhs: RuntimeValue) -> Bool {
        switch (lhs, rhs) {
        case (.null, .null): return true
        case let (.bool(a), .bool(b)): return a == b
        case let (.number(a), .number(b)): return a == b
        case let (.exactNumber(a), .exactNumber(b)): return a == b
        case let (.string(a), .string(b)): return a.utf8.elementsEqual(b.utf8)
        case let (.array(a), .array(b)): return a == b
        case let (.object(a), .object(b)): return a == b
        default: return false
        }
    }

    public subscript(_ key: String) -> RuntimeValue {
        guard case .object(let fields) = self else { return .null }
        return fields[key] ?? .null
    }

    public var stringValue: String? {
        if case .string(let value) = self { return value }
        return nil
    }

    public var trimmedString: String? {
        guard let value = stringValue?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty else { return nil }
        return value
    }

    public var boolValue: Bool? {
        if case .bool(let value) = self { return value }
        return nil
    }

    public var doubleValue: Double? {
        switch self {
        case .number(let value): return value.isFinite ? value : nil
        case .exactNumber(let number):
            if let value = Double(number.literal), value.isFinite { return value }
            return nil
        default: return nil
        }
    }

    public var intValue: Int? {
        switch self {
        case .number(let value):
            guard value.isFinite, value.rounded(.towardZero) == value,
                  value >= Double(Int.min), value <= Double(Int.max) else { return nil }
            return Int(value)
        case .exactNumber(let number):
            return Int(number.literal)
        default: return nil
        }
    }

    public var int64Value: Int64? {
        switch self {
        case .number(let value):
            guard value.isFinite, value.rounded(.towardZero) == value,
                  value >= Double(Int64.min), value < Double(Int64.max) else { return nil }
            return Int64(value)
        case .exactNumber(let number):
            return Int64(number.literal)
        default: return nil
        }
    }

    public var arrayValue: [RuntimeValue]? {
        if case .array(let items) = self { return items }
        return nil
    }

    public var objectValue: RuntimeObject? {
        if case .object(let fields) = self { return fields }
        return nil
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
