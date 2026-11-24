import Foundation
import MeowDebug
import MeowRuntime

public struct FilmPlaybackHistory: Equatable, Sendable {
    public let value: RuntimeObject
    public let targetID: String
    public let title: String
    public var businessID: String? { value["businessId"]?.filmText }
    public var playbackTarget: FilmPlaybackTarget? {
        guard let raw = value["playbackTarget"] else { return nil }
        do { return try FilmPlaybackTarget(raw) }
        catch {
            DebugRuntime.logError(.filmPlayback, sub: "library", event: "history.target.invalid", error: error)
            return nil
        }
    }
    init(_ raw: RuntimeValue) throws {
        value = try raw.filmRequiredObject()
        targetID = try value.filmString("targetId"); title = try value.filmString("title")
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
    public enum Area: Equatable, Sendable { case manual, blocks }

    public let area: Area
    public var history: [FilmPlaybackHistory]
    public var manual: [FilmManualMatch]
    public let blocks: [FilmPlaybackBlock]
    init(_ value: RuntimeValue, area: Area) throws {
        let fields = try value.filmRequiredObject()
        self.area = area
        switch area {
        case .manual:
            manual = try Self.items(fields.filmArray("manual"), kind: "manual", transform: FilmManualMatch.init)
            history = (fields["history"]?.filmArray ?? []).compactMap { raw in
                do { return try FilmPlaybackHistory(raw) }
                catch {
                    DebugRuntime.logError(.filmPlayback, sub: "library", event: "history.item.invalid", error: error)
                    return nil
                }
            }
            blocks = []
        case .blocks:
            blocks = try Self.items(fields.filmArray("blocks"), kind: "blocks", transform: FilmPlaybackBlock.init)
            history = []; manual = []
        }
    }

    static func items<Item>(_ values: [RuntimeValue], kind: String,
                            transform: (RuntimeValue) throws -> Item) throws -> [Item] {
        var result: [Item] = []
        for raw in values {
            do { result.append(try transform(raw)) }
            catch {
                DebugRuntime.logError(.filmPlayback, sub: "library", event: "\(kind).item.invalid", error: error)
            }
        }
        guard values.isEmpty || !result.isEmpty else { throw FilmPlaybackError.invalidResponse }
        return result
    }
}

/// Pure validation & payload builder for manual match entries (UI-independent).
public struct FilmManualMatchDraft: Sendable {
    public let id: String
    public let kind: String
    public let targetID: String
    public let name: String
    public let sourceKey: String
    public let detailID: String
    public let numericDetailID: Bool
    public let provider: FilmPanProvider?
    public let shareURL: String
    public let sharePassword: String
    public let seasonText: String
    public let enabled: Bool
    public let autoDisable: Bool

    public init(id: String, kind: String, targetID: String, name: String,
                sourceKey: String, detailID: String, numericDetailID: Bool,
                provider: FilmPanProvider?, shareURL: String, sharePassword: String,
                seasonText: String, enabled: Bool, autoDisable: Bool) {
        self.id = id; self.kind = kind; self.targetID = targetID; self.name = name
        self.sourceKey = sourceKey; self.detailID = detailID; self.numericDetailID = numericDetailID
        self.provider = provider; self.shareURL = shareURL; self.sharePassword = sharePassword
        self.seasonText = seasonText; self.enabled = enabled; self.autoDisable = autoDisable
    }

    public func build(from original: FilmManualMatch?) throws -> FilmManualMatch {
        let target = targetID.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !target.isEmpty else { throw FilmPlaybackError(code: "INVALID_REQUEST") }
        var value = original ?? FilmManualMatch(targetID: target)
        value.value["targetId"] = .string(target)
        value.value["title"] = .string(name)
        value.value["name"] = .string(name)
        value.value["kind"] = .string(kind)
        value.value["id"] = .string(id)
        value.value["enabled"] = .bool(enabled)
        value.value["autoDisable"] = .bool(autoDisable)
        value.value["seasonHint"] = nil
        if !seasonText.isEmpty {
            guard let number = Int(seasonText), number >= 0 else {
                throw FilmPlaybackError(code: "INVALID_REQUEST")
            }
            value.value["seasonHint"] = .number(Double(number))
        }
        if kind == "site" {
            guard !sourceKey.isEmpty else { throw FilmPlaybackError(code: "INVALID_REQUEST") }
            value.value["sourceKey"] = .string(sourceKey)
            if numericDetailID {
                guard let number = Int64(detailID), abs(Double(number)) <= 9_007_199_254_740_991 else {
                    throw FilmPlaybackError(code: "INVALID_REQUEST")
                }
                value.value["detailId"] = .number(Double(number))
            } else {
                value.value["detailId"] = .string(detailID)
            }
            value.value["share"] = nil; value.value["provider"] = nil
        } else {
            guard let provider, !shareURL.isEmpty else { throw FilmPlaybackError(code: "INVALID_REQUEST") }
            value.value["provider"] = .string(provider.rawValue)
            value.value["share"] = .object(["url": .string(shareURL), "password": .string(sharePassword)])
            value.value["sourceKey"] = nil; value.value["detailId"] = nil
        }
        return value
    }
}
