import XCTest
import MeowContracts

final class PlaybackVideoInputTests: XCTestCase {
    func testVideoInputAcceptsLocalAndRemoteURLsWithoutOriginOrResourcePathAdmission() throws {
        for value in [
            "http://127.0.0.1:12345/media/issued/manifest",
            "http://127.0.0.1:12346/media/issued/manifest", "http://127.0.0.1:12345/media/other/manifest",
            "http://127.0.0.1:12345/media/issued-extended/manifest",
            "http://127.0.0.1:12345/media/issued/../other", "http://127.0.0.1:12345/media/issued/%2e%2e/other",
            "http://127.0.0.1:12345/media/issued/manifest?url=fixture",
            "http://localhost:19191/video.mp4", "http://[::1]:19191/video.mp4",
            "http://192.168.1.2:19191/video.mp4", "https://media.invalid:8443/video.mp4",
            "https://user:synthetic@media.invalid/video.mp4?token=fixture#chapter",
            "file:///tmp/synthetic-video.mp4",
        ] {
            let url = try XCTUnwrap(URL(string: value))
            let input = try PlaybackVideoInput(resourceID: "receipt/id:+opaque", url: url,
                title: "test", format: .hls, startPosition: 7)
            XCTAssertEqual(input.url, url)
            XCTAssertEqual(input.resourceID, "receipt/id:+opaque")
            XCTAssertEqual(input.format, .hls)
            XCTAssertEqual(input.startPosition, 7)
            XCTAssertTrue(input.requestContext.headers.isEmpty)
            XCTAssertNil(input.requestContext.cookie)
        }
    }

    func testVideoInputRetainsResourceIdentityPositionAndSupportedProtocolRequirements() throws {
        let url = try XCTUnwrap(URL(string: "http://localhost:19191/video.mp4"))
        XCTAssertThrowsError(try PlaybackVideoInput(resourceID: "", url: url, title: "test"))
        for position in [-1, Double.infinity, Double.nan] {
            XCTAssertThrowsError(try PlaybackVideoInput(resourceID: "receipt", url: url, title: "test",
                startPosition: position))
        }
        for value in ["/relative.mp4", "ftp://media.invalid/video.mp4", "https:/missing-host.mp4"] {
            XCTAssertThrowsError(try PlaybackVideoInput(resourceID: "receipt",
                url: XCTUnwrap(URL(string: value)), title: "test"))
        }
    }

    func testEmptyRequestContextDoesNotAddHeaders() throws {
        XCTAssertTrue(PlaybackVideoRequestContext.empty.headers.isEmpty)
        XCTAssertNil(PlaybackVideoRequestContext.empty.cookie)
        let context = try PlaybackVideoRequestContext()
        XCTAssertTrue(context.headers.isEmpty)
        XCTAssertNil(context.cookie)
    }

    func testRequestContextNormalizesHeaderNamesAndPreservesValues() throws {
        let authorization = "Bearer \(UUID().uuidString)"
        let context = try PlaybackVideoRequestContext(headers: [
            "Referer": "https://referer.invalid/path?case=UPPER",
            "USER-AGENT": "FixturePlayer/1.0", "Authorization": authorization,
            "X-Media+Context": "\t媒体 fixture \t", "X-Empty": ""
        ])
        XCTAssertEqual(context.headers, [
            "referer": "https://referer.invalid/path?case=UPPER",
            "user-agent": "FixturePlayer/1.0", "authorization": authorization,
            "x-media+context": "\t媒体 fixture \t", "x-empty": ""
        ])
        XCTAssertNil(context.cookie)
    }

    func testRequestContextAcceptsCookieFromHeaderOrArgument() throws {
        let cookie = "fixture=\(UUID().uuidString); other=fixture"
        for context in [
            try PlaybackVideoRequestContext(headers: ["cOoKiE": cookie]),
            try PlaybackVideoRequestContext(cookie: cookie)
        ] {
            XCTAssertEqual(context.headers, ["cookie": cookie])
            XCTAssertEqual(context.cookie, cookie)
        }
        XCTAssertEqual(try PlaybackVideoRequestContext(cookie: "").cookie, "")
    }

    func testRequestContextRejectsCaseInsensitiveDuplicatesAndCookieConflicts() throws {
        for headers in [
            ["Referer": "one", "referer": "two"],
            ["User-Agent": "same", "USER-AGENT": "same"],
            ["Cookie": "fixture=one", "cookie": "fixture=two"]
        ] {
            XCTAssertThrowsError(try PlaybackVideoRequestContext(headers: headers)) { error in
                XCTAssertEqual(error as? PlaybackVideoRequestContextError, .invalidHeaders)
            }
        }
        for name in ["Cookie", "cookie", "COOKIE"] {
            XCTAssertThrowsError(try PlaybackVideoRequestContext(headers: [name: "fixture=same"],
                cookie: "fixture=same")) { error in
                XCTAssertEqual(error as? PlaybackVideoRequestContextError, .invalidHeaders)
            }
        }
    }

