#if os(iOS) || os(macOS)
import Foundation
import XCTest
@testable import MeowMusicFeature

final class MusicAccountSettingsSummaryTests: XCTestCase {
    private let platforms: [MusicAccountPlatform] = [.netease, .qq, .kugou]
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    func testFixedPlatformsKeepIdentityAndSignedOutHidesStaleAccountData() {
        XCTAssertEqual(MusicAccountPlatform.allCases, platforms)
        let memberships: [MusicAccountMembership] = [
            .init(vip: true, tier: "合成等级", expiresAt: now.addingTimeInterval(86_400)),
            .init(vip: false, tier: "合成等级", expiresAt: now.addingTimeInterval(86_400))
        ]
        for platform in platforms {
            for membership in memberships {
                let summary = makeSummary(platform: platform, phase: .signedOut, nickname: "旧账号昵称",
                                          profile: .init(nickname: "旧资料昵称", membership: membership))
                XCTAssertEqual(summary.id, platform)
                XCTAssertEqual(summary.detail(at: now),
                               .init(name: nil, status: "未登录", isNonVIP: false), platform.rawValue)
            }
        }
    }

    func testNonSignedInPhasesPreserveActualStatusWithoutStaleAccountData() {
        let phases: [(MusicAccountStatus.Phase, String)] = [
            (.signingIn, "登录中"),
            (.requiresValidation, "待验证"),
            (.unavailable, "不可用"),
            (.removalPending, "退出未完成")
        ]
        let profile = MusicAccountProfile(nickname: "旧资料昵称",
                                          membership: .init(vip: true, expiresAt: now.addingTimeInterval(86_400)))
        for platform in platforms {
            for (phase, status) in phases {
                let detail = makeSummary(platform: platform, phase: phase, nickname: "旧账号昵称",
                                         profile: profile).detail(at: now)
                XCTAssertEqual(detail, .init(name: nil, status: status, isNonVIP: false),
                               "\(platform.rawValue), \(phase.rawValue)")
            }
        }
    }

    func testSignedInNicknameIsTrimmedAndFallsBackToProfileThenSignedInTitle() {
        let cases: [(nickname: String, profile: MusicAccountProfile?, name: String)] = [
            (" \n账号昵称\t ", .init(nickname: "资料昵称"), "账号昵称"),
            ("", .init(nickname: " \n资料昵称\t "), "资料昵称"),
            (" \n\t ", .init(nickname: " \n资料昵称\t "), "资料昵称"),
            ("", .init(nickname: ""), "已登录"),
            (" \n\t ", .init(nickname: " \n\t "), "已登录"),
            ("", nil, "已登录"),
            (" \n\t ", nil, "已登录")
        ]
        for platform in platforms {
            for value in cases {
                let detail = makeSummary(platform: platform, nickname: value.nickname,
                                         profile: value.profile).detail(at: now)
                XCTAssertEqual(detail, .init(name: value.name, status: "会员状态未知", isNonVIP: false),
                               "\(platform.rawValue), nickname: \(String(reflecting: value.nickname))")
            }
        }
    }

    func testValidVIPShowsLocalExpiryDateWithSuffix() throws {
        let expiresAt = now.addingTimeInterval(56_700)
        let status = try localExpiryStatus(expiresAt)
        for platform in platforms {
            let detail = makeSummary(platform: platform,
                                     profile: .init(membership: .init(vip: true, tier: "合成等级",
                                                                     expiresAt: expiresAt))).detail(at: now)
            XCTAssertEqual(detail, .init(name: "合成账号", status: status, isNonVIP: false),
                           platform.rawValue)
        }
    }

    func testExplicitFalseVIPIsNonVIPRegardlessOfExpiryOrTier() {
        let expirations: [Date?] = [nil, now.addingTimeInterval(-86_400), now, now.addingTimeInterval(86_400)]
        for platform in platforms {
            for expiresAt in expirations {
                let detail = makeSummary(platform: platform,
                                         profile: .init(membership: .init(vip: false, tier: "VIP",
                                                                         expiresAt: expiresAt))).detail(at: now)
                XCTAssertEqual(detail, .init(name: "合成账号", status: "无VIP", isNonVIP: true),
                               "\(platform.rawValue), expiresAt: \(String(describing: expiresAt))")
            }
        }
    }

