#if os(iOS)
import XCTest
import UIKit
import SwiftUI
import Combine
import MeowContracts
import MeowPlayback
@testable import MeowMusicFeature
@testable import MeowUI

@MainActor
final class MusicSettingsPlatformTests: XCTestCase {
    func testUserDataResetRequiresDestructiveConfirmationAndCancelDoesNotRequestIt() throws {
        let domain = "MusicResetUI." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        var requests = 0
        let page = MusicSettingsViewController(model: owner, appearance: appearance,
            router: MusicSettingsRouterFixture(), resetUserData: { requests += 1 })
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
        window.rootViewController = page; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        let visible = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { page.canPresentPageAlert }
        }, evaluatedWith: page)
        wait(for: [visible], timeout: 3)
        let attachment = XCTAttachment(image: UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
            page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true)
        })
        attachment.name = "Settings user data reset"; attachment.lifetime = .keepAlways
        add(attachment)
        page.confirmUserDataReset()
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.title, "清除本机用户数据？")
        XCTAssertTrue(alert.message?.contains("云端数据和 App 外的原始文件不受影响") == true)
        XCTAssertTrue(alert.message?.contains("重新打开") == true)
        XCTAssertEqual(alert.actions.map(\.style), [.cancel, .destructive])
        XCTAssertEqual(alert.actions.map(\.title), ["取消", "清除并等待重启"])
        XCTAssertEqual(requests, 0, "Opening the confirmation is not consent")
        let cancelled = expectation(description: "confirmation dismissed without consent")
        page.dismiss(animated: false) { cancelled.fulfill() }
        wait(for: [cancelled], timeout: 3)
        XCTAssertEqual(requests, 0)
        let closed = expectation(description: "owner closed")
        Task { await owner.close(); closed.fulfill() }
        wait(for: [closed], timeout: 3)
    }

    func testConfirmedUserDataResetIsSingleFlightAndFailureAllowsRetry() throws {
        let domain = "MusicResetUI." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        var requests = 0
        let requested = expectation(description: "confirmed request")
        let retried = expectation(description: "retry request")
        let page = MusicSettingsViewController(model: owner, appearance: .init(defaults: defaults),
            router: MusicSettingsRouterFixture(), resetUserData: {
                requests += 1
                if requests == 1 { requested.fulfill() } else { retried.fulfill() }
                throw MusicServiceError.storageFailed
            })
        page.loadViewIfNeeded()
        page.requestUserDataReset()
        page.requestUserDataReset()
        wait(for: [requested], timeout: 3)
        XCTAssertEqual(requests, 1)
        page.requestUserDataReset()
        wait(for: [retried], timeout: 3)
        XCTAssertEqual(requests, 2)
        let closed = expectation(description: "owner closed")
        Task { await owner.close(); closed.fulfill() }
        wait(for: [closed], timeout: 3)
    }

    func testSourceManagementRendersBuiltInAndScriptSwitchesTogetherAndWritesThroughBindings() throws {
        let domain = "MusicSourceSettings." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let service = makeService(), scripts = MusicSettingsScriptFixture()
        let model = MusicViewModel(service: service, player: MusicSettingsPlayerFixture(), scriptSources: scripts)
        let state = MusicSourceManagementState(model: model)
        let loaded = expectation(description: "source list loaded")
        Task { await model.load(); state.start(); await state.flush(); loaded.fulfill() }
        wait(for: [loaded], timeout: 3)
        XCTAssertEqual(MusicSettingsDestination.sources.title, "音源管理")
        XCTAssertEqual(MusicSettingsDestination.sources.accessibilityIdentifier, "music.settings.sources")
        let host = UIHostingController(rootView: MusicSourceManagementContent(state: state, theme: PageTheme(palette: appearance.pageColorway)))
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
        window.rootViewController = host; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        func views(_ view: UIView) -> [UIView] { [view] + view.subviews.flatMap(views) }
        let switchesReady = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            host.view.layoutIfNeeded()
            return views(host.view).compactMap { $0 as? UISwitch }.count == 4
        }, object: nil)
        wait(for: [switchesReady], timeout: 3)
        let switches = views(host.view).compactMap { $0 as? UISwitch }.sorted { $0.convert(.zero, to: host.view).y < $1.convert(.zero, to: host.view).y }
        XCTAssertEqual(switches.count, 4)
        guard switches.count == 4 else { return }
        XCTAssertTrue(switches[0].isOn); XCTAssertFalse(switches[3].isOn)
        switches[0].setOn(false, animated: false); switches[0].sendActions(for: .valueChanged)
        switches[3].setOn(true, animated: false); switches[3].sendActions(for: .valueChanged)
        let saved = expectation(description: "switches persisted")
        Task { await state.flush(); saved.fulfill() }
        wait(for: [saved], timeout: 3)
        XCTAssertFalse(state.officialEnabled(.netease))
        XCTAssertEqual(state.scripts.first?.state, .ready)
        let screenshot = XCTAttachment(image: UIGraphicsImageRenderer(bounds: host.view.bounds).image { context in
            host.view.drawHierarchy(in: host.view.bounds, afterScreenUpdates: true)
        })
        screenshot.name = "Unified music sources"; screenshot.lifetime = .keepAlways
        add(screenshot)
        state.close()
        let closed = expectation(description: "source settings closed")
        Task { await model.close(); closed.fulfill() }
        wait(for: [closed], timeout: 3)
    }

    /// Synchronous on purpose: pages released inside a Swift task enter the
    /// back-deployed isolated-deinit path, which UIKit owners never use.
    func testSettingsConsumersUseTheAppOwnedHeaderAndKeepFloatingCards() throws {
        let domain = "MusicSettingsPlatformTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        let router = MusicSettingsRouterFixture()
        let root = MusicSettingsViewController(model: owner, appearance: appearance, router: router)
        let carrier = MusicSettingsPageViewController(appearance: appearance, content: EmptyView())
        let accounts = MusicAccountsViewController(model: owner, appearance: appearance, router: router)
        let login = MusicAccountLoginViewController(
            owner: owner, service: service, platform: .qq, appearance: appearance
        )
        let detail = MusicAccountDetailViewController(
            state: MusicAccountsSettingsState(owner: owner), platform: .qq, appearance: appearance, actions: .init()
        )
        let pages: [PageViewController] = [root, carrier, accounts, login, detail]
        for page in pages {
            XCTAssertFalse(page.isViewLoaded)
            XCTAssertNil(page.title)
            XCTAssertNil(page.navigationItem.title)
        }
        XCTAssertEqual(root.pageTitle, "设置")
        XCTAssertEqual(accounts.pageTitle, "平台账号")
        XCTAssertEqual(login.pageTitle, MusicAccountPlatform.qq.title)
        XCTAssertEqual(detail.pageTitle, MusicAccountPlatform.qq.title)
        for card in [login, detail] as [UIViewController] {
            XCTAssertEqual(card.modalPresentationStyle, .overFullScreen, "Account login and details float over the list")
            XCTAssertTrue(card.view is MusicAccountFloatingBackdrop)
        }
        XCTAssertNil(PageViewController(themeProvider: appearance).navigationItem.titleView)
        XCTAssertNil(HostingPageViewController(
            themeProvider: appearance, content: EmptyView()
        ).navigationItem.titleView)
        let closed = expectation(description: "owner closed")
        Task { await owner.close(); closed.fulfill() }
        wait(for: [closed], timeout: 2)
    }

    func testQRCompletionRequiresAuthoritativeSignedInStatus() async {
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        let state = MusicAccountLoginState(owner: owner, service: service, platform: .qq)
        defer { state.close() }
        await prepareQR(state)
        XCTAssertEqual(state.status, MusicLoginProgress.waiting.title)

        await service.completeQR(as: .requiresValidation)
        await finishQR(state)
        XCTAssertEqual(state.model.progress, .confirmed)
        XCTAssertEqual(state.model.rows.first { $0.platform == .qq }?.phase, .requiresValidation)
        XCTAssertFalse(state.signedIn)
        XCTAssertNotEqual(state.status, MusicLoginProgress.confirmed.title)
        XCTAssertTrue(state.canRetry)

        let accepted = expectation(description: "authoritative QR login accepted")
        let observation = state.$signedIn.filter { $0 }.prefix(1).sink { _ in accepted.fulfill() }
        defer { observation.cancel() }
        state.retry()
        await prepareQR(state)
        await service.completeQR(as: .signedIn)
        await finishQR(state)
        await fulfillment(of: [accepted], timeout: 2)
        XCTAssertEqual(state.model.rows.first { $0.platform == .qq }?.phase, .signedIn)
        XCTAssertTrue(state.signedIn)
        XCTAssertEqual(state.status, MusicLoginProgress.confirmed.title)
        XCTAssertFalse(state.canRetry)
        XCTAssertNil(state.model.challenge)
        await owner.close()
    }

    func testSignedInPresentationIsClearedWhenAccountAccessIsRevoked() async {
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        let state = MusicAccountLoginState(owner: owner, service: service, platform: .qq)
        defer { state.close() }
        await prepareQR(state)
        await service.completeQR(as: .signedIn)
        await finishQR(state)
        XCTAssertTrue(state.signedIn)

        let accessObservation = owner.accessChanges.receive(on: RunLoop.main)
            .sink { [weak state] in state?.reconcileAccess() }
        let revoked = expectation(description: "completion removed after access revocation")
        let completionObservation = state.$signedIn.filter { !$0 }.prefix(1)
            .sink { _ in revoked.fulfill() }
        defer { accessObservation.cancel(); completionObservation.cancel() }
        let generation = owner.accountAccessGeneration
        owner.setAvailable(false)
        await fulfillment(of: [revoked], timeout: 2)
        await flushPresentation()
        XCTAssertNotEqual(owner.accountAccessGeneration, generation)
        XCTAssertFalse(state.available)
        XCTAssertFalse(state.signedIn)
        XCTAssertFalse(state.canRetry)
        XCTAssertEqual(state.status, MusicAccountStatus.Phase.unavailable.title)
        XCTAssertTrue(state.model.rows.isEmpty)
        XCTAssertNil(state.image)
        let retainedStatus = await service.statuses()
        XCTAssertEqual(retainedStatus.first { $0.platform == .qq }?.phase, .signedIn,
                       "Revoking page access does not log out the native account")

        await service.setStatus(.signedOut, platform: .qq)
        let reopened = expectation(description: "new access generation reconciled")
        let availabilityObservation = state.$available.filter { $0 }.prefix(1)
            .sink { _ in reopened.fulfill() }
        defer { availabilityObservation.cancel() }
        owner.setAvailable(true)
        await fulfillment(of: [reopened], timeout: 2)
        await state.model.flush()
        await flushPresentation()
        XCTAssertFalse(state.signedIn)
        XCTAssertEqual(state.model.rows.first { $0.platform == .qq }?.phase, .signedOut)
        XCTAssertNotEqual(state.status, MusicLoginProgress.confirmed.title)
        await owner.close()
    }

    func testSamePlatformSessionChangeRechecksCompletion() async {
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        let state = MusicAccountLoginState(owner: owner, service: service, platform: .qq)
        defer { state.close() }
        await prepareQR(state)
        await service.completeQR(as: .signedIn)
        await finishQR(state)
        XCTAssertTrue(state.canFinish)

        state.accountSessionChanged(.netease)
        XCTAssertFalse(state.checkingAccount, "Another platform's session does not gate completion")
        XCTAssertTrue(state.canFinish)

        await service.setStatus(.signedOut, platform: .qq)
        state.accountSessionChanged(.qq)
        XCTAssertTrue(state.checkingAccount)
        XCTAssertFalse(state.canFinish, "Cached signed-in rows cannot finish after a session change")
        await state.model.flush()
        await flushPresentation()
        XCTAssertFalse(state.checkingAccount)
        XCTAssertFalse(state.signedIn)
        XCTAssertFalse(state.canFinish)
        XCTAssertEqual(state.model.rows.first { $0.platform == .qq }?.phase, .signedOut)
        await owner.close()
    }

    func testLateQRConfirmationCannotRestoreCompletionAfterAccessRevocation() async {
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        let state = MusicAccountLoginState(owner: owner, service: service, platform: .qq)
        defer { state.close() }
        await prepareQR(state)
        await service.completeQR(as: .signedIn)
        let entered = expectation(description: "QR confirmation request entered")
        let gate = MusicSettingsQRCheckGate { entered.fulfill() }
        await service.holdQRCheck(gate)
        state.model.setForeground(true)
        let pending = state.model.pendingPoll
        XCTAssertNotNil(pending)
        await fulfillment(of: [entered], timeout: 2)

        owner.setAvailable(false)
        state.reconcileAccess()
        XCTAssertFalse(state.available)
        await gate.release()
        await pending?.value
        await state.model.flush()
        await flushPresentation()
        let lateStatus = await service.statuses()
        XCTAssertEqual(lateStatus.first { $0.platform == .qq }?.phase, .signedIn)
        XCTAssertFalse(state.signedIn)
        XCTAssertFalse(state.canRetry)
        XCTAssertTrue(state.model.rows.isEmpty)
        XCTAssertEqual(state.status, MusicAccountStatus.Phase.unavailable.title)
        await owner.close()
    }

    func testLoginMethodsRespectQQAndOptionalSMSCapabilities() async {
        for platform in MusicAccountPlatform.allCases {
            let service = makeService()
            let accounts = MusicSettingsSMSAccountsFixture(base: service)
            let owner = makeOwner(service: service, accounts: accounts)
            let state = MusicAccountLoginState(owner: owner, service: accounts, platform: platform)
            defer { state.close() }
            await prepareQR(state)
            let supportsSMS = platform != .qq
            XCTAssertEqual(state.supportsSMS, supportsSMS)
            state.model.setForeground(true)
            state.selectMethod(.sms)
            state.fields.setPhone("10000000000")
            state.fields.setCode("000000")
            await flushPresentation()
            XCTAssertEqual(state.method, supportsSMS ? .sms : .qr)
            XCTAssertEqual(state.actions.value.canSend, supportsSMS)
            XCTAssertEqual(state.actions.value.canSubmit, supportsSMS)
            XCTAssertFalse(state.actions.value.sent, "No code was requested, so the action is not a resend")
            XCTAssertFalse(state.signedIn)
            let before = await service.smsCalls()
            XCTAssertTrue(before.sends.isEmpty)
            XCTAssertTrue(before.submits.isEmpty)

            if supportsSMS {
                let accepted = expectation(description: platform.title + " explicit SMS accepted")
                let observation = state.$signedIn.filter { $0 }.prefix(1).sink { _ in accepted.fulfill() }
                defer { observation.cancel() }
                state.actions.submit()
                await state.model.flush()
                await fulfillment(of: [accepted], timeout: 2)
                XCTAssertTrue(state.signedIn)
                XCTAssertEqual(state.model.rows.first { $0.platform == platform }?.phase, .signedIn)
                XCTAssertEqual(state.status, MusicLoginProgress.confirmed.title)
            } else {
                state.actions.send()
                state.actions.submit()
                await state.model.flush()
                await flushPresentation()
                XCTAssertFalse(state.signedIn)
            }
            let after = await service.smsCalls()
            XCTAssertTrue(after.sends.isEmpty, "Selecting a method or submitting a code must not send SMS")
            XCTAssertEqual(after.submits, supportsSMS ? [platform] : [])
            await owner.close()
        }

        for platform in [MusicAccountPlatform.netease, .kugou] {
            let service = makeService()
            let owner = makeOwner(service: service, accounts: service)
            let state = MusicAccountLoginState(owner: owner, service: service, platform: platform)
            defer { state.close() }
            await prepareQR(state)
            XCTAssertFalse(state.supportsSMS)
            state.model.setForeground(true)
            state.selectMethod(.sms)
            state.fields.setPhone("10000000000")
            state.fields.setCode("000000")
            await flushPresentation()
            XCTAssertEqual(state.method, .qr)
            XCTAssertFalse(state.actions.value.canSend)
            XCTAssertFalse(state.actions.value.canSubmit)
            state.actions.send()
            state.actions.submit()
            await state.model.flush()
            let calls = await service.smsCalls()
            XCTAssertTrue(calls.sends.isEmpty)
            XCTAssertTrue(calls.submits.isEmpty)
            XCTAssertFalse(state.signedIn)
            await owner.close()
        }
    }

    private func makeService() -> MusicSettingsServiceFixture {
        let image = UIGraphicsImageRenderer(size: CGSize(width: 8, height: 8)).pngData { context in
            UIColor.black.setFill()
            context.fill(CGRect(x: 0, y: 0, width: 8, height: 8))
        }
        return MusicSettingsServiceFixture(image: image)
    }

    private func makeOwner(
        service: MusicSettingsServiceFixture, accounts: any MusicAccountsServing
    ) -> MusicViewModel {
        MusicViewModel(service: service, player: MusicSettingsPlayerFixture(), accounts: accounts)
    }

    private func prepareQR(_ state: MusicAccountLoginState) async {
        state.start()
        await state.model.flush()
        state.model.setForeground(false)
        await flushPresentation()
        XCTAssertNotNil(state.model.challenge)
        XCTAssertNotNil(state.image)
        XCTAssertEqual(state.method, .qr)
        XCTAssertFalse(state.signedIn)
    }

    private func finishQR(_ state: MusicAccountLoginState) async {
        // The existing foreground-resume path checks immediately, without
        // replacing the production clock or waiting for its polling interval.
        state.model.setForeground(true)
        let pending = state.model.pendingPoll
        XCTAssertNotNil(pending)
        let completed = expectation(description: "immediate QR poll completed")
        var drained = false
        let drain = Task {
            await pending?.value
            drained = true
            completed.fulfill()
        }
        await fulfillment(of: [completed], timeout: 2)
        if !drained { state.model.setForeground(false) }
        await drain.value
        await state.model.flush()
        await flushPresentation()
    }

    private func flushPresentation() async {
        let flushed = expectation(description: "main-run-loop presentation delivered")
        RunLoop.main.perform { flushed.fulfill() }
        await fulfillment(of: [flushed], timeout: 2)
    }
}