    func testRequestContextRejectsHeaderNameInjection() throws {
        for name in ["", "Bad Name", "X:Injected", "X\tName", "X\rName", "X\nName", "X-Ünicode", "Key"] {
            XCTAssertThrowsError(try PlaybackVideoRequestContext(headers: [name: "fixture"])) { error in
                XCTAssertEqual(error as? PlaybackVideoRequestContextError, .invalidHeaders)
            }
        }
    }

    func testRequestContextRejectsControlCharacterInjectionWithoutDisclosingValues() throws {
        let secret = UUID().uuidString
        for suffix in ["\r\nInjected: value", "\rvalue", "\nvalue", "\0value", "\u{01}value", "\u{7F}value"] {
            let value = secret + suffix
            XCTAssertThrowsError(try PlaybackVideoRequestContext(headers: ["Authorization": value])) { error in
                XCTAssertEqual(error as? PlaybackVideoRequestContextError, .invalidHeaders)
                var dumped = ""
                dump(error, to: &dumped)
                let text = String(describing: error) + String(reflecting: error) + error.localizedDescription + dumped
                XCTAssertFalse(text.contains(secret))
            }
            XCTAssertThrowsError(try PlaybackVideoRequestContext(cookie: value)) { error in
                XCTAssertEqual(error as? PlaybackVideoRequestContextError, .invalidHeaders)
            }
        }
    }

    func testVideoRequestContextsRemainIsolatedFromOtherMediaAndCallerMutation() throws {
        let url = try XCTUnwrap(URL(string: "https://media.invalid/video.mp4"))
        let authorization = "Bearer \(UUID().uuidString)"
        let firstCookie = "fixture=\(UUID().uuidString)"
        let secondCookie = "fixture=\(UUID().uuidString)"
        var headers = ["Authorization": authorization]
        let context = try PlaybackVideoRequestContext(headers: headers, cookie: firstCookie)
        headers["Authorization"] = "changed"
        let first = try PlaybackVideoInput(resourceID: "first", url: url, title: "first", requestContext: context)
        let second = try PlaybackVideoInput(resourceID: "second", url: url, title: "second",
            requestContext: PlaybackVideoRequestContext(cookie: secondCookie))
        let receipt = try PlaybackVideoInput(resourceID: "runtime-receipt", url: url, title: "receipt")
        XCTAssertEqual(first.requestContext.headers, ["authorization": authorization, "cookie": firstCookie])
        XCTAssertEqual(second.requestContext.headers, ["cookie": secondCookie])
        XCTAssertTrue(receipt.requestContext.headers.isEmpty)
        XCTAssertNil(receipt.requestContext.cookie)
    }

    func testVideoInputsAndKeysDoNotDisclosePrivateDataInDescriptionsOrMirror() throws {
        let kid = Data((0..<16).map(UInt8.init))
        let key = Data((16..<32).map(UInt8.init))
        let keys = try PlaybackVideoDecryption(clearKeys: [
            kid.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_"):
            key.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
        ])
        let secret = "private-\(UUID().uuidString)"
        let context = try PlaybackVideoRequestContext(headers: [
            "Authorization": "Bearer \(secret)", "Referer": "https://referer.invalid/\(secret)",
            "User-Agent": secret, "X-\(secret)": secret
        ], cookie: "fixture=\(secret)")
        let input = try PlaybackVideoInput(resourceID: "private-resource",
            url: XCTUnwrap(URL(string: "https://media.invalid/video?token=private-test-only")), title: "private-title",
            decryption: keys, requestContext: context)
        var dumped = ""
        dump(input, to: &dumped); dump(keys, to: &dumped); dump(context, to: &dumped)
        let text = String(describing: input) + String(reflecting: input) + String(describing: keys) + String(reflecting: keys)
            + String(describing: context) + String(reflecting: context) + dumped
        XCTAssertFalse(text.contains("private")); XCTAssertFalse(text.contains("10111213"))
        XCTAssertEqual(Mirror(reflecting: input).children.count, 1)
        XCTAssertEqual(Mirror(reflecting: keys).children.count, 0)
        XCTAssertEqual(Mirror(reflecting: context).children.count, 0)
        keys.withKeyMaterial { XCTAssertEqual(Array($0.values), [key]) }
        XCTAssertThrowsError(try PlaybackVideoDecryption(clearKeys: ["wrong-size": "not-a-key"]))
    }
}
