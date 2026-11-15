// Fixed algorithm/fixtures: read-only iWebPlus b900c259 song-palette.js and
// song-palette.test.cjs. These test production Swift, never a Node substitute.
import XCTest
import MeowUI
@testable import MeowMusicFeature
#if canImport(CoreGraphics)
import CoreGraphics
#endif

final class MusicAppearancePaletteTests: XCTestCase {
    private func pixels(_ groups: ([UInt8], Int)...) -> [UInt8] {
        groups.flatMap { color, count in Array(repeating: color, count: count).flatMap { $0 } }
    }

    func testPermanentSongIdentityIsIndependentOfOccurrenceMetadataAndAddedSources() {
        let track = MusicRemoteTrack(ref: .init(kind: .queue, id: "occurrence-a"), songId: "domain-id", title: "Title",
                                    sources: [.init(sourceKey: "source", platform: "wy", nativeId: "999")])
        let enriched = MusicRemoteTrack(ref: .init(kind: .queue, id: "occurrence-b"), songId: "domain-id",
                                       title: "Enriched (Live)", artist: "当前歌手", album: "当前专辑",
                                       duration: 181.5, sources: track.sources,
                                       artwork: URL(string: "https://artwork.invalid/current"))
        XCTAssertEqual(MusicAppearanceSongIdentity(track: track), MusicAppearanceSongIdentity(songID: "domain-id"))
        XCTAssertEqual(MusicAppearanceSongIdentity(track: track), MusicAppearanceSongIdentity(track: enriched))
        let fused = MusicRemoteTrack(ref: track.ref, songId: "domain-id", title: "Title",
                                    sources: track.sources + [.init(sourceKey: "other", platform: "tx", nativeId: "123")])
        XCTAssertEqual(MusicAppearanceSongIdentity(track: fused).stableKey, "domain-id")
        XCTAssertEqual(MusicAppearanceSongIdentity(track: track), MusicAppearanceSongIdentity(track: fused))
        let independent = MusicRemoteTrack(ref: .init(kind: .library, id: "other-song"), songId: "other-song",
                                          title: track.title, sources: track.sources)
        XCTAssertNotEqual(MusicAppearanceSongIdentity(track: track), MusicAppearanceSongIdentity(track: independent))
    }

    func testFallbackUsesUTF16AndDoesNotFoldUnicodeOrOccurrence() {
        XCTAssertEqual(MusicSongPalette.fallback(for: ""), .lilac)
        // Independent FNV-1a code-unit fixtures; emoji is two UTF-16 units.
        XCTAssertEqual(MusicSongPalette.fallback(for: "wy:999"), .lilac)
        XCTAssertEqual(MusicSongPalette.fallback(for: "wy:\u{1f600}"), .mint)
        XCTAssertEqual(MusicSongPalette.fallback(for: "\u{e9}"), .coral)
        XCTAssertEqual(MusicSongPalette.fallback(for: "e\u{301}"), .silver)
        XCTAssertNotEqual(MusicAppearanceSongIdentity(songID: "\u{e9}"),
                          MusicAppearanceSongIdentity(songID: "e\u{301}"))
        let first = MusicAppearanceSongIdentity(songID: "permanent-song")
        let second = MusicAppearanceSongIdentity(track: .init(ref: .init(kind: .queue, id: "another-occurrence"),
                                                             songId: "permanent-song", title: "enriched"))
        XCTAssertEqual(MusicSongPalette.fallback(for: first.stableKey), MusicSongPalette.fallback(for: second.stableKey))
    }

    func testExtractionMatchesNoiseThresholdsAndDominantHue() {
        XCTAssertEqual(MusicSongPalette.fromPixels(pixels(([240, 45, 55, 255], 100), ([255, 255, 255, 255], 400), ([0, 0, 255, 0], 500))), .coral)
        XCTAssertEqual(MusicSongPalette.fromPixels(pixels(([40, 220, 120, 255], 200), ([255, 0, 0, 255], 3))), .mint)
        XCTAssertEqual(MusicSongPalette.fromPixels(pixels(([120, 120, 120, 255], 100))), .silver)
        XCTAssertNil(MusicSongPalette.fromPixels(pixels(([0, 0, 0, 0], 100))))
        XCTAssertNil(MusicSongPalette.fromPixels([]))
        XCTAssertNil(MusicSongPalette.fromPixels([240, 45, 55, 179]))
        XCTAssertEqual(MusicSongPalette.fromPixels([240, 45, 55, 180]), .coral)
        XCTAssertEqual(MusicSongPalette.fromPixels([240, 45, 55, 255, 0]), .coral, "Ignore incomplete trailing pixels")
    }