private actor MusicSettingsServiceFixture: MusicServing, MusicAccountsServing {
    private let image: Data
    private var rows = MusicAccountPlatform.allCases.map {
        MusicAccountStatus(platform: $0, phase: .signedOut, canLogin: true)
    }
    private var challenge: (id: UUID, platform: MusicAccountPlatform)?
    private var qrCompletion: MusicAccountStatus.Phase?
    private var qrGate: MusicSettingsQRCheckGate?
    private var sends: [MusicAccountPlatform] = [], submits: [MusicAccountPlatform] = []
    private var settingsRevision = 0
    private var official = MusicAccountPlatform.allCases

    init(image: Data) { self.image = image }
    func completeQR(as phase: MusicAccountStatus.Phase) { qrCompletion = phase }
    func holdQRCheck(_ gate: MusicSettingsQRCheckGate) { qrGate = gate }
    func smsCalls() -> (sends: [MusicAccountPlatform], submits: [MusicAccountPlatform]) { (sends, submits) }
    func setStatus(_ phase: MusicAccountStatus.Phase, platform: MusicAccountPlatform) {
        let value = MusicAccountStatus(platform: platform, phase: phase, canLogin: true)
        rows = rows.map { $0.platform == platform ? value : $0 }
    }
    func statuses() -> [MusicAccountStatus] { rows }
    func validate(platform: MusicAccountPlatform) throws -> MusicAccountStatus { throw MusicServiceError.unsupported }
    func beginLogin(platform: MusicAccountPlatform) -> MusicLoginChallenge {
        let id = UUID()
        challenge = (id, platform)
        qrCompletion = nil
        setStatus(.signingIn, platform: platform)
        return .init(id: id, platform: platform, image: image, expiresAt: .distantFuture)
    }
    func checkLogin(challengeID: UUID) async throws -> MusicLoginProgress {
        guard let challenge, challenge.id == challengeID else { throw MusicServiceError.staleResult }
        guard let phase = qrCompletion else { return .waiting }
        let gate = qrGate; qrGate = nil
        if let gate { await gate.wait() }
        // Deliberately non-cooperative: a late native receipt must not revive
        // the revoked page's presentation, even if native status is signed in.
        setStatus(phase, platform: challenge.platform)
        if self.challenge?.id == challengeID { self.challenge = nil }
        return .confirmed
    }
    func cancelLogin(challengeID: UUID) {
        guard let challenge, challenge.id == challengeID else { return }
        self.challenge = nil
        if rows.first(where: { $0.platform == challenge.platform })?.phase == .signingIn {
            setStatus(.signedOut, platform: challenge.platform)
        }
    }
    func logout(platform: MusicAccountPlatform) { setStatus(.signedOut, platform: platform) }
    func sendSMS(platform: MusicAccountPlatform, countryCode: String, phone: String,
                 attemptID: UUID) throws -> MusicSMSSendReceipt {
        sends.append(platform)
        guard platform != .qq else { throw MusicServiceError.unsupported }
        return .init(retryAt: .distantPast)
    }
    func submitSMS(platform: MusicAccountPlatform, countryCode: String, phone: String,
                   code: String, attemptID: UUID) throws -> MusicAccountStatus {
        submits.append(platform)
        guard platform != .qq else { throw MusicServiceError.unsupported }
        setStatus(.signedIn, platform: platform)
        return .init(platform: platform, phase: .signedIn, canLogin: true)
    }
    func cancelSMSLogin(attemptID: UUID) {}

    func state() -> MusicPlaybackState { .init(settingsRevision: settingsRevision, officialSources: official) }
    func platforms() -> [MusicPlatform] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func collections() -> [MusicCollection] { [] }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { [] }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func queue() -> MusicQueueSnapshot { .init(revision: 0, currentId: nil, entries: []) }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState {
        guard change.ifRevision == settingsRevision else { throw MusicServiceError.staleRevision }
        official = change.officialSources ?? official
        settingsRevision += 1
        return state()
    }
    func sourceAvailability(for ref: MusicReference) -> MusicSourceAvailability { .init(options: [], scriptState: .notConfigured) }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}

