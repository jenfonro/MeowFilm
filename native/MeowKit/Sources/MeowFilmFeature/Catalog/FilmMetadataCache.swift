import MeowRuntime

public struct FilmMetadataCache: Equatable, Sendable {
    public enum Status: String, Sendable {
        case fresh, stale
    }

    public let status: Status
    public let errorCode: String?
    public let components: RuntimeObject

    init(value: RuntimeValue) throws {
        guard let fields = value.filmObject,
              let name = fields["status"]?.filmText, let status = Status(rawValue: name),
              let components = fields["components"]?.filmObject else { throw RuntimeHTTPError.invalidResponse }
        self.status = status
        self.components = components
        if let error = fields["error"], error != .null {
            guard let code = error.filmObject?["code"]?.filmText else { throw RuntimeHTTPError.invalidResponse }
            errorCode = code
        } else {
            errorCode = nil
        }
    }
}
