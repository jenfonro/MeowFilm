import Foundation
import MeowRuntime

public struct FilmPlaybackHistory: Equatable, Sendable {
    public let value: RuntimeObject
    public let targetID: String
    public let title: String
    public let position: Double
    public let duration: Double?
    public let updatedAt: Double
    init(_ raw: RuntimeValue) throws {
        value = try raw.filmRequiredObject()
        targetID = try value.filmString("targetId"); title = try value.filmString("title")
        position = try value.filmNumber("position"); duration = value["duration"]?.filmNumber
        updatedAt = try value.filmNumber("updatedAt")
    }
}

/// The private share reference is only read by its editor and submitted to Node.
/// Do not use the entire record as a diagnostic or a list's accessibility value.
public struct FilmManualMatch: Equatable, Sendable, CustomStringConvertible, CustomDebugStringConvertible, CustomReflectable {
    public var value: RuntimeObject
    public var id: String { value["id"]!.filmText! }
    public var targetID: String { value["targetId"]!.filmText! }
    public var kind: String { value["kind"]!.filmText! }
    public var name: String { value["name"]?.filmText ?? value["title"]?.filmText ?? "人工匹配" }
    public var enabled: Bool { value["enabled"] == .bool(true) }
    public var autoDisable: Bool { value["autoDisable"] == .bool(true) }
    public var failureCount: Int { value["failureCount"]!.filmInteger! }

    public init(targetID: String, title: String = "", kind: String = "site") {
        value = ["id": .string(UUID().uuidString.lowercased()), "targetId": .string(targetID),
                 "title": .string(title), "name": .string(title), "kind": .string(kind),
                 "enabled": .bool(true), "autoDisable": .bool(true), "failureCount": .number(0)]
    }
    init(_ raw: RuntimeValue) throws {
        value = try raw.filmRequiredObject()
        _ = try value.filmString("id"); _ = try value.filmString("targetId")
        _ = try value.filmString("kind")
        _ = try value.filmBool("enabled"); _ = try value.filmBool("autoDisable")
        guard value["failureCount"]?.filmInteger != nil else { throw FilmPlaybackError.invalidResponse }
    }
    public var description: String { "FilmManualMatch(redacted)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["id": id]) }
}

public struct FilmPlaybackBlock: Equatable, Sendable {
    public let value: RuntimeObject
    public let targetID: String
    public let sourceKey: String
    public let detailID: FilmPlaybackIdentifier
    public let lineID: FilmPlaybackIdentifier
    init(_ raw: RuntimeValue) throws {
        value = try raw.filmRequiredObject()
        targetID = try value.filmString("targetId"); sourceKey = try value.filmString("sourceKey")
        detailID = try FilmPlaybackIdentifier(value.filmValue("detailId"))
        lineID = try FilmPlaybackIdentifier(value.filmValue("lineId"))
    }
}

public struct FilmPlaybackLibrary: Equatable, Sendable {
    public let preferences: FilmPlaybackPreferences
    public let history: [FilmPlaybackHistory]
    public var manual: [FilmManualMatch]
    public let blocks: [FilmPlaybackBlock]
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        preferences = try FilmPlaybackPreferences(fields.filmValue("preferences"))
        history = try fields.filmArray("history").map(FilmPlaybackHistory.init)
        manual = try fields.filmArray("manual").map(FilmManualMatch.init)
        blocks = try fields.filmArray("blocks").map(FilmPlaybackBlock.init)
    }
}
