import Foundation
import XCTest
@testable import MeowStorage

final class NativeFileProtectionVerificationTests: XCTestCase {
    func testDeviceNeverAcceptsMissingOrWeakerOrUnknownProtection() {
        let values: [Any?] = [nil, "NSFileProtectionNone", "NSFileProtectionCompleteUnlessOpen",
                             "NSFileProtectionCompleteUntilFirstUserAuthentication", "unknown", 0]
        for value in values {
            XCTAssertEqual(NativeFileProtectionVerification.inspect(value, simulator: false), .rejected)
        }
        XCTAssertEqual(NativeFileProtectionVerification.inspect("NSFileProtectionComplete", simulator: false), .complete)
    }
    func testSimulatorAbsenceIsExplicitlyNotVerifiedDeviceProtection() {
        XCTAssertEqual(NativeFileProtectionVerification.inspect(nil, simulator: true), .unavailableInSimulator)
        XCTAssertNotEqual(NativeFileProtectionVerification.inspect(nil, simulator: true), .complete)
        let values: [Any] = ["NSFileProtectionNone", "NSFileProtectionCompleteUnlessOpen",
                            "NSFileProtectionCompleteUntilFirstUserAuthentication", "unknown", 0]
        for value in values {
            XCTAssertEqual(NativeFileProtectionVerification.inspect(value, simulator: true), .rejected)
        }
        XCTAssertEqual(NativeFileProtectionVerification.inspect("NSFileProtectionComplete", simulator: true), .complete)
    }
    func testCurrentTargetCannotSelectAnotherPlatformsPolicy() {
        #if targetEnvironment(simulator)
        XCTAssertEqual(NativeFileProtectionVerification.inspect(nil), .unavailableInSimulator)
        #else
        XCTAssertEqual(NativeFileProtectionVerification.inspect(nil), .rejected)
        #endif
    }
}