private actor MusicSettingsScriptFixture: MusicScriptSourceManaging {
    var enabled = false
    func item() -> MusicScriptSourceItem {
        .init(id: "fixture", name: "合成 LX 音源", version: "2", origin: .file,
              platforms: [.qq], enabled: enabled, state: enabled ? .ready : .disabled)
    }
    func scriptSources() -> [MusicScriptSourceItem] { [item()] }
    func importScriptSource(_ data: Data, fileName: String) throws -> MusicScriptSourceItem { throw MusicServiceError.unsupported }
    func downloadScriptSource(from url: URL) throws -> MusicScriptSourceItem { throw MusicServiceError.unsupported }
    func setScriptSourceEnabled(_ id: String, enabled: Bool) -> MusicScriptSourceItem { self.enabled = enabled; return item() }
    func scriptSourceDetail(_ id: String) -> MusicScriptSourceDetail { .init(item: item(), networkURL: nil, rollbackAvailable: false) }
    func editScriptSource(_ id: String, name: String, networkURL: String?) -> MusicScriptSourceItem { item() }
    func updateScriptSource(_ id: String) -> MusicScriptSourceItem { item() }
    func updateScriptSource(_ id: String, data: Data, fileName: String) -> MusicScriptSourceItem { item() }
    func rollbackScriptSource(_ id: String) throws -> MusicScriptSourceItem { throw MusicServiceError.unsupported }
    func checkScriptSource(_ id: String) -> MusicScriptSourceItem { item() }
    func removeScriptSource(_ id: String) throws { throw MusicServiceError.unsupported }
}

