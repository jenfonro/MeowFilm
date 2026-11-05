import Foundation
import MeowJSON

/// The closed runtime-bridge error vocabulary; no provider text, stack or payload.
public enum RuntimeBridgeError: String, Error, Sendable, Equatable {
    case cancelled = "CANCELLED", timeout = "TIMEOUT", closed = "CLOSED", limit = "LIMIT"
    case invalidMessage = "INVALID_MESSAGE", methodUnavailable = "METHOD_UNAVAILABLE", internalError = "INTERNAL"
}

/// One protocol-1 envelope. Kind-specific fields are exact, as in the JS peer.
public struct RuntimeBridgeFrame: Sendable, Equatable {
    public enum Kind: Sendable, Equatable {
        case request(method: String, params: JSONValue)
        case result(JSONValue)
        case error(RuntimeBridgeError)
        case cancel
    }
    public static let version = 1
    /// Physical frame bound shared with packages/runtime-bridge; not a business data limit.
    public static let maximumBytes = 1024 * 1024
    public let session: String
    public let id: Int
    public let kind: Kind

    public init(session: String, id: Int, kind: Kind) {
        self.session = session; self.id = id; self.kind = kind
    }

    static func validSession(_ value: String) -> Bool {
        let bytes = value.utf8
        return (1...80).contains(bytes.count) && bytes.allSatisfy {
            (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) ||
                $0 == 95 || $0 == 46 || $0 == 58 || $0 == 45
        }
    }
    static func validMethod(_ value: String) -> Bool {
        let bytes = Array(value.utf8)
        guard let first = bytes.first, (1...128).contains(bytes.count),
              (65...90).contains(first) || (97...122).contains(first) else { return false }
        return bytes.allSatisfy {
            (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) ||
                $0 == 95 || $0 == 46 || $0 == 45
        }
    }

    /// Length-prefixed UTF-8 JSON body, rejected when larger than one physical frame.
    public func encoded() throws -> Data {
        guard Self.validSession(session), id > 0, id <= 9_007_199_254_740_991 else {
            throw RuntimeBridgeError.invalidMessage
        }
        var fields: [String: JSONValue] = [
            "version": .number(Double(Self.version)), "session": .string(session), "id": .number(Double(id)),
        ]
        switch kind {
        case .request(let method, let params):
            guard Self.validMethod(method) else { throw RuntimeBridgeError.invalidMessage }
            fields["kind"] = .string("request"); fields["method"] = .string(method); fields["params"] = params
        case .result(let value):
            fields["kind"] = .string("result"); fields["value"] = value
        case .error(let code):
            fields["kind"] = .string("error"); fields["code"] = .string(code.rawValue)
        case .cancel:
            fields["kind"] = .string("cancel")
        }
        let body: Data
        do { body = try RuntimeBridgeJSON.encode(.object(fields), maximumBytes: 8 * 1024 * 1024) }
        catch { throw RuntimeBridgeError.invalidMessage }
        guard body.count <= Self.maximumBytes else { throw RuntimeBridgeError.limit }
        var length = UInt32(body.count).bigEndian
        var frame = Data(bytes: &length, count: 4)
        frame.append(body)
        return frame
    }

    /// Decodes one frame body (without the 4-byte length prefix).
    public static func decode(body: Data) throws -> RuntimeBridgeFrame {
        guard !body.isEmpty, body.count <= maximumBytes else { throw RuntimeBridgeError.limit }
        var text = body
        // The JS decoder tolerates a single leading BOM; keep the same wire grammar.
        if text.starts(with: [0xef, 0xbb, 0xbf]) { text = text.dropFirst(3) }
        let value: JSONValue
        do { value = try RuntimeBridgeJSON.decode(Data(text), maximumBytes: maximumBytes) }
        catch { throw RuntimeBridgeError.invalidMessage }
        guard case .object(let fields) = value,
              case .number(let version)? = fields["version"], version == Double(Self.version),
              case .string(let session)? = fields["session"], validSession(session),
              case .number(let rawID)? = fields["id"], rawID >= 1, rawID <= 9_007_199_254_740_991,
              rawID.rounded(.towardZero) == rawID,
              case .string(let kindName)? = fields["kind"] else { throw RuntimeBridgeError.invalidMessage }
        let kind: Kind, expected: Set<String>
        switch kindName {
        case "request":
            guard case .string(let method)? = fields["method"], validMethod(method),
                  let params = fields["params"] else { throw RuntimeBridgeError.invalidMessage }
            kind = .request(method: method, params: params); expected = ["method", "params"]
        case "result":
            guard let value = fields["value"] else { throw RuntimeBridgeError.invalidMessage }
            kind = .result(value); expected = ["value"]
        case "error":
            guard case .string(let raw)? = fields["code"], let code = RuntimeBridgeError(rawValue: raw) else {
                throw RuntimeBridgeError.invalidMessage
            }
            kind = .error(code); expected = ["code"]
        case "cancel":
            kind = .cancel; expected = []
        default:
            throw RuntimeBridgeError.invalidMessage
        }
        guard Set(fields.keys) == expected.union(["version", "session", "id", "kind"]) else {
            throw RuntimeBridgeError.invalidMessage
        }
        return .init(session: session, id: Int(rawID), kind: kind)
    }
}
