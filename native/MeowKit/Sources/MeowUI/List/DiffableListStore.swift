#if os(iOS)
import UIKit

/// Order-preserving, duplicate-safe builder for `NSDiffableDataSourceSnapshot`.
/// Enforces unique section and item identifiers at the shared boundary so individual
/// pages never hand-roll dedup sets or risk duplicate-ID diffable crashes.
public struct DiffableSnapshotBuilder<SectionID: Hashable & Sendable, ItemID: Hashable & Sendable> {
    private var sectionOrder: [SectionID] = []
    private var seenSections: Set<SectionID> = []
    private var itemsBySection: [SectionID: [ItemID]] = [:]
    private var itemOrder: [ItemID] = []
    private var seenItems: Set<ItemID> = []

    public init() {}

    public var sectionIdentifiers: [SectionID] { sectionOrder }
    public var itemIdentifiers: [ItemID] { itemOrder }

    @discardableResult
    public mutating func appendSection(_ section: SectionID, items: [ItemID] = []) -> [ItemID] {
        if seenSections.insert(section).inserted {
            sectionOrder.append(section)
            itemsBySection[section] = []
        }
        return appendItems(items, toSection: section)
    }

    @discardableResult
    public mutating func appendItems(_ items: [ItemID], toSection section: SectionID) -> [ItemID] {
        if seenSections.insert(section).inserted {
            sectionOrder.append(section)
            itemsBySection[section] = []
        }
        var admitted: [ItemID] = []
        admitted.reserveCapacity(items.count)
        for id in items where seenItems.insert(id).inserted {
            admitted.append(id)
            itemOrder.append(id)
        }
        if !admitted.isEmpty {
            itemsBySection[section, default: []].append(contentsOf: admitted)
        }
        return admitted
    }

    public func build(reconfiguring changed: [ItemID] = []) -> NSDiffableDataSourceSnapshot<SectionID, ItemID> {
        var snapshot = NSDiffableDataSourceSnapshot<SectionID, ItemID>()
        snapshot.appendSections(sectionOrder)
        for section in sectionOrder {
            if let ids = itemsBySection[section], !ids.isEmpty {
                snapshot.appendItems(ids, toSection: section)
            }
        }
        if !changed.isEmpty {
            var seenChanged: Set<ItemID> = []
            let valid = changed.filter { seenItems.contains($0) && seenChanged.insert($0).inserted }
            if !valid.isEmpty {
                snapshot.reconfigureItems(valid)
            }
        }
        return snapshot
    }
}

/// Unified 1:1 diffable list projection owning deduplicated sections, ordered items,
/// O(1) lookup by ID, incremental `reconfigureItems` computation, and no-op skipping.
@MainActor
public struct IndexedDiffableProjection<SectionID: Hashable & Sendable, ItemID: Hashable & Sendable, Item> {
    public struct Update {
        public let snapshot: NSDiffableDataSourceSnapshot<SectionID, ItemID>
        public let changedItemIDs: [ItemID]
        public let structureChanged: Bool
        public var hasChanges: Bool { structureChanged || !changedItemIDs.isEmpty }
    }

    public private(set) var sections: [SectionID] = []
    public private(set) var items: [Item] = []
    public private(set) var itemIDs: [ItemID] = []
    public private(set) var itemsByID: [ItemID: Item] = [:]
    private var itemIDsBySection: [SectionID: [ItemID]] = [:]
    private var hasUpdated = false

    public init() {}

    public subscript(id: ItemID) -> Item? {
        itemsByID[id]
    }

    public mutating func update(
        sections inputSections: [(id: SectionID, items: [Item])],
        id: (Item) -> ItemID,
        isChanged: (Item, Item) -> Bool
    ) -> Update {
        let previousSections = sections
        let previousBySection = itemIDsBySection
        let previousItemsByID = itemsByID

        var builder = DiffableSnapshotBuilder<SectionID, ItemID>()
        var nextItems: [Item] = []
        var nextByID: [ItemID: Item] = [:]
        var nextBySection: [SectionID: [ItemID]] = [:]
        var changed: [ItemID] = []
        var seenSections: Set<SectionID> = []

        for section in inputSections {
            guard seenSections.insert(section.id).inserted else { continue }
            var sectionIDs: [ItemID] = []
            sectionIDs.reserveCapacity(section.items.count)
            for item in section.items {
                let itemID = id(item)
                guard nextByID[itemID] == nil else { continue }
                nextByID[itemID] = item
                nextItems.append(item)
                sectionIDs.append(itemID)
                if let previous = previousItemsByID[itemID], isChanged(previous, item) {
                    changed.append(itemID)
                }
            }
            _ = builder.appendSection(section.id, items: sectionIDs)
            nextBySection[section.id] = sectionIDs
        }

        let nextSections = builder.sectionIdentifiers
        let structureChanged = !hasUpdated
            || previousSections != nextSections
            || nextSections.contains { previousBySection[$0] != nextBySection[$0] }

        hasUpdated = true
        sections = nextSections
        items = nextItems
        itemIDs = builder.itemIdentifiers
        itemsByID = nextByID
        itemIDsBySection = nextBySection

        return Update(
            snapshot: builder.build(reconfiguring: changed),
            changedItemIDs: changed,
            structureChanged: structureChanged
        )
    }

    public mutating func update(
        section: SectionID,
        items inputItems: [Item],
        id: (Item) -> ItemID,
        isChanged: (Item, Item) -> Bool
    ) -> Update {
        update(sections: [(id: section, items: inputItems)], id: id, isChanged: isChanged)
    }
}

public extension IndexedDiffableProjection where Item: Equatable {
    mutating func update(
        sections inputSections: [(id: SectionID, items: [Item])],
        id: (Item) -> ItemID
    ) -> Update {
        update(sections: inputSections, id: id, isChanged: !=)
    }

    mutating func update(
        section: SectionID,
        items inputItems: [Item],
        id: (Item) -> ItemID
    ) -> Update {
        update(sections: [(id: section, items: inputItems)], id: id, isChanged: !=)
    }
}

@MainActor
public extension UITableViewDiffableDataSource {
    @discardableResult
    func applyIfNeeded<Item>(
        _ update: IndexedDiffableProjection<SectionIdentifierType, ItemIdentifierType, Item>.Update,
        animatingDifferences: Bool = false,
        completion: (() -> Void)? = nil
    ) -> Bool where SectionIdentifierType: Sendable, ItemIdentifierType: Sendable {
        guard update.hasChanges else {
            completion?()
            return false
        }
        apply(update.snapshot, animatingDifferences: animatingDifferences, completion: completion)
        return true
    }
}

@MainActor
public extension UICollectionViewDiffableDataSource {
    @discardableResult
    func applyIfNeeded<Item>(
        _ update: IndexedDiffableProjection<SectionIdentifierType, ItemIdentifierType, Item>.Update,
        animatingDifferences: Bool = false,
        completion: (() -> Void)? = nil
    ) -> Bool where SectionIdentifierType: Sendable, ItemIdentifierType: Sendable {
        guard update.hasChanges else {
            completion?()
            return false
        }
        apply(update.snapshot, animatingDifferences: animatingDifferences, completion: completion)
        return true
    }
}
#endif
