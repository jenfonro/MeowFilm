// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus-derived heart/FM artwork retains Apache-2.0 attribution; see LICENSES/NOTICE.md.
#if os(iOS)
import UIKit

@MainActor
enum MusicPlaybackModeIcon {
    enum Kind: CaseIterable {
        case order, loop, single, shuffle, heartbeat, fm
    }

    static func image(_ kind: Kind) -> UIImage {
        switch kind {
        case .order: images.order
        case .loop: images.loop
        case .single: images.single
        case .shuffle: images.shuffle
        case .heartbeat: images.heartbeat
        case .fm: images.fm
        }
    }

    private static let images = (
        order: render(.order),
        loop: render(.loop),
        single: render(.single),
        shuffle: render(.shuffle),
        heartbeat: render(.heartbeat),
        fm: render(.fm)
    )

    private static func render(_ kind: Kind) -> UIImage {
        UIGraphicsImageRenderer(size: CGSize(width: 24, height: 24)).image { renderer in
            let context = renderer.cgContext
            context.setFillColor(UIColor.black.cgColor)
            context.setStrokeColor(UIColor.black.cgColor)
            context.setLineWidth(1.6)
            context.setLineCap(.round)
            context.setLineJoin(.round)
            draw(kind, in: context)
        }.withRenderingMode(.alwaysTemplate)
    }

    // Coordinates and stroke styles match MusicPlaybackIcon.cs's six 24-unit icons.
    private static func draw(_ kind: Kind, in context: CGContext) {
        switch kind {
        case .order:
            context.move(to: .init(x: 2, y: 6))
            context.addLine(to: .init(x: 20, y: 6))
            context.move(to: .init(x: 2, y: 18))
            context.addLine(to: .init(x: 20, y: 18))
            context.strokePath()

            context.move(to: .init(x: 18, y: 3))
            context.addLine(to: .init(x: 22, y: 6))
            context.addLine(to: .init(x: 18, y: 9))
            context.closePath()
            context.move(to: .init(x: 18, y: 15))
            context.addLine(to: .init(x: 22, y: 18))
            context.addLine(to: .init(x: 18, y: 21))
            context.closePath()
            context.fillPath()

        case .loop, .single:
            context.move(to: .init(x: 22, y: 13))
            context.addCurve(to: .init(x: 12, y: 21),
                             control1: .init(x: 22, y: 17.42), control2: .init(x: 17.52, y: 21))
            context.addCurve(to: .init(x: 2, y: 13),
                             control1: .init(x: 6.48, y: 21), control2: .init(x: 2, y: 17.42))
            context.addCurve(to: .init(x: 12, y: 5),
                             control1: .init(x: 2, y: 8.58), control2: .init(x: 6.48, y: 5))
            context.addLine(to: .init(x: 20, y: 5))
            context.strokePath()

            context.move(to: .init(x: 18.8, y: 1.9))
            context.addLine(to: .init(x: 22.5, y: 5))
            context.addLine(to: .init(x: 18.8, y: 8.1))
            context.closePath()
            context.fillPath()

            if kind == .single {
                // The reference uses system-bold text, centered at x=12 with baseline y=17.
                let text = "1" as NSString
                let font = UIFont.systemFont(ofSize: 10, weight: .bold)
                let attributes: [NSAttributedString.Key: Any] = [
                    .font: font, .foregroundColor: UIColor.black
                ]
                text.draw(at: .init(x: 12 - text.size(withAttributes: attributes).width / 2,
                                    y: 17 - font.ascender), withAttributes: attributes)
            }

        case .shuffle:
            context.move(to: .init(x: 2, y: 20))
            context.addLine(to: .init(x: 4, y: 20))
            context.addCurve(to: .init(x: 20, y: 4),
                             control1: .init(x: 12, y: 20), control2: .init(x: 12, y: 4))
            context.move(to: .init(x: 2, y: 4))
            context.addLine(to: .init(x: 4, y: 4))
            context.addCurve(to: .init(x: 9.8, y: 8.4),
                             control1: .init(x: 7, y: 4), control2: .init(x: 8.4, y: 6))
            context.move(to: .init(x: 14.2, y: 15.6))
            context.addCurve(to: .init(x: 20, y: 20),
                             control1: .init(x: 15.6, y: 18), control2: .init(x: 17, y: 20))
            context.strokePath()

            context.move(to: .init(x: 18, y: 1))
            context.addLine(to: .init(x: 22, y: 4))
            context.addLine(to: .init(x: 18, y: 7))
            context.closePath()
            context.move(to: .init(x: 18, y: 17))
            context.addLine(to: .init(x: 22, y: 20))
            context.addLine(to: .init(x: 18, y: 23))
            context.closePath()
            context.fillPath()

        case .heartbeat:
            context.setLineWidth(1.65)
            context.move(to: .init(x: 21, y: 5))
            // A5.5 5.5 0 0 0 13 4: center=(17-sqrt(14/65),4.5+8*sqrt(14/65)).
            // SVG sweep=0 maps to decreasing angles in UIKit's downward-y coordinates.
            context.addArc(center: .init(x: 16.53590451910774, y: 8.212763847138056),
                           radius: 5.5, startAngle: -0.6238187087603091,
                           endAngle: -2.269063955735961, clockwise: true)
            context.addLine(to: .init(x: 12, y: 5))
            context.addLine(to: .init(x: 11, y: 4))
            // A5.5 5.5 0 0 0 3 12 needs SVG radii correction: r²=32, center=(7,8).
            context.addArc(center: .init(x: 7, y: 8), radius: 5.656854249492381,
                           startAngle: -.pi / 4, endAngle: -5 * .pi / 4, clockwise: true)
            context.addLine(to: .init(x: 12, y: 21))
            context.addLine(to: .init(x: 21, y: 12))
            // A5.5 5.5 0 0 0 21 5: center=(21-sqrt(18),8.5), negative sweep.
            context.addArc(center: .init(x: 16.75735931288072, y: 8.5),
                           radius: 5.5, startAngle: 0.6897750007854998,
                           endAngle: -0.6897750007854998, clockwise: true)
            context.closePath()
            context.move(to: .init(x: 2, y: 12))
            context.addLine(to: .init(x: 7, y: 12))
            context.addLine(to: .init(x: 9, y: 8))
            context.addLine(to: .init(x: 13, y: 16))
            context.addLine(to: .init(x: 15, y: 12))
            context.addLine(to: .init(x: 22, y: 12))
            context.strokePath()

        case .fm:
            context.setLineWidth(1.8)
            context.setLineCap(.butt)
            context.setLineJoin(.miter)
            context.setMiterLimit(4)
            context.move(to: .init(x: 12, y: 12))
            context.addCurve(to: .init(x: 5, y: 8),
                             control1: .init(x: 9, y: 7), control2: .init(x: 7, y: 7))
            context.addCurve(to: .init(x: 5, y: 16),
                             control1: .init(x: 1, y: 10), control2: .init(x: 1, y: 14))
            context.addCurve(to: .init(x: 12, y: 12),
                             control1: .init(x: 7, y: 17), control2: .init(x: 9, y: 17))
            context.addCurve(to: .init(x: 19, y: 8),
                             control1: .init(x: 15, y: 7), control2: .init(x: 17, y: 7))
            context.addCurve(to: .init(x: 19, y: 16),
                             control1: .init(x: 23, y: 10), control2: .init(x: 23, y: 14))
            context.addCurve(to: .init(x: 12, y: 12),
                             control1: .init(x: 17, y: 17), control2: .init(x: 15, y: 17))
            context.closePath()
            context.strokePath()
        }
    }
}
#endif
