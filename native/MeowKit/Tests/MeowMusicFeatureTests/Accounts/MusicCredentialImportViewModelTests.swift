#if os(iOS) || os(macOS)
import Foundation
import XCTest
@testable import MeowMusicFeature

private actor CredentialImportPageGate {
    private var entered = false, released = false
    private var arrivals: [CheckedContinuation<Void, Never>] = []
    private var waits: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        entered = true; arrivals.forEach { $0.resume() }; arrivals.removeAll()
        if !released { await withCheckedContinuation { waits.append($0) } }
    }
    func started() async { if !entered { await withCheckedContinuation { arrivals.append($0) } } }
    func release() { released = true; waits.forEach { $0.resume() }; waits.removeAll() }
}

private actor CredentialImportPageService: MusicCredentialsImporting {
    private var previews = 0, commits = 0
    private var cancelled: Set<UUID> = []
    private var replacing = false, wrongPlatform = false, wrongPhase = false
    private var previewGate: CredentialImportPageGate?, commitGate: CredentialImportPageGate?
    func configure(replacing: Bool = false, wrongPlatform: Bool = false, wrongPhase: Bool = false,
                   previewGate: CredentialImportPageGate? = nil, commitGate: CredentialImportPageGate? = nil) {
        self.replacing = replacing; self.wrongPlatform = wrongPlatform; self.wrongPhase = wrongPhase
        self.previewGate = previewGate; self.commitGate = commitGate
    }
    func counts() -> (previews: Int, commits: Int, cancellations: Int) { (previews, commits, cancelled.count) }
    func previewCredentialImport(_ input: MusicCredentialImportInput, platform: MusicAccountPlatform,
                                 format: MusicCredentialImportFormat, requestID: UUID) async throws -> MusicCredentialImportPreview {
        _ = try input.consume(); previews += 1
        if let previewGate { await previewGate.wait() }
        return .init(id: requestID, platform: wrongPlatform ? .netease : platform, deviceState: .requiresVerification,
                     requiresReplacement: replacing, expiresAt: Date().addingTimeInterval(180))
    }
    func commitCredentialImport(id: UUID, replaceExisting: Bool) async -> MusicAccountStatus {
        commits += 1
        if let commitGate { await commitGate.wait() }
        return .init(platform: .qq, phase: wrongPhase ? .signedIn : .requiresValidation, canLogin: true)
    }
    func cancelCredentialImport(id: UUID) { cancelled.insert(id) }
}

@MainActor final class MusicCredentialImportViewModelTests: XCTestCase {
    func testPreviewShowsOnlySafeMetadataAndNeedsExplicitReplacementConfirmation() async throws {
        let service = CredentialImportPageService()
        await service.configure(replacing: true)
        let model = MusicCredentialImportViewModel(service: service)
        model.activate(isAvailable: true, accessGeneration: UUID())
        model.previewText("synthetic-input-not-a-real-credential")
        await model.flush()
        let preview = try XCTUnwrap(model.preview)
        XCTAssertFalse(String(reflecting: preview).contains("synthetic-input"))
        model.commit(replaceExisting: false)
        XCTAssertEqual(model.error, .confirmationRequired)
        let before = await service.counts(); XCTAssertEqual(before.commits, 0)
        model.commit(replaceExisting: true); await model.flush()
        XCTAssertEqual(model.completed?.phase, .requiresValidation)
        XCTAssertEqual(model.completed?.nickname, "")
        XCTAssertNil(model.preview)
        let after = await service.counts(); XCTAssertEqual(after.commits, 1)
        model.deactivate()
    }
    func testLatePreviewAfterLeaveLockGenerationChangeOrCancelCannotRefillUI() async throws {
        for intent in ["leave", "lock", "generation", "cancel"] {
            let service = CredentialImportPageService(), gate = CredentialImportPageGate()
            await service.configure(previewGate: gate)
            let model = MusicCredentialImportViewModel(service: service)
            model.activate(isAvailable: true, accessGeneration: UUID())
            model.previewText("synthetic-input")
            let pending = model.pendingOperation
            await gate.started()
            switch intent {
            case "leave": model.deactivate()
            case "lock": model.setAccess(isAvailable: false, accessGeneration: UUID())
            case "generation": model.setAccess(isAvailable: true, accessGeneration: UUID())
            default: model.cancel()
            }
            await gate.release(); await pending?.value
            XCTAssertNil(model.preview); XCTAssertNil(model.completed); XCTAssertNil(model.error)
            let calls = await service.counts()
            XCTAssertEqual(calls.commits, 0); XCTAssertGreaterThanOrEqual(calls.cancellations, 1)
            model.deactivate()
        }
    }
    func testLateCommitCannotPublishSuccessAndWrongResultCannotClaimSignedIn() async throws {
        let service = CredentialImportPageService(), gate = CredentialImportPageGate()
        await service.configure(commitGate: gate)
        let model = MusicCredentialImportViewModel(service: service)
        model.activate(isAvailable: true, accessGeneration: UUID())
        model.previewText("synthetic-input"); await model.flush()
        model.commit(replaceExisting: false)
        let task = model.pendingOperation
        await gate.started(); model.deactivate()
        await gate.release(); await task?.value
        XCTAssertNil(model.completed); XCTAssertNil(model.error)

        await service.configure(wrongPhase: true)
        model.activate(isAvailable: true, accessGeneration: UUID())
        model.previewText("synthetic-input"); await model.flush()
        model.commit(replaceExisting: false); await model.flush()
        XCTAssertNil(model.completed); XCTAssertEqual(model.error, .invalidFormat)
        model.deactivate()
    }
    func testFileReadUsesBudgetAndLateFileCannotStartImport() async throws {
        let service = CredentialImportPageService(), gate = CredentialImportPageGate()
        let model = MusicCredentialImportViewModel(service: service, readFile: { _, budget in
            guard budget == MusicCredentialImportInput.maximumBytes else { throw MusicCredentialImportError.sizeLimit }
            await gate.wait()
            return Data("synthetic-file-input".utf8)
        })
        model.activate(isAvailable: true, accessGeneration: UUID())
        model.previewFile(URL(fileURLWithPath: "/synthetic/not-read.json"))
        let task = model.pendingOperation
        await gate.started(); model.cancel()
        await gate.release(); await task?.value
        let calls = await service.counts()
        XCTAssertEqual(calls.previews, 0); XCTAssertEqual(calls.commits, 0)
        XCTAssertNil(model.preview); XCTAssertNil(model.error)
        model.deactivate()
    }
    func testPlatformFormatChangeOversizeAndUntrustedFileErrorsAreSafe() async throws {
        let service = CredentialImportPageService()
        let model = MusicCredentialImportViewModel(service: service, readFile: { _, _ in
            throw NSError(domain: "synthetic-private-path", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "synthetic-secret-must-not-appear"])
        })
        model.activate(isAvailable: true, accessGeneration: UUID())
        model.selectFormat(.qqCookie); model.selectPlatform(.netease)
        XCTAssertEqual(model.format, .iwebPlusSnapshot); XCTAssertFalse(model.formats.contains(.qqCookie))
        model.previewText(String(repeating: "x", count: MusicCredentialImportInput.maximumBytes + 1))
        XCTAssertEqual(model.error, .sizeLimit)
        model.previewFile(URL(fileURLWithPath: "/synthetic/not-read.json")); await model.flush()
        XCTAssertFalse(model.error?.localizedDescription.contains("synthetic-secret") ?? true)
        let calls = await service.counts(); XCTAssertEqual(calls.previews, 0)
        model.deactivate()
    }
}
#endif
