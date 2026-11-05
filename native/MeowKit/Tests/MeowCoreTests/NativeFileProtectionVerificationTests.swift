import Foundation
import XCTest
@testable import MeowStorage

final class NativeFileProtectionVerificationTests: XCTestCase {
    private let accepted = "NSFileProtectionCompleteUntilFirstUserAuthentication"

    func testDeviceAcceptsOnlyProtectionUntilFirstUserAuthentication() {
        let values: [Any?] = [nil, "NSFileProtectionNone", "NSFileProtectionComplete",
                             "NSFileProtectionCompleteUnlessOpen", "unknown", 0]
        for value in values {
            XCTAssertEqual(NativeFileProtectionVerification.inspect(value, simulator: false), .rejected)
        }
        XCTAssertEqual(NativeFileProtectionVerification.inspect(accepted, simulator: false),
                       .completeUntilFirstUserAuthentication)
    }
    func testSimulatorAbsenceIsExplicitlyNotVerifiedDeviceProtection() {
        XCTAssertEqual(NativeFileProtectionVerification.inspect(nil, simulator: true), .unavailableInSimulator)
        XCTAssertNotEqual(NativeFileProtectionVerification.inspect(nil, simulator: true),
                          .completeUntilFirstUserAuthentication)
        let values: [Any] = ["NSFileProtectionNone", "NSFileProtectionComplete",
                            "NSFileProtectionCompleteUnlessOpen", "unknown", 0]
        for value in values {
            XCTAssertEqual(NativeFileProtectionVerification.inspect(value, simulator: true), .rejected)
        }
        XCTAssertEqual(NativeFileProtectionVerification.inspect(accepted, simulator: true),
                       .completeUntilFirstUserAuthentication)
    }
    func testCurrentTargetCannotSelectAnotherPlatformsPolicy() {
        #if targetEnvironment(simulator)
        XCTAssertEqual(NativeFileProtectionVerification.inspect(nil), .unavailableInSimulator)
        #else
        XCTAssertEqual(NativeFileProtectionVerification.inspect(nil), .rejected)
        #endif
    }
}
