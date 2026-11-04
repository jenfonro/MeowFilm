import XCTest
import Foundation
import MeowContracts

final class ContractTests: XCTestCase {
    func testSiteIdentityIncludesInstallation() throws {
        let first = try SourceAddress(installationID: UUID(), siteKey: "same")
        let second = try SourceAddress(installationID: UUID(), siteKey: "same")
        XCTAssertNotEqual(first, second)
    }
    func testDecoderCannotBypassAddressValidation() {
        let bytes = Data(#"{"installationID":"00000000-0000-0000-0000-000000000001","siteKey":" "}"#.utf8)
        XCTAssertThrowsError(try JSONDecoder().decode(SourceAddress.self, from: bytes))
    }
    func testOpaqueSiteKeysDoNotMergeUnicodeVariantsAcrossRuntimes() throws {
        let installation = UUID()
        let first = try SourceAddress(installationID: installation, siteKey: "\u{00e9}")
        let second = try SourceAddress(installationID: installation, siteKey: "e\u{0301}")
        XCTAssertNotEqual(first, second)
        XCTAssertEqual(Set([first, second]).count, 2)
        XCTAssertEqual(try JSONDecoder().decode(SourceAddress.self, from: JSONEncoder().encode(first)), first)
    }
    func testInstallationUUIDUsesCanonicalWireSpelling() throws {
        let id = UUID(uuidString: "ABCDEF12-3456-7890-ABCD-EF1234567890")!
        let address = try SourceAddress(installationID: id, siteKey: "site")
        let data = try JSONEncoder().encode(address)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: String])
        XCTAssertEqual(object["installationID"], "abcdef12-3456-7890-abcd-ef1234567890")
        XCTAssertEqual(try JSONDecoder().decode(SourceAddress.self, from: data), address)
    }
    func testPlaybackRejectsCredentialsAndInvalidOffsets() {
        XCTAssertThrowsError(try PlaybackResource(id: "1", url: URL(string: "https://user:secret@example.invalid/v")!, title: "x"))
        XCTAssertThrowsError(try PlaybackResource(id: "1", url: URL(string: "javascript:alert(0)")!, title: "x"))
        XCTAssertThrowsError(try PlaybackResource(id: "1", url: URL(string: "https://example.invalid/v")!, title: "x", startPosition: .nan))
    }
}