/// The base fixture is QR-only; the optional SMS port exists only on this wrapper.
private struct MusicSettingsSMSAccountsFixture: MusicAccountsServing, MusicAccountsSMSServing {
    let base: MusicSettingsServiceFixture
    func statuses() async throws -> [MusicAccountStatus] { await base.statuses() }
    func validate(platform: MusicAccountPlatform) async throws -> MusicAccountStatus { try await base.validate(platform: platform) }
    func beginLogin(platform: MusicAccountPlatform) async throws -> MusicLoginChallenge { await base.beginLogin(platform: platform) }
    func checkLogin(challengeID: UUID) async throws -> MusicLoginProgress { try await base.checkLogin(challengeID: challengeID) }
    func cancelLogin(challengeID: UUID) async { await base.cancelLogin(challengeID: challengeID) }
    func logout(platform: MusicAccountPlatform) async throws { await base.logout(platform: platform) }
    func sendSMS(platform: MusicAccountPlatform, countryCode: String, phone: String, attemptID: UUID) async throws -> MusicSMSSendReceipt {
        try await base.sendSMS(platform: platform, countryCode: countryCode, phone: phone, attemptID: attemptID)
    }
    func submitSMS(platform: MusicAccountPlatform, countryCode: String, phone: String, code: String, attemptID: UUID) async throws -> MusicAccountStatus {
        try await base.submitSMS(platform: platform, countryCode: countryCode, phone: phone, code: code, attemptID: attemptID)
    }
    func cancelSMSLogin(attemptID: UUID) async { await base.cancelSMSLogin(attemptID: attemptID) }
}

