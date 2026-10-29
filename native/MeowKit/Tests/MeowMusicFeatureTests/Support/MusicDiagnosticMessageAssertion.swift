import Foundation
import XCTest

/// Assert the complete user-visible context/reason, allowing only the locally
/// generated diagnostic ID to vary. A bare reason or arbitrary suffix fails.
func XCTAssertMusicDiagnosticMessage(
    _ actual: String?, _ message: String,
    file: StaticString = #filePath, line: UInt = #line
) {
    let pattern = "^" + NSRegularExpression.escapedPattern(for: message) + "（诊断 [A-F0-9]{8}）$"
    XCTAssertNotNil(actual?.range(of: pattern, options: .regularExpression),
                    "Expected contextual safe diagnostic, got \(actual ?? "nil")", file: file, line: line)
}
