#if os(iOS)
import UIKit
import XCTest
@testable import MeowUI

/// Operates the shared manager's real controls without retaining old settings-list adapters.
@MainActor
enum ManagementListTestUI {
    static func button(_ id: String, in view: UIView) throws -> UIButton {
        try XCTUnwrap(views(view).first { $0.accessibilityIdentifier == id } as? UIButton)
    }

    static func cell(_ id: String, in manager: ManagementListView) throws -> UITableViewCell {
        let table = manager.table
        table.layoutIfNeeded()
        for row in 0..<table.numberOfRows(inSection: 0) {
            let index = IndexPath(row: row, section: 0)
            let candidate = table.dataSource?.tableView(table, cellForRowAt: index)
            if candidate?.accessibilityIdentifier == "management.row." + id {
                table.scrollToRow(at: index, at: .top, animated: false)
                table.layoutIfNeeded()
                return try XCTUnwrap(table.cellForRow(at: index))
            }
        }
        XCTFail("Missing management row: " + id)
        throw NSError(domain: "ManagementListTestUI", code: 1)
    }

    static func rowIDs(in manager: ManagementListView) -> [String] {
        let table = manager.table
        return (0..<table.numberOfRows(inSection: 0)).compactMap {
            table.dataSource?.tableView(table, cellForRowAt: .init(row: $0, section: 0)).accessibilityIdentifier
        }
    }

    static func select(_ id: String, in manager: ManagementListView) throws {
        let cell = try cell(id, in: manager)
        let button = try button("management.select." + id, in: cell)
        XCTAssertTrue(button.isEnabled); XCTAssertFalse(button.isHidden)
        button.sendActions(for: .touchUpInside)
    }

    static func open(_ id: String, in manager: ManagementListView) throws {
        let cell = try cell(id, in: manager)
        let index = try XCTUnwrap(manager.table.indexPath(for: cell))
        manager.table.delegate?.tableView?(manager.table, didSelectRowAt: index)
    }

    static func menuAction(_ title: String, on button: UIButton) throws -> UIAction {
        XCTAssertTrue(button.isEnabled)
        let action = try XCTUnwrap(button.menu?.children.compactMap { $0 as? UIAction }.first { $0.title == title })
        XCTAssertFalse(action.attributes.contains(.disabled))
        return action
    }

    static func activateMenu(_ title: String, on button: UIButton) throws {
        let action = try menuAction(title, on: button)
        let trigger = UIButton()
        trigger.addAction(action, for: .touchUpInside)
        trigger.sendActions(for: .touchUpInside)
    }

    private static func views(_ view: UIView) -> [UIView] { [view] + view.subviews.flatMap(views) }
}
#endif
