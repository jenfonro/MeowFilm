// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS)
import UIKit

/// Reference-stable geometry rendered once as a native template image.
public enum PageIcon {
    public enum Kind: Sendable {
        case home, channels, person, menu, search, recognize, plus, append, more, play, pause, queue, down
        case playerDown, playerPlay, playerPause, lyricPlay, previous, next, miniPrevious, miniNext
        case heart, translation, image, remove, playing, miniResume, reorder, multiSelect
    }
    public static func image(_ kind: Kind, filled: Bool = false, size: CGFloat = 23) -> UIImage {
        UIGraphicsImageRenderer(size: CGSize(width: size, height: size)).image { renderer in
            let context = renderer.cgContext
            context.scaleBy(x: size / 24, y: size / 24)
            context.setFillColor(UIColor.black.cgColor)
            context.setStrokeColor(UIColor.black.cgColor)
            context.setLineCap(.round)
            context.setLineJoin(.round)
            context.setLineWidth(strokeWidth(kind))
            let path = shape(kind)
            switch kind {
            case .play, .pause, .more, .lyricPlay, .miniResume:
                context.addPath(path); context.fillPath()
            default:
                let solid = kind == .playerPlay || kind == .playerPause ||
                    kind == .previous || kind == .next || kind == .heart && filled
                if solid || filled {
                    context.setAlpha(solid ? 1 : 0.12)
                    context.addPath(path); context.fillPath()
                    context.setAlpha(1)
                }
                context.addPath(path); context.strokePath()
                if kind == .queue {
                    let marker = CGMutablePath()
                    marker.move(to: CGPoint(x: 2, y: 4))
                    marker.addLine(to: CGPoint(x: 5, y: 6))
                    marker.addLine(to: CGPoint(x: 2, y: 8)); marker.closeSubpath()
                    context.addPath(marker); context.fillPath()
                }
            }
        }.withRenderingMode(.alwaysTemplate)
    }
    private static func strokeWidth(_ kind: Kind) -> CGFloat {
        switch kind {
        case .playerPlay, .playerPause, .previous, .next: 2
        case .playerDown: 1.8
        case .translation, .image: 1.6
        case .playing: 3
        default: 1.7
        }
    }
    private static func shape(_ kind: Kind) -> CGPath {
        let p = CGMutablePath()
        func line(_ points: [(CGFloat, CGFloat)], close: Bool = false) {
            guard let first = points.first else { return }
            p.move(to: .init(x: first.0, y: first.1))
            for point in points.dropFirst() { p.addLine(to: .init(x: point.0, y: point.1)) }
            if close { p.closeSubpath() }
        }
        switch kind {
        case .home: line([(3,10),(12,3),(21,10),(21,21),(15,21),(15,14),(9,14),(9,21),(3,21)], close: true)
        case .channels:
            for x in [3.0, 14.0] { for y in [3.0, 14.0] {
                p.addRoundedRect(in: .init(x: x, y: y, width: 7, height: 7), cornerSize: .init(width: 2, height: 2))
            } }
        case .person:
            p.addEllipse(in: .init(x: 8, y: 3, width: 8, height: 8))
            p.move(to: .init(x: 4, y: 22)); p.addLine(to: .init(x: 4, y: 19))
            p.addCurve(to: .init(x: 20, y: 19), control1: .init(x: 4, y: 8.33), control2: .init(x: 20, y: 8.33))
            p.addLine(to: .init(x: 20, y: 22))
        case .menu:
            for y in [6.0,12.0,18.0] { line([(4,y),(20,y)]) }
        case .search:
            p.addEllipse(in: .init(x: 3, y: 3, width: 15, height: 15)); line([(16,16),(21,21)])
        case .recognize:
            line([(4,8),(4,5),(6,3),(8,3)]); line([(16,3),(18,3),(20,5),(20,8)])
            line([(20,16),(20,19),(18,21),(16,21)]); line([(8,21),(6,21),(4,19),(4,16)])
            line([(8,10),(8,14)]); line([(12,7),(12,17)]); line([(16,10),(16,14)])
        case .plus: line([(12,4),(12,20)]); line([(4,12),(20,12)])
        case .append:
            line([(4,3),(17,11),(4,19)], close: true); line([(18,15),(18,22)]); line([(15,18.5),(21,18.5)])
        case .more:
            for y in [4.5,12.0,19.5] { p.addEllipse(in: .init(x: 10.5, y: y - 1.5, width: 3, height: 3)) }
        case .play: line([(8,4),(20,12),(8,20)], close: true)
        case .pause:
            p.addRect(.init(x: 6, y: 4, width: 4, height: 16)); p.addRect(.init(x: 14, y: 4, width: 4, height: 16))
        case .queue:
            for y in [6.0,12.0,18.0] { line([(8,y),(21,y)]) }
            line([(2,4),(5,6),(2,8)], close: true)
        case .down: line([(6,9),(12,15),(18,9)])
        case .playerDown: line([(5,9),(12,16),(19,9)])
        case .playerPlay: line([(5,3),(19,12),(5,21)], close: true)
        case .playerPause:
            p.addRect(.init(x: 6, y: 4, width: 4, height: 16)); p.addRect(.init(x: 14, y: 4, width: 4, height: 16))
        case .lyricPlay: line([(8,5),(19,12),(8,19)], close: true)
        case .miniResume: line([(8,5),(19,12),(8,19)], close: true)
        case .previous: line([(19,20),(9,12),(19,4)], close: true); line([(5,19),(5,5)])
        case .next: line([(5,4),(15,12),(5,20)], close: true); line([(19,5),(19,19)])
        case .miniPrevious: line([(5,5),(5,19)]); line([(19,5),(9,12),(19,19)], close: true)
        case .miniNext: line([(19,5),(19,19)]); line([(5,5),(15,12),(5,19)], close: true)
        case .heart:
            // The original SVG's circular lobes, not an SF Symbol substitute.
            p.move(to: .init(x: 20.84, y: 4.61))
            p.addRelativeArc(center: .init(x: 16.95, y: 8.50), radius: 5.5,
                             startAngle: (-45 * .pi / 180), delta: (-90 * .pi / 180))
            p.addLine(to: .init(x: 12, y: 5.67))
            p.addLine(to: .init(x: 10.94, y: 4.61))
            p.addRelativeArc(center: .init(x: 7.05, y: 8.50), radius: 5.5,
                             startAngle: (-45 * .pi / 180), delta: (-180 * .pi / 180))
            p.addLine(to: .init(x: 12, y: 21.23))
            p.addLine(to: .init(x: 20.84, y: 12.39))
            p.addRelativeArc(center: .init(x: 16.95, y: 8.50), radius: 5.5,
                             startAngle: (45 * .pi / 180), delta: (-90 * .pi / 180))
            p.closeSubpath()
        case .translation:
            line([(3,5),(15,5)]); line([(9,3),(9,5)])
            p.move(to: .init(x: 12, y: 5))
            p.addCurve(to: .init(x: 3, y: 16), control1: .init(x: 11, y: 11), control2: .init(x: 8, y: 14))
            p.move(to: .init(x: 5, y: 8))
            p.addCurve(to: .init(x: 12, y: 16), control1: .init(x: 6, y: 12), control2: .init(x: 9, y: 14))
            line([(13,21),(17,10),(21,21)]); line([(14.5,17),(19.5,17)])
        case .image:
            p.addRoundedRect(in: .init(x: 3, y: 3, width: 18, height: 18), cornerSize: .init(width: 5, height: 5))
            p.addEllipse(in: .init(x: 6.5, y: 6.5, width: 3, height: 3))
            line([(3,17),(9,11),(13,15),(16,12),(21,17)])
        case .remove:
            line([(4,7),(20,7)]); line([(9,3),(15,3)])
            line([(6,7),(7,21),(17,21),(18,7)])
            line([(10,11),(10,17)]); line([(14,11),(14,17)])
        case .playing:
            line([(5,9),(5,15)]); line([(10,4),(10,20)])
            line([(15,7),(15,17)]); line([(20,10),(20,14)])
        case .reorder:
            for y in [7.0, 12.0, 17.0] { line([(5,y),(19,y)]) }
        case .multiSelect:
            line([(3,6),(5,8),(8,4)])
            line([(11,6),(21,6)])
            line([(3,12),(21,12)])
            line([(3,18),(21,18)])
        }
        return p
    }
}

private extension CGMutablePath {
    func addRoundedRect(in rect: CGRect, cornerSize: CGSize) {
        addRoundedRect(in: rect, cornerWidth: cornerSize.width, cornerHeight: cornerSize.height)
    }
}
#endif