private actor MusicSettingsQRCheckGate {
    private let entered: @Sendable () -> Void
    private var released = false
    private var waiter: CheckedContinuation<Void, Never>?
    init(_ entered: @escaping @Sendable () -> Void) { self.entered = entered }
    func wait() async {
        entered()
        if !released { await withCheckedContinuation { waiter = $0 } }
    }
    func release() { released = true; waiter?.resume(); waiter = nil }
}

@MainActor
private final class MusicSettingsPlayerFixture: MusicPlaybackDriving {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        XCTFail("Unexpected playback"); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected pause") }
    func resume(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected resume") }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { XCTFail("Unexpected seek"); return false }
    func stop(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected stop") }
}

@MainActor
private final class MusicSettingsRouterFixture: MusicUIRouting {
    let primaryPlaybackPresentation = MusicMiniPlayerPresentation()
    func showSearch() { XCTFail("Unexpected navigation") }
    func showSettings() { XCTFail("Unexpected navigation") }
    func showPublicBrowse(_ request: MusicPublicBrowseRequest, title: String) { XCTFail("Unexpected navigation") }
    func showPublicCollection(_ collection: MusicPublicBrowseCollection) { XCTFail("Unexpected navigation") }
    func showLocalCollection(_ collection: MusicCollection) { XCTFail("Unexpected navigation") }
    func showPlayer(_ destination: MusicPlaybackDestination) { XCTFail("Unexpected navigation") }
    func selectChannel(_ channel: MusicPublicBrowseChannel) { XCTFail("Unexpected navigation") }
}
#endif
