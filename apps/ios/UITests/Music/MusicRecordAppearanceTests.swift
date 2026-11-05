import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature

@MainActor
final class MusicRecordAppearanceTests: XCTestCase {
    func testReferenceRecordAndTonearmRendering() async throws {
        for dark in [false, true] {
            for playing in [false, true] {
                let canvas = UIView(frame: CGRect(x: 0, y: 0, width: 390, height: 440))
                canvas.backgroundColor = UIColor(PageColor(rgb: dark ? 0x282033 : 0xe8dff3))
                let theme = PageTheme(palette: .lilac, dark: dark)
                let disc = MusicRecordDiscView(frame: CGRect(x: 35, y: 40, width: 320, height: 320))
                disc.configure(theme: theme, image: cover())
                canvas.addSubview(disc)
                let arm = MusicTonearmView()
                arm.configure(theme: theme)
                canvas.addSubview(arm)
                position(arm, relativeTo: disc.frame)
                UIView.performWithoutAnimation { arm.setPlaying(playing) }
                arm.layer.removeAllAnimations()
                let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
                let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: canvas.bounds)
                let root = UIViewController()
                window.rootViewController = root
                window.makeKeyAndVisible()
                root.view.addSubview(canvas)
                root.view.layoutIfNeeded()
                canvas.layoutIfNeeded()
                CATransaction.flush()
                let displayed = expectation(description: "component displayed")
                DispatchQueue.main.async { displayed.fulfill() }
                await fulfillment(of: [displayed], timeout: 2)
                let image = UIGraphicsImageRenderer(bounds: canvas.bounds).image { _ in
                    XCTAssertTrue(canvas.drawHierarchy(in: canvas.bounds, afterScreenUpdates: true))
                }
                window.isHidden = true
                window.rootViewController = nil
                let center = try pixel(image, x: 195, y: 200)
                XCTAssertEqual(center[0], 0x44, accuracy: 1)
                XCTAssertEqual(center[1], 0x66, accuracy: 1)
                XCTAssertEqual(center[2], 0x88, accuracy: 1)
                let attachment = XCTAttachment(image: image)
                attachment.name = "record-\(dark ? "dark" : "light")-\(playing ? "playing" : "paused")"
                attachment.lifetime = .keepAlways
                add(attachment)
            }
        }
    }

    func testCoverKeepsItsOriginalColorsAcrossRecordPalettesAndSizes() throws {
        let artwork = cover()
        for diameter: CGFloat in [190, 320, 400] {
            for palette in PageColorway.allCases {
                let disc = MusicRecordDiscView(frame: CGRect(x: 0, y: 0, width: diameter, height: diameter))
                disc.configure(theme: PageTheme(palette: palette), image: artwork)
                let image = render(disc)
                // Both samples lie inside the original 54.8% cover, including its outer half.
                for fraction: CGFloat in [0, 0.24] {
                    let color = try pixel(image, x: diameter * (0.5 + fraction), y: diameter / 2)
                    XCTAssertEqual(color[0], 0x44, accuracy: 1, palette.rawValue)
                    XCTAssertEqual(color[1], 0x66, accuracy: 1, palette.rawValue)
                    XCTAssertEqual(color[2], 0x88, accuracy: 1, palette.rawValue)
                }
            }
        }
    }

    func testTonearmKeepsReferencePivotAndNeedleLandingAcrossDiscSizes() {
        // Reference SVG: bottom midpoint (26.5,313), rotated 32° around (26,289).
        // Expected normalized landing points come from the reference browser geometry.
        let headAngle: CGFloat = 32 * .pi / 180
        let tip = CGPoint(x: 26 + 0.5 * cos(headAngle) - 24 * sin(headAngle),
                          y: 289 + 0.5 * sin(headAngle) + 24 * cos(headAngle))
        for diameter: CGFloat in [190, 320, 400] {
            let canvas = UIView(frame: CGRect(x: 0, y: 0, width: 600, height: 600))
            let disc = CGRect(x: (600 - diameter) / 2, y: 40, width: diameter, height: diameter)
            let arm = MusicTonearmView()
            canvas.addSubview(arm)
            position(arm, relativeTo: disc)
            let pivot = CGPoint(x: arm.bounds.width * 58 / 80, y: arm.bounds.height * 24 / 320)
            let fixedPivot = arm.convert(pivot, to: canvas)
            for playing in [false, true, false] {
                UIView.performWithoutAnimation { arm.setPlaying(playing) }
                arm.layer.removeAllAnimations()
                let actualPivot = arm.convert(pivot, to: canvas)
                XCTAssertEqual(actualPivot.x, fixedPivot.x, accuracy: 0.001)
                XCTAssertEqual(actualPivot.y, fixedPivot.y, accuracy: 0.001)
                let needle = arm.convert(CGPoint(x: tip.x * arm.bounds.width / 80,
                                                y: tip.y * arm.bounds.height / 320), to: canvas)
                XCTAssertEqual((needle.x - disc.midX) / (diameter / 2),
                               playing ? 0.5461 : 0.80295, accuracy: 0.0001)
                XCTAssertEqual((needle.y - disc.midY) / (diameter / 2),
                               playing ? 0.58233 : 0.6455, accuracy: 0.0001)
            }
        }
    }

    func testTransparentArtworkDoesNotReceivePlaceholderTint() throws {
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        format.opaque = false
        let artwork = UIGraphicsImageRenderer(size: CGSize(width: 32, height: 32), format: format).image {
            UIColor(PageColor(rgb: 0x446688)).withAlphaComponent(0.5).setFill()
            $0.fill(CGRect(x: 0, y: 0, width: 32, height: 32))
        }
        let canvas = UIView(frame: CGRect(x: 0, y: 0, width: 320, height: 320))
        canvas.backgroundColor = UIColor(PageColor(rgb: 0x282033))
        let disc = MusicRecordDiscView(frame: canvas.bounds)
        canvas.addSubview(disc)
        disc.configure(theme: PageTheme(), image: artwork)
        let center = try pixel(render(canvas), x: 160, y: 160)
        // CSS cover pixels composite directly over the page at the transparent disc center.
        for (actual, expected) in zip(center.prefix(3), [54.0, 67.0, 94.0]) {
            XCTAssertEqual(actual, expected, accuracy: 1)
        }
    }

    func testDefaultCoverMaterialAcrossPalettesAppearancesAndSizes() throws {
        XCTAssertEqual(PageColorway.allCases.count, 9)
        for diameter: CGFloat in [190, 320, 400] {
            let disc = MusicRecordDiscView(frame: CGRect(x: 0, y: 0, width: diameter, height: diameter))
            var lightBrightness: [PageColorway: Double] = [:]
            for dark in [false, true] {
                var colors: [[Double]] = []
                var screenshots: [UIImage] = []
                for palette in PageColorway.allCases {
                    try autoreleasepool {
                        let theme = PageTheme(palette: palette, dark: dark)
                        let context = "\(palette.rawValue), \(dark ? "dark" : "light"), \(diameter)pt"
                        disc.configure(theme: theme, image: nil)
                        let image = render(disc)
                        // Reference 320×320 cover: these opposite points are outside all grooves.
                        let upperLeft = try coverPixel(image, diameter: diameter, x: 54, y: 54)
                        let lowerRight = try coverPixel(image, diameter: diameter, x: 266, y: 266)
                        XCTAssertEqual(lowerRight[3], 255, accuracy: 1, context)
                        XCTAssertGreaterThan(brightness(upperLeft) - brightness(lowerRight), 15, context)

                        // Neutral reflections preserve the CSS theme surface's channel ordering.
                        let channels = [Double(theme.top.red), Double(theme.top.green), Double(theme.top.blue)]
                        for (a, b) in [(0, 1), (1, 2), (0, 2)] where abs(channels[a] - channels[b]) > 10 {
                            let direction = channels[a] > channels[b] ? 1.0 : -1.0
                            XCTAssertGreaterThan((lowerRight[a] - lowerRight[b]) * direction, 3, context)
                        }
                        let surfaceColors = Array(upperLeft.prefix(3)) + Array(lowerRight.prefix(3))
                        for previous in colors {
                            // Compare the combined color change, not a single channel:
                            // the reference silver/sky dark surfaces differ subtly in all channels.
                            let difference = zip(previous, surfaceColors).reduce(0.0) {
                                $0 + pow($1.0 - $1.1, 2)
                            }
                            XCTAssertGreaterThan(sqrt(difference), 3, "Distinct palette: \(context)")
                        }
                        colors.append(surfaceColors)
                        if dark {
                            XCTAssertLessThan(brightness(lowerRight),
                                              try XCTUnwrap(lightBrightness[palette]) - 25, context)
                        } else {
                            lightBrightness[palette] = brightness(lowerRight)
                        }

                        // Recorded bands have fine texture outside the small pressed hub.
                        // Do not require the rejected, uniform full-face target pattern.
                        for range: ClosedRange<CGFloat> in [52...73, 80...101, 108...126, 134...149] {
                            for angle: CGFloat in [0, .pi / 2, .pi] {
                                XCTAssertGreaterThanOrEqual(try grooveExtrema(image, diameter: diameter,
                                    range: range, angle: angle), 2, "Texture \(range), \(angle): \(context)")
                            }
                        }
                        let spindle = try coverPixel(image, diameter: diameter, x: 160, y: 160)
                        for point in [CGPoint(x: 152, y: 160), CGPoint(x: 168, y: 160),
                                      CGPoint(x: 160, y: 152), CGPoint(x: 160, y: 168)] {
                            let surround = try coverPixel(image, diameter: diameter, x: point.x, y: point.y)
                            // The pale material has less headroom to white; the spindle must
                            // still provide substantial local contrast, not merely a bright surface.
                            XCTAssertGreaterThan(brightness(spindle) - brightness(surround),
                                                 (255 - brightness(surround)) * 0.4, context)
                        }
                        if diameter == 320 { screenshots.append(image) }
                    }
                }
                if diameter == 320 { attachPaletteSheet(screenshots, dark: dark) }
            }
        }
    }

    func testReusedRecordRestoresDefaultCoverAndKeepsTransparentArtworkUntinted() throws {
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        format.opaque = false
        let transparentArtwork = UIGraphicsImageRenderer(size: CGSize(width: 32, height: 32), format: format).image {
            // Left is fully transparent, middle is translucent, right is opaque.
            UIColor(PageColor(rgb: 0x446688)).withAlphaComponent(0.5).setFill()
            $0.fill(CGRect(x: 10, y: 0, width: 12, height: 32))
            UIColor(PageColor(rgb: 0x446688)).setFill()
            $0.fill(CGRect(x: 22, y: 0, width: 10, height: 32))
        }
        for diameter: CGFloat in [190, 320, 400] {
            try autoreleasepool {
                let canvas = UIView(frame: CGRect(x: 0, y: 0, width: diameter, height: diameter))
                canvas.backgroundColor = UIColor(PageColor(rgb: 0x282033))
                let disc = MusicRecordDiscView(frame: canvas.bounds)
                canvas.addSubview(disc)
                let initialTheme = PageTheme(palette: .lilac)
                disc.configure(theme: initialTheme, image: nil)
                let initial = render(canvas)
                disc.configure(theme: initialTheme, image: cover())
                let opaque = render(canvas)
                for x: CGFloat in [35, 160, 285] {
                    let color = try coverPixel(opaque, diameter: diameter, x: x, y: 160)
                    for (actual, expected) in zip(color.prefix(3), [68.0, 102.0, 136.0]) {
                        XCTAssertEqual(actual, expected, accuracy: 1, "\(diameter)pt opaque artwork")
                    }
                }
                disc.configure(theme: initialTheme, image: nil)
                try assertSameCover(render(canvas), initial, diameter: diameter)

                // Reconfigure this same view immediately; no waiting or private layer inspection.
                for palette in PageColorway.allCases {
                    for dark in [false, true] {
                        try autoreleasepool {
                            disc.configure(theme: PageTheme(palette: palette, dark: dark), image: transparentArtwork)
                            let image = render(canvas)
                            let context = "\(palette.rawValue), \(dark ? "dark" : "light"), \(diameter)pt"
                            // Probe the cover edge, grooves and spindle, where a retained placeholder would show.
                            let samples: [(CGFloat, CGFloat, [Double])] = [
                                (6, 160, [40, 32, 51]), (35, 160, [40, 32, 51]),
                                (69, 160, [40, 32, 51]),
                                (160, 160, [54, 67, 94]), (160, 69, [54, 67, 94]),
                                (160, 285, [54, 67, 94]),
                                (251, 160, [68, 102, 136]), (285, 160, [68, 102, 136]),
                                (314, 160, [68, 102, 136])
                            ]
                            for (x, y, expected) in samples {
                                let actual = try coverPixel(image, diameter: diameter, x: x, y: y)
                                for (channel, value) in zip(actual.prefix(3), expected) {
                                    XCTAssertEqual(channel, value, accuracy: 1, context)
                                }
                            }
                        }
                    }
                }
                let updatedTheme = PageTheme(palette: .sky, dark: true)
                disc.configure(theme: updatedTheme, image: nil)
                let updated = render(canvas)
                let fresh = MusicRecordDiscView(frame: canvas.bounds)
                fresh.configure(theme: updatedTheme, image: nil)
                try assertSameCover(updated, render(fresh), diameter: diameter)
                disc.configure(theme: initialTheme, image: nil)
                try assertSameCover(render(canvas), initial, diameter: diameter)
            }
        }
    }

    // Map the reference SVG's 320×320 coordinates into the public 54.8% cover region.
    private func coverPixel(_ image: UIImage, diameter: CGFloat, x: CGFloat, y: CGFloat) throws -> [Double] {
        try pixel(image, x: diameter * (0.5 + (x - 160) * 0.548 / 320),
                  y: diameter * (0.5 + (y - 160) * 0.548 / 320))
    }

    private func brightness(_ color: [Double]) -> Double {
        color[0] * 0.2126 + color[1] * 0.7152 + color[2] * 0.0722
    }

    private func grooveExtrema(_ image: UIImage, diameter: CGFloat,
                               range: ClosedRange<CGFloat>, angle: CGFloat) throws -> Int {
        let values = try stride(from: range.lowerBound, through: range.upperBound, by: 1).map { radius in
            brightness(try coverPixel(image, diameter: diameter,
                x: 160 + radius * cos(angle), y: 160 + radius * sin(angle)))
        }
        // A smooth gradient has no repeated local peaks or troughs. Pale materials
        // show the etched dark edge most clearly; dark materials show the bright edge.
        var count = 0
        for index in 1..<(values.count - 1) {
            let surround = (values[index - 1] + values[index + 1]) / 2
            let peak = values[index] > values[index - 1] && values[index] >= values[index + 1]
            let trough = values[index] < values[index - 1] && values[index] <= values[index + 1]
            if (peak || trough), abs(values[index] - surround) > 3 {
                count += 1
            }
        }
        return count
    }

    private func assertSameCover(_ actual: UIImage, _ expected: UIImage, diameter: CGFloat,
                                 file: StaticString = #filePath, line: UInt = #line) throws {
        let points = [CGPoint(x: 54, y: 54), CGPoint(x: 266, y: 266), CGPoint(x: 160, y: 160)]
            + [91, 100, 105, 110, 115, 120, 125, 130, 135, 140, 145].map {
                CGPoint(x: 160 + CGFloat($0), y: 160)
            }
        for point in points {
            let a = try coverPixel(actual, diameter: diameter, x: point.x, y: point.y)
            let b = try coverPixel(expected, diameter: diameter, x: point.x, y: point.y)
            for (channel, value) in zip(a, b) {
                XCTAssertEqual(channel, value, accuracy: 1, "\(diameter)pt at \(point)", file: file, line: line)
            }
        }
    }

    private func attachPaletteSheet(_ images: [UIImage], dark: Bool) {
        let format = UIGraphicsImageRendererFormat()
        format.scale = 2
        format.preferredRange = .standard
        let sheet = UIGraphicsImageRenderer(size: CGSize(width: 960, height: 1032), format: format).image { context in
            UIColor(white: dark ? 0.1 : 0.95, alpha: 1).setFill()
            context.fill(CGRect(x: 0, y: 0, width: 960, height: 1032))
            for (index, image) in images.enumerated() {
                let origin = CGPoint(x: (index % 3) * 320, y: (index / 3) * 344)
                UIColor(PageTheme(palette: PageColorway.allCases[index], dark: dark).top).setFill()
                context.fill(CGRect(x: origin.x, y: origin.y, width: 320, height: 344))
                image.draw(in: CGRect(x: origin.x, y: origin.y + 24, width: 320, height: 320))
                (PageColorway.allCases[index].rawValue as NSString).draw(
                    at: CGPoint(x: origin.x + 8, y: origin.y + 3),
                    withAttributes: [.font: UIFont.systemFont(ofSize: 14),
                                     .foregroundColor: dark ? UIColor.white : UIColor.black])
            }
        }
        let attachment = XCTAttachment(image: sheet)
        attachment.name = "default-cover-\(dark ? "dark" : "light")-nine-palettes-320pt"
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    private func position(_ arm: MusicTonearmView, relativeTo disc: CGRect) {
        arm.bounds = CGRect(x: 0, y: 0, width: disc.width * 0.21, height: disc.height * 0.84)
        arm.center = CGPoint(x: disc.midX + disc.width * 0.3655 + arm.bounds.width * 0.725,
                             y: disc.minY + disc.height * 0.01 + arm.bounds.height * 0.075)
    }

    private func cover() -> UIImage {
        UIGraphicsImageRenderer(size: CGSize(width: 32, height: 32)).image { context in
            UIColor(PageColor(rgb: 0x446688)).setFill()
            context.fill(CGRect(x: 0, y: 0, width: 32, height: 32))
        }
    }

    private func render(_ view: UIView) -> UIImage {
        view.layoutIfNeeded()
        func display(_ layer: CALayer) {
            layer.displayIfNeeded()
            layer.sublayers?.forEach(display)
        }
        display(view.layer)
        let format = UIGraphicsImageRendererFormat()
        format.scale = 3
        format.preferredRange = .standard
        return UIGraphicsImageRenderer(bounds: view.bounds, format: format).image {
            view.layer.render(in: $0.cgContext)
        }
    }

    private func pixel(_ image: UIImage, x: CGFloat, y: CGFloat) throws -> [Double] {
        let cgImage = try XCTUnwrap(image.cgImage)
        let rect = CGRect(x: (x * image.scale).rounded(.down), y: (y * image.scale).rounded(.down),
                          width: 1, height: 1)
        let cropped = try XCTUnwrap(cgImage.cropping(to: rect))
        var bytes = [UInt8](repeating: 0, count: 4)
        try bytes.withUnsafeMutableBytes { buffer in
            let context = try XCTUnwrap(CGContext(data: buffer.baseAddress, width: 1, height: 1,
                bitsPerComponent: 8, bytesPerRow: 4, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            context.draw(cropped, in: CGRect(x: 0, y: 0, width: 1, height: 1))
        }
        return bytes.map(Double.init)
    }
}
