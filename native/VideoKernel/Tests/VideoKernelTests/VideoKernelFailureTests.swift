import XCTest
import KSPlayer
@testable import MeowVideoKernel

final class VideoKernelFailureTests: XCTestCase {
    func testInputAndProbePreserveNativeTransportFailures() {
        for phase in [KSPlayerErrorCode.formatOpenInput, .formatFindStreamInfo, .readFrame] {
            for code in [KSPlayer.AVError.httpForbidden.code, KSPlayer.AVError.httpNotFound.code,
                         -Int32(EIO), -Int32(ECONNRESET), -Int32(ETIMEDOUT)] {
                XCTAssertEqual(VideoKernelFailure(NSError(errorCode: phase, avErrorCode: code)),
                               .transportFailed)
            }
        }
    }

    func testMalformedProbeAndCodecFailuresAreNotTransportFailures() {
        XCTAssertEqual(VideoKernelFailure(NSError(errorCode: .formatOpenInput,
            avErrorCode: KSPlayer.AVError.invalidData.code)), .formatFailed)
        XCTAssertEqual(VideoKernelFailure(NSError(errorCode: .formatFindStreamInfo,
            avErrorCode: KSPlayer.AVError.invalidData.code)), .formatFailed)
        XCTAssertEqual(VideoKernelFailure(NSError(errorCode: .codecVideoReceiveFrame,
            avErrorCode: KSPlayer.AVError.invalidData.code)), .decodeFailed)
    }

    func testEncryptedInputDoesNotHideTransportFailureAsDecryption() {
        XCTAssertEqual(VideoKernelFailure(NSError(errorCode: .formatFindStreamInfo,
            avErrorCode: KSPlayer.AVError.httpForbidden.code), encrypted: true), .transportFailed)
        XCTAssertEqual(VideoKernelFailure(NSError(errorCode: .codecVideoReceiveFrame,
            avErrorCode: KSPlayer.AVError.invalidData.code), encrypted: true), .decryptionFailed)
    }
}
