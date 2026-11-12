// SPDX-License-Identifier: GPL-3.0-only
import Foundation

public struct MusicDailyStyleTag: Sendable, Equatable, Identifiable {
    public let id: String
    public let name: String
    public init(id: String, name: String) { self.id = id; self.name = name }
}
public struct MusicDailyStyleCategory: Sendable, Equatable, Identifiable {
    public let id: String
    public let name: String
    public let tags: [MusicDailyStyleTag]
    public init(id: String, name: String, tags: [MusicDailyStyleTag]) {
        self.id = id; self.name = name; self.tags = tags
    }
}
public struct MusicDailyStyleSelection: Sendable, Equatable {
    public let category: String
    public let tags: [MusicDailyStyleTag]
    public init(category: String, tags: [MusicDailyStyleTag]) { self.category = category; self.tags = tags }
    public func validate(in categories: [MusicDailyStyleCategory]) throws {
        guard let value = categories.first(where: { $0.id == category }), !tags.isEmpty,
              Set(tags.map(\.id)).count == tags.count,
              tags.allSatisfy({ tag in value.tags.contains { $0.id == tag.id } }) else {
            throw MusicServiceError.invalidRequest
        }
    }
}
public struct MusicDailyStyleSave: Sendable {
    public let page: MusicNativeTrackPage
    public let saveConfirmed: Bool
    public let selectionChanged: Bool
    public init(page: MusicNativeTrackPage, saveConfirmed: Bool, selectionChanged: Bool) {
        self.page = page; self.saveConfirmed = saveConfirmed; self.selectionChanged = selectionChanged
    }
}

/// An explicit account mutation, not a preference written by browsing.
/// Both operations are bound to the already-opened recommendation lease.
public protocol MusicNativeDailyStyleProviding: MusicNativeAccountQueueProviding {
    func dailyStyleCategories(_ lease: MusicAccountQueueLease) async throws -> [MusicDailyStyleCategory]
    func saveDailyStyle(_ lease: MusicAccountQueueLease, selection: MusicDailyStyleSelection) async throws -> MusicDailyStyleSave
}
public struct MusicDailyStyleEditing: Sendable {
    public let categories: @Sendable () async throws -> [MusicDailyStyleCategory]
    public let save: @Sendable (MusicDailyStyleSelection) async throws -> MusicDailyStyleSave
    public init(categories: @escaping @Sendable () async throws -> [MusicDailyStyleCategory],
                save: @escaping @Sendable (MusicDailyStyleSelection) async throws -> MusicDailyStyleSave) {
        self.categories = categories; self.save = save
    }
}
