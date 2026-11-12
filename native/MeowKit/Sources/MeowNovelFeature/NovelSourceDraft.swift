import Foundation
import MeowRuntime

/// An editor draft, never a second installed-source store. Unedited values retain
/// their JSON types and the original protocol, including extension members.
struct NovelSourceDraft {
    let `protocol`: NovelSourceProtocol
    private var base: RuntimeObject
    private var edits: [String: String] = [:]
    init(protocol: NovelSourceProtocol, definition: String? = nil) throws {
        self.protocol = `protocol`
        if let definition {
            let value = try RuntimeJSON.decode(Data(definition.utf8))
            if case .object(let object) = value { base = object }
            else if `protocol` == .qysg, let html = value.string { base = ["html": .string(html)] }
            else { throw NovelError.invalidResponse }
        } else {
            base = ["bookSourceType": .number(0), "enabledCookieJar": .bool(true),
                    "enabledExplore": .bool(true)]
            base[`protocol` == .yiciyuan ? "enable" : "enabled"] = .bool(true)
            switch `protocol` {
            case .legado: base["ruleSearch"] = .object([:])
            case .yiciyuan: base["ruleSearchList"] = .string("")
            case .qysg: base["html"] = .string("")
            }
        }
    }
    private func object(_ value: RuntimeValue?) -> RuntimeObject {
        if case .object(let object) = value { return object }
        if let text = value?.string, let value = try? RuntimeJSON.decode(Data(text.utf8)), case .object(let object) = value { return object }
        return [:]
    }
    private func original(_ field: NovelSourceField) -> RuntimeValue? {
        let parts = field.parts
        if parts.count == 1 { return base[parts[0]] }
        let value = object(base[parts[0]])[parts[1]]
        if value == nil, field.path == "ruleToc.updateTime" { return object(base["ruleToc"])["chapterInfo"] }
        return value
    }
    func text(_ field: NovelSourceField) -> String {
        if let value = edits[field.id] { return value }
        return originalText(field)
    }
    private func originalText(_ field: NovelSourceField) -> String {
        let value = original(field)
        return field.kind == .json && value == .null ? "null" : Self.text(value)
    }
    static func text(_ value: RuntimeValue?) -> String {
        switch value {
        case .none, .null: return ""
        case .string(let text): return text
        case .bool(let value): return value ? "true" : "false"
        case .number(let value): return value.rounded() == value ? String(format: "%.0f", value) : String(value)
        default: return (try? String(decoding: RuntimeJSON.encode(value!), as: UTF8.self)) ?? ""
        }
    }
    mutating func set(_ field: NovelSourceField, _ text: String) {
        if text == originalText(field) { edits[field.id] = nil } else { edits[field.id] = text }
    }
    func bool(_ key: String, fallback: Bool = true) -> Bool {
        let value = text(.init(path: key, label: key))
        return value.isEmpty ? fallback : !["false", "0"].contains(value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased())
    }
    mutating func setBool(_ key: String, _ value: Bool) { set(.init(path: key, label: key, kind: .bool), value ? "true" : "false") }
    var enabledKey: String { `protocol` == .yiciyuan ? "enable" : "enabled" }
    var tabs: [NovelSourceTab] { NovelSourceFields.tabs(`protocol`) }
    var extensionFields: [NovelSourceField] {
        let aliases = `protocol` == .yiciyuan ? ["enabled", "customOrder"] : []
        let known = Set(tabs.flatMap(\.fields).map(\.path) + ["bookSourceType", enabledKey, "enabledExplore", "enabledCookieJar"] + aliases)
        var fields: [NovelSourceField] = []
        for (key, value) in base where !known.contains(key) {
            if tabs.flatMap(\.fields).contains(where: { $0.path.hasPrefix(key + ".") }) {
                for (child, childValue) in object(value) where !known.contains(key + "." + child) {
                    fields.append(.init(path: key + "." + child, label: key + "." + child, rows: 3, kind: Self.kind(childValue), components: [key, child]))
                }
            } else { fields.append(.init(path: key, label: key, rows: 3, kind: Self.kind(value), components: [key])) }
        }
        return fields.sorted { $0.path < $1.path }
    }
    private static func kind(_ value: RuntimeValue) -> NovelSourceFieldKind {
        switch value { case .number: return .number; case .bool: return .bool; case .string: return .text; default: return .json }
    }
    func definition() throws -> String {
        var result = base
        let fields = tabs.flatMap(\.fields) + extensionFields + [
            .init(path: "bookSourceType", label: "内容类型", kind: .number),
            .init(path: enabledKey, label: "启用", kind: .bool),
            .init(path: "enabledExplore", label: "发现", kind: .bool),
            .init(path: "enabledCookieJar", label: "CookieJar", kind: .bool)]
        for field in fields {
            guard let text = edits[field.id] else { continue }
            let value: RuntimeValue
            switch field.kind {
            case .text: value = .string(text)
            case .bool:
                guard ["true", "false"].contains(text) else { throw NovelError.invalidRule([field.label + "：请输入 true 或 false"]) }
                value = .bool(text == "true")
            case .number:
                guard let number = Double(text), number.isFinite else { throw NovelError.invalidRule([field.label + "：数值无效"]) }
                value = .number(number)
            case .json: value = try RuntimeJSON.decode(Data(text.utf8))
            }
            let parts = field.parts
            if parts.count == 1 { result[parts[0]] = value }
            else { var group = object(result[parts[0]]); group[parts[1]] = value; result[parts[0]] = .object(group) }
        }
        if `protocol` == .yiciyuan, edits[NovelSourceField(path: enabledKey, label: "").id] != nil { result["enabled"] = result[enabledKey] }
        if `protocol` == .yiciyuan, edits[NovelSourceField(path: "serialNumber", label: "").id] != nil { result["customOrder"] = result["serialNumber"] }
        for key in ["bookSourceName", "bookSourceUrl"] {
            let value = Self.text(result[key]).trimmingCharacters(in: .whitespacesAndNewlines)
            guard !value.isEmpty else { throw NovelError.invalidRule([key == "bookSourceName" ? "请填写源名称" : "请填写源 URL"]) }
            result[key] = .string(value)
        }
        return String(decoding: try RuntimeJSON.encode(.object(result)), as: UTF8.self)
    }
}

// Group tags are part of each item; removing a tag never removes its owner.
enum NovelGroups {
    static func split(_ text: String) -> [String] {
        Array(Set(text.components(separatedBy: CharacterSet(charactersIn: "，,;；\n")).map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty })).sorted { $0.localizedStandardCompare($1) == .orderedAscending }
    }
    static func replacing(_ text: String, old: String, new: String?) -> String {
        var groups = split(text).filter { $0 != old }
        if let new, !new.isEmpty { groups += split(new) }
        return Array(Set(groups)).sorted().joined(separator: ",")
    }
}