    func testAllNineReferenceLightColorsAndMaterialAlphaArePreserved() {
        let top: [UInt32] = [0xd7d1f7, 0xefcfd0, 0xe1fced, 0xdad8d9, 0xfcf7d7, 0xf0d0e7, 0xdae1fb, 0xf6e7d4, 0xd7e8fa]
        let accent: [UInt32] = [0x7864b5, 0x99535f, 0x377860, 0x646976, 0x806727, 0x995d88, 0x5d6fa8, 0x926840, 0x4b739b]
        XCTAssertEqual(PageColorway.allCases.count, 9)
        for (index, palette) in PageColorway.allCases.enumerated() {
            let tokens = PageTheme(palette: palette)
            let materials = MusicRecordMaterials(palette: palette)
            XCTAssertEqual(tokens.top.rgb, top[index])
            XCTAssertEqual(tokens.accent.rgb, accent[index])
            XCTAssertEqual(tokens.bottom.rgb, 0xf6fbfe)
            XCTAssertLessThan(materials.ring.alpha, 255)
            XCTAssertLessThan(materials.tone.alpha, 255)
            XCTAssertLessThan(materials.mist.alpha, 255)
            XCTAssertLessThan(materials.reflect.alpha, 255)
            XCTAssertEqual(materials.discRotation, palette.hue - 262)
        }
        let dark = PageTheme(dark: true)
        XCTAssertEqual(dark.top.rgb, 0x2b2239)
        XCTAssertEqual(dark.bottom.rgb, 0x1b1622)
        XCTAssertEqual(dark.ink.rgb, 0xf3f1f8)
        XCTAssertEqual(MusicRecordMaterials().ring.rgba, 0x89749c8c)
    }

    func testMaterialMixingIncludesAlphaAndRoundsLikeReference() {
        XCTAssertEqual(PageColor(rgb: 0).mixed(with: .init(rgb: 0xffffff), ratio: 0.5).rgba, 0x808080ff)
        XCTAssertEqual(PageColor(rgba: 0).mixed(with: .init(rgba: 0xffffffff), ratio: 0.5).rgba, 0x80808080)
        for palette in PageColorway.allCases {
            let start = PageTheme(), end = PageTheme(palette: palette)
            let first = MusicRecordMaterials(), last = MusicRecordMaterials(palette: palette)
            for (a, b) in zip([start.top, start.bottom, start.accent, first.ring, first.tone, first.mist, first.reflect],
                              [end.top, end.bottom, end.accent, last.ring, last.tone, last.mist, last.reflect]) {
                XCTAssertEqual(a.mixed(with: b, ratio: 0), a)
                XCTAssertEqual(a.mixed(with: b, ratio: 1), b)
            }
        }
    }



    #if canImport(CoreGraphics)
    func testNativeSamplerProducesBoundedUnpremultipliedSRGBPixels() throws {
        let provider = try XCTUnwrap(CGDataProvider(data: Data([40, 220, 120, 200]) as CFData))
        let colorSpace = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let image = try XCTUnwrap(CGImage(width: 1, height: 1, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: 4,
                                         space: colorSpace,
                                         bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue),
                                         provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent))
        let rgba = try XCTUnwrap(MusicCoverPaletteSampler.rgba(image))
        XCTAssertEqual(rgba.count, 48 * 48 * 4)
        XCTAssertEqual(Double(rgba[0]), 40, accuracy: 2)
        XCTAssertEqual(Double(rgba[1]), 220, accuracy: 2)
        XCTAssertEqual(Double(rgba[2]), 120, accuracy: 2)
        XCTAssertEqual(rgba[3], 200)
        XCTAssertEqual(MusicSongPalette.fromPixels(rgba), .mint)
    }
    #endif
}