    func testVIPBecomesNonVIPAtAndAfterExactExpiryUsingProvidedClock() throws {
        let expiresAt = now
        let validStatus = try localExpiryStatus(expiresAt)
        for platform in platforms {
            let summary = makeSummary(platform: platform,
                                      profile: .init(membership: .init(vip: true, expiresAt: expiresAt)))
            XCTAssertEqual(summary.detail(at: expiresAt.addingTimeInterval(-1)),
                           .init(name: "合成账号", status: validStatus, isNonVIP: false), platform.rawValue)
            for date in [expiresAt, expiresAt.addingTimeInterval(1), expiresAt.addingTimeInterval(86_400)] {
                XCTAssertEqual(summary.detail(at: date),
                               .init(name: "合成账号", status: "无VIP", isNonVIP: true),
                               "\(platform.rawValue), clock: \(date.timeIntervalSince1970)")
            }
        }
    }

    func testUnknownVIPRemainsUnknownEvenWithExpiredDeadlineOrVIPTier() {
        let expirations: [Date?] = [nil, now.addingTimeInterval(-86_400), now, now.addingTimeInterval(86_400)]
        for platform in platforms {
            for expiresAt in expirations {
                let detail = makeSummary(platform: platform,
                                         profile: .init(membership: .init(vip: nil, tier: "VIP",
                                                                         expiresAt: expiresAt))).detail(at: now)
                XCTAssertEqual(detail, .init(name: "合成账号", status: "会员状态未知", isNonVIP: false),
                               "\(platform.rawValue), expiresAt: \(String(describing: expiresAt))")
            }
        }
    }

    func testMissingProfileKeepsMembershipUnknownAndAccountNickname() {
        for platform in platforms {
            let detail = makeSummary(platform: platform, nickname: " \n合成账号\t ", profile: nil).detail(at: now)
            XCTAssertEqual(detail, .init(name: "合成账号", status: "会员状态未知", isNonVIP: false),
                           platform.rawValue)
        }
    }

    func testVIPWithoutDeadlineReportsUnknownExpiry() {
        for platform in platforms {
            let detail = makeSummary(platform: platform,
                                     profile: .init(membership: .init(vip: true, tier: "合成等级"))).detail(at: now)
            XCTAssertEqual(detail, .init(name: "合成账号", status: "VIP · 到期时间未知", isNonVIP: false),
                           platform.rawValue)
        }
    }

    func testDetailMembershipSeparatesTierExpiryUnknownAndNonVIP() throws {
        let future = now.addingTimeInterval(86_400 * 30)
        let expiry = try localExpiryStatus(future).replacingOccurrences(of: " 到期", with: "")
        let cases: [(MusicAccountProfile?, MusicAccountSettingsMembership)] = [
            (.init(membership: .init(vip: true, tier: " 合成等级 ", expiresAt: future)),
             .init(title: "合成等级", tone: .accent, expiry: expiry)),
            (.init(membership: .init(vip: true, expiresAt: future)), .init(title: "VIP", tone: .accent, expiry: expiry)),
            (.init(membership: .init(vip: true, tier: "合成等级")),
             .init(title: "合成等级", tone: .accent, expiry: "—")),
            (.init(membership: .init(vip: false, tier: "合成等级", expiresAt: future)),
             .init(title: "无VIP", tone: .danger, expiry: "—")),
            (.init(membership: .init(vip: true, tier: "合成等级", expiresAt: now)),
             .init(title: "无VIP", tone: .danger, expiry: "—")),
            (.init(membership: .init(tier: "合成等级", expiresAt: future)),
             .init(title: "会员状态未知", tone: .muted, expiry: "—")),
            (nil, .init(title: "会员状态未知", tone: .muted, expiry: "—"))
        ]
        for platform in platforms {
            for (profile, expected) in cases {
                XCTAssertEqual(makeSummary(platform: platform, profile: profile).membership(at: now), expected,
                               platform.rawValue)
            }
        }
    }

    private func makeSummary(platform: MusicAccountPlatform, phase: MusicAccountStatus.Phase = .signedIn,
                             nickname: String = "合成账号", profile: MusicAccountProfile? = nil) -> MusicAccountSettingsSummary {
        .init(account: .init(platform: platform, phase: phase, nickname: nickname, profile: profile))
    }

    private func localExpiryStatus(_ date: Date) throws -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .current
        let components = calendar.dateComponents([.year, .month, .day], from: date)
        let year = try XCTUnwrap(components.year)
        let month = try XCTUnwrap(components.month)
        let day = try XCTUnwrap(components.day)
        return String(format: "%04d-%02d-%02d 到期", year, month, day)
    }
}
#endif
