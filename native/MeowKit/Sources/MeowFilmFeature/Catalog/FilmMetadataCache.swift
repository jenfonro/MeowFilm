import MeowRuntime

public struct FilmMetadataCache: Equatable, Sendable {
    public enum Status: String, Sendable {
        case fresh, stale
    }

    public let status: Status
    public let errorCode: String?
    public let components: RuntimeObject

    init(value: RuntimeValue) {
        let fields = value.filmObject
        let components = fields?["components"]?.filmObject
        // Unrecognized cache evidence must not grant fresh-only actions.
        status = fields?["status"] == .string("fresh") && components != nil ? .fresh : .stale
        self.components = components ?? [:]
        errorCode = fields?["error"]?.filmObject?["code"]?.filmText
    }
}
