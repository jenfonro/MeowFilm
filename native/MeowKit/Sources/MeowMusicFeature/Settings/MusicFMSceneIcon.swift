// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS)
import UIKit

enum MusicFMSceneIcon {
    static func image(code: String, size: CGFloat = 25) -> UIImage {
        UIGraphicsImageRenderer(size: CGSize(width: size, height: size)).image { renderer in
            let context = renderer.cgContext
            context.scaleBy(x: size / 32, y: size / 32)
            context.setFillColor(UIColor.black.cgColor)
            context.setStrokeColor(UIColor.black.cgColor)
            context.setLineCap(.round); context.setLineJoin(.round)
            draw(code, in: context)
        }.withRenderingMode(.alwaysTemplate)
    }
    private static func drawText(_ value: String, font: UIFont, centerX: CGFloat,
                                 baseline: CGFloat, in context: CGContext) {
        UIGraphicsPushContext(context)
        defer { UIGraphicsPopContext() }
        let text = value as NSString
        let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: UIColor.black]
        let width = text.size(withAttributes: attributes).width
        text.draw(at: CGPoint(x: centerX - width / 2, y: baseline - font.ascender),
                  withAttributes: attributes)
    }
    private static func draw(_ code: String, in context: CGContext) {
        switch code {
        case "NIGHT_EMO": NIGHT_EMO(in: context)
        case "EXERCISE": EXERCISE(in: context)
        case "SLEEP_HELP": SLEEP_HELP(in: context)
        case "RELAX": RELAX(in: context)
        case "CHEERFUL": CHEERFUL(in: context)
        case "LYRICAL": LYRICAL(in: context)
        case "CURE": CURE(in: context)
        case "FOCUS": FOCUS(in: context)
        case "SWEET": SWEET(in: context)
        case "RHYTHM_BLUES": RHYTHM_BLUES(in: context)
        case "RAINY": RAINY(in: context)
        case "GAMES": GAMES(in: context)
        case "RAP": RAP(in: context)
        case "K_POP": K_POP(in: context)
        case "ORIGINAL_MUSICIAL": ORIGINAL_MUSICIAL(in: context)
        case "ELECTRONIC": ELECTRONIC(in: context)
        case "COMMUTE": COMMUTE(in: context)
        case "TAKE_SHOWER": TAKE_SHOWER(in: context)
        case "COFFEE_SHOP": COFFEE_SHOP(in: context)
        case "ROCK": ROCK(in: context)
        case "INSPIRATIONAL": INSPIRATIONAL(in: context)
        case "CHINESE": CHINESE(in: context)
        case "ENGLISH": ENGLISH(in: context)
        case "YUEYU": YUEYU(in: context)
        case "MANYAO": MANYAO(in: context)
        case "JINGDIAN": JINGDIAN(in: context)
        case "LIGHT": LIGHT(in: context)
        case "GUOFENG": GUOFENG(in: context)
        case "FOLK": FOLK(in: context)
        case "ACG": ACG(in: context)
        case "GUDIAN": GUDIAN(in: context)
        case "JAZZ": JAZZ(in: context)
        case "JAPANESE": JAPANESE(in: context)
        case "GLOBAL": GLOBAL(in: context)
        case "FRANCH": FRANCH(in: context)
        case "BLUE": BLUE(in: context)
        case "DANCE": DANCE(in: context)
        case "LATIN": LATIN(in: context)
        case "PUNK": PUNK(in: context)
        case "COUNTRY": COUNTRY(in: context)
        case "MUSICAL": MUSICAL(in: context)
        case "YINGSHI": YINGSHI(in: context)
        case "HOT_SONG": HOT_SONG(in: context)
        default: break
        }
    }
    private static func HOT_SONG(in context: CGContext) {
        let path = CGMutablePath()
        path.move(to: .init(x: 18, y: 2))
        path.addCurve(to: .init(x: 9, y: 15), control1: .init(x: 18, y: 8), control2: .init(x: 10, y: 9))
        path.addLine(to: .init(x: 6, y: 11))
        path.addCurve(to: .init(x: 5, y: 21), control1: .init(x: 4, y: 14), control2: .init(x: 3, y: 18))
        path.addCurve(to: .init(x: 27, y: 21), control1: .init(x: 7, y: 33), control2: .init(x: 25, y: 33))
        path.addCurve(to: .init(x: 18, y: 2), control1: .init(x: 30, y: 12), control2: .init(x: 21, y: 5))
        path.closeSubpath()
        path.move(to: .init(x: 16, y: 16))
        path.addCurve(to: .init(x: 12, y: 23), control1: .init(x: 16, y: 19), control2: .init(x: 12, y: 20))
        path.addCurve(to: .init(x: 21, y: 23), control1: .init(x: 12, y: 29), control2: .init(x: 21, y: 29))
        path.addCurve(to: .init(x: 16, y: 16), control1: .init(x: 22, y: 20), control2: .init(x: 18, y: 18))
        path.closeSubpath()
        context.addPath(path); context.fillPath(using: .evenOdd)
    }
    private static func NIGHT_EMO(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 19, y: 3, width: 6, height: 6))
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 16, y: 9))
            p.addCurve(to: .init(x: 7, y: 23), control1: .init(x: 11, y: 10), control2: .init(x: 7, y: 17))
            p.addCurve(to: .init(x: 13, y: 27), control1: .init(x: 7, y: 27), control2: .init(x: 10, y: 29))
            p.addLine(to: .init(x: 19, y: 20))
            p.addLine(to: .init(x: 24, y: 28))
            p.addCurve(to: .init(x: 25.84010303, y: 28.52030908), control1: .init(x: 24.44844053, y: 28.53466935), control2: .init(x: 25.17808097, y: 28.7409831))
            p.addCurve(to: .init(x: 27, y: 27), control1: .init(x: 26.50212509, y: 28.29963506), control2: .init(x: 26.96204919, y: 27.6967998))
            p.addLine(to: .init(x: 21, y: 17))
            p.addCurve(to: .init(x: 19.61523743, y: 16.05857545), control1: .init(x: 20.69831605, y: 16.49876661), control2: .init(x: 20.19229736, y: 16.1547521))
            p.addCurve(to: .init(x: 18, y: 16.5), control1: .init(x: 19.03817749, y: 15.96239879), control2: .init(x: 18.44793889, y: 16.12370375))
            p.addLine(to: .init(x: 15, y: 19.5))
            p.addLine(to: .init(x: 14, y: 15.5))
            p.addLine(to: .init(x: 17, y: 11.5))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 17, y: 11))
            p.addLine(to: .init(x: 21, y: 16))
            p.addLine(to: .init(x: 26, y: 17))
            context.addPath(p); context.setLineWidth(3); context.strokePath()
        }
    }
    private static func EXERCISE(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 17, y: 2, width: 6, height: 6))
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 19, y: 11))
            p.addLine(to: .init(x: 15, y: 18))
            p.addLine(to: .init(x: 20, y: 22))
            p.addLine(to: .init(x: 22, y: 28))
            p.move(to: .init(x: 14, y: 19))
            p.addLine(to: .init(x: 10, y: 24))
            p.addLine(to: .init(x: 5, y: 24))
            p.move(to: .init(x: 19, y: 11))
            p.addLine(to: .init(x: 12, y: 9))
            p.addLine(to: .init(x: 7, y: 12))
            p.move(to: .init(x: 19, y: 11))
            p.addLine(to: .init(x: 23, y: 14))
            p.addLine(to: .init(x: 28, y: 11))
            context.addPath(p); context.setLineWidth(3.5); context.strokePath()
        }
    }
    private static func SLEEP_HELP(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 28, y: 18))
            p.addCurve(to: .init(x: 17.31055015, y: 14.68944985), control1: .init(x: 24.10891798, y: 18.72708739), control2: .init(x: 20.10958349, y: 17.48848319))
            p.addCurve(to: .init(x: 14, y: 4), control1: .init(x: 14.51151681, y: 11.89041651), control2: .init(x: 13.27291261, y: 7.89108202))
            p.addCurve(to: .init(x: 4.2269113, y: 16.53428292), control1: .init(x: 8.04568493, y: 5.11262301), control2: .init(x: 3.85415485, y: 10.48838757))
            p.addCurve(to: .init(x: 15.46571708, y: 27.7730887), control1: .init(x: 4.59966775, y: 22.58017827), control2: .init(x: 9.41982173, y: 27.40033225))
            p.addCurve(to: .init(x: 28, y: 18), control1: .init(x: 21.51161243, y: 28.14584515), control2: .init(x: 26.88737699, y: 23.95431507))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 22, y: 4))
            p.addLine(to: .init(x: 28, y: 4))
            p.addLine(to: .init(x: 22, y: 11))
            p.addLine(to: .init(x: 28, y: 11))
            context.addPath(p); context.setLineWidth(2); context.strokePath()
        }
    }
    private static func RELAX(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 14, y: 4, width: 6, height: 6))
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 17, y: 13))
            p.addLine(to: .init(x: 13, y: 20))
            p.addLine(to: .init(x: 21, y: 19))
            p.addLine(to: .init(x: 23, y: 14))
            p.move(to: .init(x: 17, y: 13))
            p.addLine(to: .init(x: 11, y: 14))
            p.addLine(to: .init(x: 6, y: 8))
            p.move(to: .init(x: 17, y: 13))
            p.addLine(to: .init(x: 23, y: 10))
            p.addLine(to: .init(x: 27, y: 4))
            p.move(to: .init(x: 3, y: 10))
            p.addCurve(to: .init(x: 29, y: 8), control1: .init(x: 9, y: 23), control2: .init(x: 23, y: 25))
            p.move(to: .init(x: 13, y: 25))
            p.addLine(to: .init(x: 11, y: 29))
            p.move(to: .init(x: 22, y: 24))
            p.addLine(to: .init(x: 23, y: 29))
            context.addPath(p); context.setLineWidth(2.8); context.strokePath()
        }
    }
    private static func CHEERFUL(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 12, y: 3, width: 6, height: 6))
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 16, y: 12))
            p.addLine(to: .init(x: 14, y: 20))
            p.addLine(to: .init(x: 23, y: 21))
            p.addLine(to: .init(x: 18, y: 28))
            p.move(to: .init(x: 14, y: 20))
            p.addLine(to: .init(x: 6, y: 27))
            p.move(to: .init(x: 16, y: 12))
            p.addLine(to: .init(x: 9, y: 14))
            p.addLine(to: .init(x: 4, y: 12))
            p.move(to: .init(x: 16, y: 12))
            p.addLine(to: .init(x: 22, y: 8))
            p.addLine(to: .init(x: 25, y: 2))
            context.addPath(p); context.setLineWidth(3.5); context.strokePath()
        }
    }
    private static func LYRICAL(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 9, y: 4, width: 6, height: 6))
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 22, y: 6))
            p.addCurve(to: .init(x: 22, y: 12), control1: .init(x: 18, y: 2), control2: .init(x: 15, y: 8))
            p.addCurve(to: .init(x: 22, y: 6), control1: .init(x: 29, y: 8), control2: .init(x: 26, y: 2))
            p.closeSubpath()
            p.move(to: .init(x: 10, y: 12))
            p.addCurve(to: .init(x: 9, y: 27), control1: .init(x: 8, y: 15), control2: .init(x: 8, y: 20))
            p.addLine(to: .init(x: 13, y: 27))
            p.addLine(to: .init(x: 13, y: 19))
            p.addLine(to: .init(x: 17, y: 21))
            p.addLine(to: .init(x: 17, y: 27))
            p.addLine(to: .init(x: 26, y: 27))
            p.addLine(to: .init(x: 26, y: 21))
            p.addCurve(to: .init(x: 16, y: 16), control1: .init(x: 26, y: 17), control2: .init(x: 20, y: 14))
            p.addLine(to: .init(x: 14, y: 12))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 8, y: 10))
            p.addLine(to: .init(x: 5, y: 15))
            p.addLine(to: .init(x: 6, y: 27))
            context.addPath(p); context.setLineWidth(2.5); context.strokePath()
        }
    }
    private static func CURE(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 8, y: 4, width: 6, height: 6))
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 18, y: 4, width: 6, height: 6))
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 5, y: 27))
            p.addLine(to: .init(x: 7, y: 16))
            p.addQuadCurve(to: .init(x: 13, y: 14), control: .init(x: 8, y: 11))
            p.addLine(to: .init(x: 18, y: 18))
            p.addLine(to: .init(x: 24, y: 18))
            p.move(to: .init(x: 27, y: 27))
            p.addLine(to: .init(x: 25, y: 16))
            p.addQuadCurve(to: .init(x: 19, y: 14), control: .init(x: 24, y: 11))
            p.addLine(to: .init(x: 14, y: 18))
            p.addLine(to: .init(x: 8, y: 18))
            p.move(to: .init(x: 11, y: 21))
            p.addLine(to: .init(x: 10, y: 27))
            p.move(to: .init(x: 21, y: 21))
            p.addLine(to: .init(x: 22, y: 27))
            context.addPath(p); context.setLineWidth(3.5); context.strokePath()
        }
    }
    private static func FOCUS(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 7, y: 6))
            p.addLine(to: .init(x: 21, y: 9))
            p.addLine(to: .init(x: 21, y: 29))
            p.addLine(to: .init(x: 7, y: 26))
            p.addQuadCurve(to: .init(x: 4, y: 22), control: .init(x: 4, y: 25.5))
            p.addLine(to: .init(x: 4, y: 10))
            p.addQuadCurve(to: .init(x: 7, y: 6), control: .init(x: 4, y: 6))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 7, y: 6))
            p.addLine(to: .init(x: 7, y: 4))
            p.addLine(to: .init(x: 25, y: 7))
            p.addQuadCurve(to: .init(x: 27, y: 9), control: .init(x: 27, y: 7.3))
            p.addLine(to: .init(x: 27, y: 25))
            context.addPath(p); context.setLineWidth(2.5); context.strokePath()
        }
    }
    private static func SWEET(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 13, y: 22))
            p.addLine(to: .init(x: 5, y: 15))
            p.addCurve(to: .init(x: 12, y: 8), control1: .init(x: -1, y: 9), control2: .init(x: 7, y: 3))
            p.addLine(to: .init(x: 13, y: 9))
            p.addLine(to: .init(x: 14, y: 8))
            p.addCurve(to: .init(x: 22, y: 13), control1: .init(x: 18, y: 4), control2: .init(x: 24, y: 8))
            context.addPath(p); context.setLineWidth(2.5); context.strokePath()
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 22, y: 28))
            p.addLine(to: .init(x: 14, y: 20))
            p.addCurve(to: .init(x: 20, y: 13), control1: .init(x: 9, y: 15), control2: .init(x: 16, y: 9))
            p.addLine(to: .init(x: 22, y: 15))
            p.addLine(to: .init(x: 24, y: 13))
            p.addCurve(to: .init(x: 29, y: 20), control1: .init(x: 28, y: 9), control2: .init(x: 34, y: 15))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
    }
    private static func RHYTHM_BLUES(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 15, y: 2, width: 12, height: 12))
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 16, y: 12))
            p.addLine(to: .init(x: 7, y: 22))
            p.addLine(to: .init(x: 11, y: 25))
            p.addLine(to: .init(x: 22, y: 15))
            p.move(to: .init(x: 9, y: 26))
            p.addCurve(to: .init(x: 14, y: 28), control1: .init(x: 6, y: 30), control2: .init(x: 11, y: 31))
            p.addLine(to: .init(x: 21, y: 20))
            p.addQuadCurve(to: .init(x: 28, y: 19), control: .init(x: 25, y: 16))
            context.addPath(p); context.setLineWidth(2.5); context.strokePath()
        }
    }
    private static func RAINY(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 3, y: 17))
            p.addCurve(to: .init(x: 16, y: 6), control1: .init(x: 4, y: 10), control2: .init(x: 9, y: 6))
            p.addCurve(to: .init(x: 29, y: 17), control1: .init(x: 23, y: 6), control2: .init(x: 28, y: 10))
            p.addCurve(to: .init(x: 21, y: 17), control1: .init(x: 26, y: 15), control2: .init(x: 24, y: 15))
            p.addCurve(to: .init(x: 11, y: 17), control1: .init(x: 18, y: 15), control2: .init(x: 14, y: 15))
            p.addCurve(to: .init(x: 3, y: 17), control1: .init(x: 8, y: 15), control2: .init(x: 6, y: 15))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 16, y: 6))
            p.addLine(to: .init(x: 16, y: 25))
            p.addCurve(to: .init(x: 12, y: 29), control1: .init(x: 16, y: 27.209139), control2: .init(x: 14.209139, y: 29))
            p.addCurve(to: .init(x: 8, y: 25), control1: .init(x: 9.790861, y: 29), control2: .init(x: 8, y: 27.209139))
            p.move(to: .init(x: 7, y: 3))
            p.addLine(to: .init(x: 7, y: 5))
            p.move(to: .init(x: 16, y: 1))
            p.addLine(to: .init(x: 16, y: 3))
            p.move(to: .init(x: 25, y: 3))
            p.addLine(to: .init(x: 25, y: 5))
            context.addPath(p); context.setLineWidth(2.5); context.strokePath()
        }
    }
    private static func GAMES(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 10, y: 7))
            p.addLine(to: .init(x: 22, y: 7))
            p.addCurve(to: .init(x: 30, y: 22), control1: .init(x: 27, y: 7), control2: .init(x: 29, y: 12))
            p.addCurve(to: .init(x: 22, y: 24), control1: .init(x: 31, y: 29), control2: .init(x: 25, y: 30))
            p.addLine(to: .init(x: 20, y: 21))
            p.addLine(to: .init(x: 12, y: 21))
            p.addLine(to: .init(x: 10, y: 24))
            p.addCurve(to: .init(x: 2, y: 22), control1: .init(x: 7, y: 30), control2: .init(x: 1, y: 29))
            p.addCurve(to: .init(x: 10, y: 7), control1: .init(x: 3, y: 12), control2: .init(x: 5, y: 7))
            p.closeSubpath()
            p.move(to: .init(x: 8, y: 12))
            p.addLine(to: .init(x: 8, y: 15))
            p.addLine(to: .init(x: 5, y: 15))
            p.addLine(to: .init(x: 5, y: 18))
            p.addLine(to: .init(x: 8, y: 18))
            p.addLine(to: .init(x: 8, y: 21))
            p.addLine(to: .init(x: 11, y: 21))
            p.addLine(to: .init(x: 11, y: 18))
            p.addLine(to: .init(x: 14, y: 18))
            p.addLine(to: .init(x: 14, y: 15))
            p.addLine(to: .init(x: 11, y: 15))
            p.addLine(to: .init(x: 11, y: 12))
            p.closeSubpath()
            p.move(to: .init(x: 21, y: 12))
            p.addCurve(to: .init(x: 19.5, y: 13.5), control1: .init(x: 20.17157288, y: 12), control2: .init(x: 19.5, y: 12.67157288))
            p.addCurve(to: .init(x: 21, y: 15), control1: .init(x: 19.5, y: 14.32842712), control2: .init(x: 20.17157288, y: 15))
            p.addCurve(to: .init(x: 22.5, y: 13.5), control1: .init(x: 21.82842712, y: 15), control2: .init(x: 22.5, y: 14.32842712))
            p.addCurve(to: .init(x: 21, y: 12), control1: .init(x: 22.5, y: 12.67157288), control2: .init(x: 21.82842712, y: 12))
            p.closeSubpath()
            p.move(to: .init(x: 26, y: 17))
            p.addCurve(to: .init(x: 24.5, y: 18.5), control1: .init(x: 25.17157288, y: 17), control2: .init(x: 24.5, y: 17.67157288))
            p.addCurve(to: .init(x: 26, y: 20), control1: .init(x: 24.5, y: 19.32842712), control2: .init(x: 25.17157288, y: 20))
            p.addCurve(to: .init(x: 27.5, y: 18.5), control1: .init(x: 26.82842712, y: 20), control2: .init(x: 27.5, y: 19.32842712))
            p.addCurve(to: .init(x: 26, y: 17), control1: .init(x: 27.5, y: 17.67157288), control2: .init(x: 26.82842712, y: 17))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
    }
    private static func RAP(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 7, y: 3))
            p.addCurve(to: .init(x: 25, y: 3), control1: .init(x: 8, y: 13), control2: .init(x: 22, y: 15))
            p.move(to: .init(x: 3, y: 14))
            p.addLine(to: .init(x: 27, y: 9))
            p.addLine(to: .init(x: 30, y: 25))
            p.addLine(to: .init(x: 6, y: 30))
            p.closeSubpath()
            context.addPath(p); context.setLineWidth(2.5); context.strokePath()
        }
        do {
            context.saveGState()
            defer { context.restoreGState() }
            let local = context
            local.translateBy(x: 17, y: 20)
            local.rotate(by: -12 * .pi / 180)
            local.translateBy(x: -17, y: -20)
            drawText("RICH", font: UIFont.systemFont(ofSize: 8, weight: .bold), centerX: 17, baseline: 23, in: local)
        }
    }
    private static func K_POP(in context: CGContext) {
        do {
            context.saveGState()
            defer { context.restoreGState() }
            let local = context
            drawText("한", font: UIFont.systemFont(ofSize: 28, weight: .bold), centerX: 16, baseline: 26, in: local)
        }
    }
    private static func ORIGINAL_MUSICIAL(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 28, y: 16))
            p.addCurve(to: .init(x: 16, y: 28), control1: .init(x: 28, y: 22.627417), control2: .init(x: 22.627417, y: 28))
            p.addCurve(to: .init(x: 4, y: 16), control1: .init(x: 9.372583, y: 28), control2: .init(x: 4, y: 22.627417))
            p.addCurve(to: .init(x: 16, y: 4), control1: .init(x: 4, y: 9.372583), control2: .init(x: 9.372583, y: 4))
            p.addLine(to: .init(x: 16, y: 14))
            p.closeSubpath()
            p.move(to: .init(x: 16, y: 14))
            p.addCurve(to: .init(x: 14, y: 16), control1: .init(x: 14.8954305, y: 14), control2: .init(x: 14, y: 14.8954305))
            p.addCurve(to: .init(x: 16, y: 18), control1: .init(x: 14, y: 17.1045695), control2: .init(x: 14.8954305, y: 18))
            p.addCurve(to: .init(x: 18, y: 16), control1: .init(x: 17.1045695, y: 18), control2: .init(x: 18, y: 17.1045695))
            p.addCurve(to: .init(x: 16, y: 14), control1: .init(x: 18, y: 14.8954305), control2: .init(x: 17.1045695, y: 14))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 19, y: 12))
            p.addLine(to: .init(x: 24, y: 2))
            p.addLine(to: .init(x: 30, y: 7))
            p.addLine(to: .init(x: 19, y: 13))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
    }
    private static func ELECTRONIC(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 4, y: 17))
            p.addLine(to: .init(x: 10, y: 17))
            p.addLine(to: .init(x: 10, y: 22))
            p.addLine(to: .init(x: 4, y: 22))
            p.closeSubpath()
            p.move(to: .init(x: 4, y: 24))
            p.addLine(to: .init(x: 10, y: 24))
            p.addLine(to: .init(x: 10, y: 29))
            p.addLine(to: .init(x: 4, y: 29))
            p.closeSubpath()
            p.move(to: .init(x: 4, y: 10))
            p.addLine(to: .init(x: 10, y: 10))
            p.addLine(to: .init(x: 10, y: 15))
            p.addLine(to: .init(x: 4, y: 15))
            p.closeSubpath()
            p.move(to: .init(x: 13, y: 24))
            p.addLine(to: .init(x: 19, y: 24))
            p.addLine(to: .init(x: 19, y: 29))
            p.addLine(to: .init(x: 13, y: 29))
            p.closeSubpath()
            p.move(to: .init(x: 13, y: 17))
            p.addLine(to: .init(x: 19, y: 17))
            p.addLine(to: .init(x: 19, y: 22))
            p.addLine(to: .init(x: 13, y: 22))
            p.closeSubpath()
            p.move(to: .init(x: 22, y: 24))
            p.addLine(to: .init(x: 28, y: 24))
            p.addLine(to: .init(x: 28, y: 29))
            p.addLine(to: .init(x: 22, y: 29))
            p.closeSubpath()
            p.move(to: .init(x: 22, y: 17))
            p.addLine(to: .init(x: 28, y: 17))
            p.addLine(to: .init(x: 28, y: 22))
            p.addLine(to: .init(x: 22, y: 22))
            p.closeSubpath()
            p.move(to: .init(x: 22, y: 10))
            p.addLine(to: .init(x: 28, y: 10))
            p.addLine(to: .init(x: 28, y: 15))
            p.addLine(to: .init(x: 22, y: 15))
            p.closeSubpath()
            p.move(to: .init(x: 22, y: 3))
            p.addLine(to: .init(x: 28, y: 3))
            p.addLine(to: .init(x: 28, y: 8))
            p.addLine(to: .init(x: 22, y: 8))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
    }
    private static func COMMUTE(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 9, y: 3))
            p.addLine(to: .init(x: 23, y: 3))
            p.addCurve(to: .init(x: 28, y: 8), control1: .init(x: 25.76142375, y: 3), control2: .init(x: 28, y: 5.23857625))
            p.addLine(to: .init(x: 28, y: 25))
            p.addCurve(to: .init(x: 25, y: 28), control1: .init(x: 28, y: 26.65685425), control2: .init(x: 26.65685425, y: 28))
            p.addLine(to: .init(x: 25, y: 30))
            p.addLine(to: .init(x: 21, y: 30))
            p.addLine(to: .init(x: 21, y: 28))
            p.addLine(to: .init(x: 11, y: 28))
            p.addLine(to: .init(x: 11, y: 30))
            p.addLine(to: .init(x: 7, y: 30))
            p.addLine(to: .init(x: 7, y: 28))
            p.addCurve(to: .init(x: 4, y: 25), control1: .init(x: 5.34314575, y: 28), control2: .init(x: 4, y: 26.65685425))
            p.addLine(to: .init(x: 4, y: 8))
            p.addCurve(to: .init(x: 9, y: 3), control1: .init(x: 4, y: 5.23857625), control2: .init(x: 6.23857625, y: 3))
            p.closeSubpath()
            p.move(to: .init(x: 11, y: 5))
            p.addLine(to: .init(x: 11, y: 7))
            p.addLine(to: .init(x: 21, y: 7))
            p.addLine(to: .init(x: 21, y: 5))
            p.closeSubpath()
            p.move(to: .init(x: 7, y: 10))
            p.addLine(to: .init(x: 7, y: 19))
            p.addQuadCurve(to: .init(x: 25, y: 19), control: .init(x: 16, y: 22))
            p.addLine(to: .init(x: 25, y: 10))
            p.closeSubpath()
            p.move(to: .init(x: 7, y: 23))
            p.addLine(to: .init(x: 7, y: 25))
            p.addLine(to: .init(x: 12, y: 25))
            p.addLine(to: .init(x: 12, y: 23))
            p.closeSubpath()
            p.move(to: .init(x: 20, y: 23))
            p.addLine(to: .init(x: 20, y: 25))
            p.addLine(to: .init(x: 25, y: 25))
            p.addLine(to: .init(x: 25, y: 23))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
    }
    private static func TAKE_SHOWER(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 3, y: 16))
            p.addLine(to: .init(x: 29, y: 16))
            p.addLine(to: .init(x: 29, y: 21))
            p.addCurve(to: .init(x: 23, y: 27), control1: .init(x: 29, y: 24.3137085), control2: .init(x: 26.3137085, y: 27))
            p.addLine(to: .init(x: 9, y: 27))
            p.addCurve(to: .init(x: 3, y: 21), control1: .init(x: 5.6862915, y: 27), control2: .init(x: 3, y: 24.3137085))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 2, y: 13))
            p.addLine(to: .init(x: 30, y: 13))
            p.move(to: .init(x: 8, y: 27))
            p.addLine(to: .init(x: 8, y: 29))
            p.move(to: .init(x: 24, y: 27))
            p.addLine(to: .init(x: 24, y: 29))
            p.move(to: .init(x: 22, y: 13))
            p.addLine(to: .init(x: 22, y: 6))
            p.addCurve(to: .init(x: 18, y: 2), control1: .init(x: 22, y: 3.790861), control2: .init(x: 20.209139, y: 2))
            p.addCurve(to: .init(x: 14, y: 6), control1: .init(x: 15.790861, y: 2), control2: .init(x: 14, y: 3.790861))
            p.addLine(to: .init(x: 14, y: 7))
            context.addPath(p); context.setLineWidth(2.5); context.strokePath()
        }
    }
    private static func COFFEE_SHOP(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 3, y: 11))
            p.addLine(to: .init(x: 23, y: 11))
            p.addLine(to: .init(x: 23, y: 18))
            p.addCurve(to: .init(x: 14, y: 27), control1: .init(x: 23, y: 22.97056275), control2: .init(x: 18.97056275, y: 27))
            p.addCurve(to: .init(x: 5, y: 18), control1: .init(x: 9.02943725, y: 27), control2: .init(x: 5, y: 22.97056275))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 23, y: 12))
            p.addLine(to: .init(x: 25, y: 12))
            p.addCurve(to: .init(x: 30, y: 17), control1: .init(x: 27.76142375, y: 12), control2: .init(x: 30, y: 14.23857625))
            p.addCurve(to: .init(x: 25, y: 22), control1: .init(x: 30, y: 19.76142375), control2: .init(x: 27.76142375, y: 22))
            p.addLine(to: .init(x: 21, y: 22))
            p.move(to: .init(x: 9, y: 29))
            p.addLine(to: .init(x: 22, y: 29))
            p.move(to: .init(x: 15, y: 11))
            p.addLine(to: .init(x: 19, y: 3))
            context.addPath(p); context.setLineWidth(2.5); context.strokePath()
        }
    }
    private static func ROCK(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 6, y: 18))
            p.addLine(to: .init(x: 6, y: 9))
            p.addCurve(to: .init(x: 8, y: 7), control1: .init(x: 6, y: 7.8954305), control2: .init(x: 6.8954305, y: 7))
            p.addCurve(to: .init(x: 10, y: 9), control1: .init(x: 9.1045695, y: 7), control2: .init(x: 10, y: 7.8954305))
            p.addLine(to: .init(x: 10, y: 17))
            p.addCurve(to: .init(x: 12, y: 19), control1: .init(x: 10, y: 18.1045695), control2: .init(x: 10.8954305, y: 19))
            p.addCurve(to: .init(x: 14, y: 17), control1: .init(x: 13.1045695, y: 19), control2: .init(x: 14, y: 18.1045695))
            p.addLine(to: .init(x: 14, y: 14))
            p.addCurve(to: .init(x: 12, y: 12), control1: .init(x: 14, y: 12.8954305), control2: .init(x: 13.1045695, y: 12))
            p.addCurve(to: .init(x: 10, y: 14), control1: .init(x: 10.8954305, y: 12), control2: .init(x: 10, y: 12.8954305))
            p.move(to: .init(x: 14, y: 17))
            p.addCurve(to: .init(x: 16, y: 19), control1: .init(x: 14, y: 18.1045695), control2: .init(x: 14.8954305, y: 19))
            p.addCurve(to: .init(x: 18, y: 17), control1: .init(x: 17.1045695, y: 19), control2: .init(x: 18, y: 18.1045695))
            p.addLine(to: .init(x: 18, y: 13))
            p.addCurve(to: .init(x: 16, y: 11), control1: .init(x: 18, y: 11.8954305), control2: .init(x: 17.1045695, y: 11))
            p.addCurve(to: .init(x: 14, y: 13), control1: .init(x: 14.8954305, y: 11), control2: .init(x: 14, y: 11.8954305))
            p.move(to: .init(x: 18, y: 15))
            p.addLine(to: .init(x: 18, y: 4))
            p.addCurve(to: .init(x: 20, y: 2), control1: .init(x: 18, y: 2.8954305), control2: .init(x: 18.8954305, y: 2))
            p.addCurve(to: .init(x: 22, y: 4), control1: .init(x: 21.1045695, y: 2), control2: .init(x: 22, y: 2.8954305))
            p.addLine(to: .init(x: 22, y: 16))
            p.move(to: .init(x: 6, y: 18))
            p.addCurve(to: .init(x: 15, y: 29), control1: .init(x: 6, y: 25), control2: .init(x: 9, y: 29))
            p.addCurve(to: .init(x: 27, y: 17), control1: .init(x: 22, y: 29), control2: .init(x: 25, y: 24))
            p.addCurve(to: .init(x: 22, y: 15), control1: .init(x: 28, y: 14), control2: .init(x: 24, y: 12))
            p.addLine(to: .init(x: 17, y: 20))
            context.addPath(p); context.setLineWidth(2.3); context.strokePath()
        }
    }
    private static func INSPIRATIONAL(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 12, y: 3, width: 6, height: 6))
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 15, y: 12))
            p.addLine(to: .init(x: 10, y: 20))
            p.addLine(to: .init(x: 20, y: 16))
            p.addLine(to: .init(x: 23, y: 21))
            p.move(to: .init(x: 10, y: 20))
            p.addLine(to: .init(x: 5, y: 28))
            p.move(to: .init(x: 15, y: 12))
            p.addLine(to: .init(x: 22, y: 7))
            p.addLine(to: .init(x: 27, y: 3))
            p.move(to: .init(x: 13, y: 12))
            p.addLine(to: .init(x: 7, y: 15))
            p.addLine(to: .init(x: 5, y: 20))
            context.addPath(p); context.setLineWidth(3); context.strokePath()
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 17, y: 29))
            p.addLine(to: .init(x: 29, y: 29))
            p.addLine(to: .init(x: 29, y: 18))
            p.addLine(to: .init(x: 23, y: 18))
            p.addLine(to: .init(x: 23, y: 23))
            p.addLine(to: .init(x: 17, y: 23))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
    }
    private static func CHINESE(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.addRoundedRect(in: .init(x: 4, y: 3, width: 24, height: 26),
                             cornerWidth: 3, cornerHeight: 3)
            context.addPath(p); context.setLineWidth(2); context.strokePath()
        }
        do {
            context.saveGState()
            defer { context.restoreGState() }
            let local = context
            drawText("中", font: UIFont.systemFont(ofSize: 23, weight: .bold), centerX: 16, baseline: 24, in: local)
        }
    }
    private static func ENGLISH(in context: CGContext) {
        do {
            context.saveGState()
            defer { context.restoreGState() }
            let local = context
            drawText("En", font: UIFont.systemFont(ofSize: 25, weight: .regular), centerX: 16, baseline: 26, in: local)
        }
    }
    private static func YUEYU(in context: CGContext) {
        do {
            context.saveGState()
            defer { context.restoreGState() }
            let local = context
            drawText("勁", font: UIFont.systemFont(ofSize: 28, weight: .bold), centerX: 16, baseline: 26, in: local)
        }
    }
    private static func MANYAO(in context: CGContext) {
        do {
            context.saveGState()
            defer { context.restoreGState() }
            let local = context
            drawText("DJ", font: UIFont.systemFont(ofSize: 27, weight: .heavy), centerX: 16, baseline: 26, in: local)
        }
    }
    private static func JINGDIAN(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 5, y: 5))
            p.addLine(to: .init(x: 27, y: 5))
            p.addCurve(to: .init(x: 30, y: 8), control1: .init(x: 28.65685425, y: 5), control2: .init(x: 30, y: 6.34314575))
            p.addLine(to: .init(x: 30, y: 25))
            p.addCurve(to: .init(x: 27, y: 28), control1: .init(x: 30, y: 26.65685425), control2: .init(x: 28.65685425, y: 28))
            p.addLine(to: .init(x: 5, y: 28))
            p.addCurve(to: .init(x: 2, y: 25), control1: .init(x: 3.34314575, y: 28), control2: .init(x: 2, y: 26.65685425))
            p.addLine(to: .init(x: 2, y: 8))
            p.addCurve(to: .init(x: 5, y: 5), control1: .init(x: 2, y: 6.34314575), control2: .init(x: 3.34314575, y: 5))
            p.closeSubpath()
            p.move(to: .init(x: 10, y: 11))
            p.addCurve(to: .init(x: 5, y: 16), control1: .init(x: 7.23857625, y: 11), control2: .init(x: 5, y: 13.23857625))
            p.addCurve(to: .init(x: 10, y: 21), control1: .init(x: 5, y: 18.76142375), control2: .init(x: 7.23857625, y: 21))
            p.addLine(to: .init(x: 22, y: 21))
            p.addCurve(to: .init(x: 27, y: 16), control1: .init(x: 24.76142375, y: 21), control2: .init(x: 27, y: 18.76142375))
            p.addCurve(to: .init(x: 22, y: 11), control1: .init(x: 27, y: 13.23857625), control2: .init(x: 24.76142375, y: 11))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 7.5, y: 13.5, width: 5, height: 5))
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 19.5, y: 13.5, width: 5, height: 5))
            context.addPath(p); context.fillPath(using: .winding)
        }
    }
    private static func LIGHT(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 28, y: 3))
            p.addCurve(to: .init(x: 7, y: 21), control1: .init(x: 9, y: 3), control2: .init(x: 3, y: 11))
            p.addLine(to: .init(x: 22, y: 9))
            p.addLine(to: .init(x: 10, y: 24))
            p.addCurve(to: .init(x: 28, y: 3), control1: .init(x: 21, y: 26), control2: .init(x: 27, y: 17))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 5, y: 29))
            p.addCurve(to: .init(x: 22, y: 9), control1: .init(x: 8, y: 21), control2: .init(x: 14, y: 15))
            context.addPath(p); context.setLineWidth(2.2); context.strokePath()
        }
    }
    private static func GUOFENG(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 2, y: 14))
            p.addQuadCurve(to: .init(x: 30, y: 14), control: .init(x: 16, y: -4))
            p.addLine(to: .init(x: 16, y: 27))
            p.closeSubpath()
            p.move(to: .init(x: 9, y: 15))
            p.addLine(to: .init(x: 14, y: 23))
            p.addLine(to: .init(x: 11, y: 13))
            p.closeSubpath()
            p.move(to: .init(x: 15, y: 11))
            p.addLine(to: .init(x: 15, y: 23))
            p.addLine(to: .init(x: 17, y: 23))
            p.addLine(to: .init(x: 17, y: 11))
            p.closeSubpath()
            p.move(to: .init(x: 23, y: 13))
            p.addLine(to: .init(x: 18, y: 23))
            p.addLine(to: .init(x: 25, y: 15))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
    }
    private static func FOLK(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 13, y: 13))
            p.addCurve(to: .init(x: 5, y: 16), control1: .init(x: 9, y: 8), control2: .init(x: 3, y: 12))
            p.addCurve(to: .init(x: 5, y: 27), control1: .init(x: 0, y: 18), control2: .init(x: 1, y: 24))
            p.addCurve(to: .init(x: 17, y: 24), control1: .init(x: 10, y: 31), control2: .init(x: 16, y: 28))
            p.addCurve(to: .init(x: 20, y: 16), control1: .init(x: 22, y: 25), control2: .init(x: 25, y: 19))
            p.addLine(to: .init(x: 27, y: 9))
            p.addLine(to: .init(x: 24, y: 6))
            p.closeSubpath()
            p.move(to: .init(x: 12, y: 16))
            p.addCurve(to: .init(x: 9, y: 19), control1: .init(x: 10.34314575, y: 16), control2: .init(x: 9, y: 17.34314575))
            p.addCurve(to: .init(x: 12, y: 22), control1: .init(x: 9, y: 20.65685425), control2: .init(x: 10.34314575, y: 22))
            p.addCurve(to: .init(x: 15, y: 19), control1: .init(x: 13.65685425, y: 22), control2: .init(x: 15, y: 20.65685425))
            p.addCurve(to: .init(x: 12, y: 16), control1: .init(x: 15, y: 17.34314575), control2: .init(x: 13.65685425, y: 16))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 24, y: 6))
            p.addLine(to: .init(x: 28, y: 2))
            p.addLine(to: .init(x: 30, y: 4))
            p.addLine(to: .init(x: 26, y: 8))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
    }
    private static func ACG(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 2, y: 28))
            p.addLine(to: .init(x: 12, y: 4))
            p.addLine(to: .init(x: 22, y: 4))
            p.addLine(to: .init(x: 27, y: 13))
            p.addLine(to: .init(x: 21, y: 13))
            p.addLine(to: .init(x: 18, y: 8))
            p.addLine(to: .init(x: 15, y: 8))
            p.addLine(to: .init(x: 8, y: 28))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 22, y: 17))
            p.addLine(to: .init(x: 15, y: 17))
            p.addLine(to: .init(x: 11, y: 28))
            p.addLine(to: .init(x: 18, y: 28))
            p.move(to: .init(x: 27, y: 17))
            p.addLine(to: .init(x: 23, y: 17))
            p.addLine(to: .init(x: 21, y: 28))
            p.addLine(to: .init(x: 29, y: 28))
            p.addLine(to: .init(x: 27, y: 22))
            p.addLine(to: .init(x: 24, y: 22))
            context.addPath(p); context.setLineWidth(2.4); context.strokePath()
        }
    }
    private static func GUDIAN(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 5, y: 15))
            p.addLine(to: .init(x: 27, y: 15))
            p.addCurve(to: .init(x: 30, y: 18), control1: .init(x: 28.65685425, y: 15), control2: .init(x: 30, y: 16.34314575))
            p.addLine(to: .init(x: 30, y: 23))
            p.addLine(to: .init(x: 2, y: 23))
            p.addLine(to: .init(x: 2, y: 18))
            p.addCurve(to: .init(x: 5, y: 15), control1: .init(x: 2, y: 16.34314575), control2: .init(x: 3.34314575, y: 15))
            p.closeSubpath()
            p.move(to: .init(x: 5, y: 18))
            p.addLine(to: .init(x: 5, y: 20))
            p.addLine(to: .init(x: 27, y: 20))
            p.addLine(to: .init(x: 27, y: 18))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 4, y: 15))
            p.addLine(to: .init(x: 23, y: 3))
            p.move(to: .init(x: 20, y: 5))
            p.addLine(to: .init(x: 26, y: 15))
            p.move(to: .init(x: 7, y: 24))
            p.addLine(to: .init(x: 7, y: 29))
            p.move(to: .init(x: 24, y: 24))
            p.addLine(to: .init(x: 24, y: 29))
            context.addPath(p); context.setLineWidth(2.7); context.strokePath()
        }
    }
    private static func JAZZ(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 24, y: 3))
            p.addCurve(to: .init(x: 16, y: 10), control1: .init(x: 19, y: 1), control2: .init(x: 16, y: 4))
            p.addLine(to: .init(x: 16, y: 21))
            p.addCurve(to: .init(x: 13, y: 24), control1: .init(x: 16, y: 22.65685425), control2: .init(x: 14.65685425, y: 24))
            p.addCurve(to: .init(x: 10, y: 21), control1: .init(x: 11.34314575, y: 24), control2: .init(x: 10, y: 22.65685425))
            p.addLine(to: .init(x: 10, y: 14))
            p.addLine(to: .init(x: 2, y: 14))
            p.addLine(to: .init(x: 5, y: 24))
            p.addCurve(to: .init(x: 22, y: 22), control1: .init(x: 7, y: 32), control2: .init(x: 21, y: 32))
            p.addLine(to: .init(x: 22, y: 10))
            p.addCurve(to: .init(x: 25, y: 9), control1: .init(x: 22, y: 8), control2: .init(x: 24, y: 8))
            p.addLine(to: .init(x: 25, y: 12))
            p.addLine(to: .init(x: 29, y: 12))
            p.addLine(to: .init(x: 29, y: 8))
            p.addCurve(to: .init(x: 24, y: 3), control1: .init(x: 29, y: 6), control2: .init(x: 27, y: 3))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
    }
    private static func JAPANESE(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 16, y: 13))
            p.addCurve(to: .init(x: 16, y: 5), control1: .init(x: 9, y: 7), control2: .init(x: 13, y: 0))
            p.addCurve(to: .init(x: 16, y: 13), control1: .init(x: 19, y: 0), control2: .init(x: 23, y: 7))
            p.closeSubpath()
            p.move(to: .init(x: 19, y: 15))
            p.addCurve(to: .init(x: 26, y: 12), control1: .init(x: 22, y: 6), control2: .init(x: 30, y: 8))
            p.addCurve(to: .init(x: 19, y: 15), control1: .init(x: 32, y: 13), control2: .init(x: 26, y: 20))
            p.closeSubpath()
            p.move(to: .init(x: 18, y: 19))
            p.addCurve(to: .init(x: 23, y: 25), control1: .init(x: 27, y: 19), control2: .init(x: 27, y: 28))
            p.addCurve(to: .init(x: 18, y: 19), control1: .init(x: 24, y: 31), control2: .init(x: 16, y: 28))
            p.closeSubpath()
            p.move(to: .init(x: 14, y: 19))
            p.addCurve(to: .init(x: 9, y: 25), control1: .init(x: 16, y: 28), control2: .init(x: 8, y: 31))
            p.addCurve(to: .init(x: 14, y: 19), control1: .init(x: 5, y: 28), control2: .init(x: 5, y: 19))
            p.closeSubpath()
            p.move(to: .init(x: 13, y: 15))
            p.addCurve(to: .init(x: 6, y: 12), control1: .init(x: 6, y: 20), control2: .init(x: 0, y: 13))
            p.addCurve(to: .init(x: 13, y: 15), control1: .init(x: 2, y: 8), control2: .init(x: 10, y: 6))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 14.5, y: 14.5, width: 3, height: 3))
            context.addPath(p); context.fillPath(using: .winding)
        }
    }
    private static func GLOBAL(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 4, y: 4, width: 24, height: 24))
            context.addPath(p); context.setLineWidth(2.5); context.strokePath()
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 9, y: 6))
            p.addLine(to: .init(x: 15, y: 4))
            p.addLine(to: .init(x: 16, y: 9))
            p.addLine(to: .init(x: 11, y: 12))
            p.addLine(to: .init(x: 12, y: 17))
            p.addLine(to: .init(x: 6, y: 15))
            p.addLine(to: .init(x: 5, y: 11))
            p.closeSubpath()
            p.move(to: .init(x: 17, y: 14))
            p.addLine(to: .init(x: 24, y: 15))
            p.addLine(to: .init(x: 27, y: 20))
            p.addLine(to: .init(x: 21, y: 27))
            p.addLine(to: .init(x: 18, y: 24))
            p.addLine(to: .init(x: 18, y: 20))
            p.addLine(to: .init(x: 14, y: 18))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
    }
    private static func FRANCH(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 15, y: 2))
            p.addLine(to: .init(x: 17, y: 2))
            p.addLine(to: .init(x: 18, y: 11))
            p.addLine(to: .init(x: 21, y: 19))
            p.addLine(to: .init(x: 30, y: 30))
            p.addLine(to: .init(x: 23, y: 30))
            p.addLine(to: .init(x: 19, y: 24))
            p.addLine(to: .init(x: 13, y: 24))
            p.addLine(to: .init(x: 9, y: 30))
            p.addLine(to: .init(x: 2, y: 30))
            p.addLine(to: .init(x: 11, y: 19))
            p.addLine(to: .init(x: 14, y: 11))
            p.closeSubpath()
            p.move(to: .init(x: 16, y: 11))
            p.addLine(to: .init(x: 14, y: 18))
            p.addLine(to: .init(x: 18, y: 18))
            p.closeSubpath()
            p.move(to: .init(x: 13, y: 20))
            p.addLine(to: .init(x: 12, y: 22))
            p.addLine(to: .init(x: 20, y: 22))
            p.addLine(to: .init(x: 19, y: 20))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 10, y: 19))
            p.addLine(to: .init(x: 22, y: 19))
            p.move(to: .init(x: 13, y: 11))
            p.addLine(to: .init(x: 19, y: 11))
            context.addPath(p); context.setLineWidth(1.8); context.strokePath()
        }
    }
    private static func BLUE(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 7, y: 5))
            p.addLine(to: .init(x: 25, y: 5))
            p.addLine(to: .init(x: 30, y: 14))
            p.addLine(to: .init(x: 30, y: 25))
            p.addLine(to: .init(x: 2, y: 25))
            p.addLine(to: .init(x: 2, y: 14))
            p.closeSubpath()
            p.move(to: .init(x: 8, y: 8))
            p.addLine(to: .init(x: 5, y: 14))
            p.addLine(to: .init(x: 27, y: 14))
            p.addLine(to: .init(x: 24, y: 8))
            p.closeSubpath()
            p.move(to: .init(x: 5, y: 17))
            p.addLine(to: .init(x: 5, y: 22))
            p.addLine(to: .init(x: 8, y: 22))
            p.addLine(to: .init(x: 8, y: 17))
            p.closeSubpath()
            p.move(to: .init(x: 10, y: 17))
            p.addLine(to: .init(x: 10, y: 22))
            p.addLine(to: .init(x: 13, y: 22))
            p.addLine(to: .init(x: 13, y: 17))
            p.closeSubpath()
            p.move(to: .init(x: 15, y: 17))
            p.addLine(to: .init(x: 15, y: 22))
            p.addLine(to: .init(x: 18, y: 22))
            p.addLine(to: .init(x: 18, y: 17))
            p.closeSubpath()
            p.move(to: .init(x: 20, y: 17))
            p.addLine(to: .init(x: 20, y: 22))
            p.addLine(to: .init(x: 23, y: 22))
            p.addLine(to: .init(x: 23, y: 17))
            p.closeSubpath()
            p.move(to: .init(x: 25, y: 17))
            p.addLine(to: .init(x: 25, y: 22))
            p.addLine(to: .init(x: 27, y: 22))
            p.addLine(to: .init(x: 27, y: 17))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
    }
    private static func DANCE(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.addEllipse(in: .init(x: 15, y: 2, width: 6, height: 6))
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 17, y: 11))
            p.addLine(to: .init(x: 14, y: 18))
            p.move(to: .init(x: 17, y: 11))
            p.addLine(to: .init(x: 10, y: 13))
            p.addLine(to: .init(x: 6, y: 9))
            p.move(to: .init(x: 17, y: 11))
            p.addLine(to: .init(x: 24, y: 6))
            p.addLine(to: .init(x: 26, y: 2))
            p.move(to: .init(x: 15, y: 24))
            p.addLine(to: .init(x: 13, y: 30))
            p.move(to: .init(x: 18, y: 24))
            p.addLine(to: .init(x: 22, y: 28))
            context.addPath(p); context.setLineWidth(2.8); context.strokePath()
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 14, y: 16))
            p.addLine(to: .init(x: 23, y: 23))
            p.addCurve(to: .init(x: 8, y: 22), control1: .init(x: 18, y: 27), control2: .init(x: 12, y: 26))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
    }
    private static func LATIN(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 12, y: 3))
            p.addLine(to: .init(x: 5, y: 22))
            p.addLine(to: .init(x: 25, y: 22))
            p.addLine(to: .init(x: 25, y: 19))
            p.addLine(to: .init(x: 10, y: 19))
            p.addLine(to: .init(x: 16, y: 3))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            context.saveGState()
            defer { context.restoreGState() }
            let local = context
            drawText("ATIN", font: UIFont(descriptor: UIFont.systemFont(ofSize: 8, weight: .heavy).fontDescriptor.withSymbolicTraits(.traitItalic)!, size: 8), centerX: 16, baseline: 30, in: local)
        }
    }
    private static func PUNK(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 3, y: 17))
            p.addLine(to: .init(x: 17, y: 10))
            p.addLine(to: .init(x: 20, y: 13))
            p.addLine(to: .init(x: 27, y: 3))
            p.addLine(to: .init(x: 30, y: 5))
            p.addLine(to: .init(x: 21, y: 15))
            p.addLine(to: .init(x: 17, y: 30))
            p.addLine(to: .init(x: 12, y: 20))
            p.closeSubpath()
            p.move(to: .init(x: 13, y: 15))
            p.addLine(to: .init(x: 18, y: 20))
            p.addLine(to: .init(x: 20, y: 15))
            p.addLine(to: .init(x: 17, y: 12))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
    }
    private static func COUNTRY(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 2, y: 13))
            p.addLine(to: .init(x: 13, y: 3))
            p.addLine(to: .init(x: 24, y: 13))
            p.addLine(to: .init(x: 21, y: 13))
            p.addLine(to: .init(x: 21, y: 29))
            p.addLine(to: .init(x: 5, y: 29))
            p.addLine(to: .init(x: 5, y: 13))
            p.closeSubpath()
            p.move(to: .init(x: 10, y: 19))
            p.addLine(to: .init(x: 10, y: 29))
            p.addLine(to: .init(x: 16, y: 29))
            p.addLine(to: .init(x: 16, y: 19))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 27, y: 9))
            p.addLine(to: .init(x: 23, y: 17))
            p.addLine(to: .init(x: 25, y: 17))
            p.addLine(to: .init(x: 22, y: 24))
            p.addLine(to: .init(x: 26, y: 24))
            p.addLine(to: .init(x: 26, y: 29))
            p.addLine(to: .init(x: 28, y: 29))
            p.addLine(to: .init(x: 28, y: 24))
            p.addLine(to: .init(x: 31, y: 24))
            p.addLine(to: .init(x: 28, y: 17))
            p.addLine(to: .init(x: 30, y: 17))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 2, y: 29))
            p.addLine(to: .init(x: 30, y: 29))
            context.addPath(p); context.setLineWidth(2); context.strokePath()
        }
    }
    private static func MUSICAL(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 3, y: 6))
            p.addQuadCurve(to: .init(x: 29, y: 6), control: .init(x: 16, y: 0))
            p.addLine(to: .init(x: 29, y: 29))
            p.addLine(to: .init(x: 25, y: 29))
            p.addCurve(to: .init(x: 19, y: 13), control1: .init(x: 24, y: 21), control2: .init(x: 22, y: 18))
            p.addCurve(to: .init(x: 25, y: 9), control1: .init(x: 22, y: 13), control2: .init(x: 24, y: 11))
            p.addLine(to: .init(x: 7, y: 9))
            p.addCurve(to: .init(x: 13, y: 13), control1: .init(x: 8, y: 11), control2: .init(x: 10, y: 13))
            p.addCurve(to: .init(x: 7, y: 29), control1: .init(x: 10, y: 18), control2: .init(x: 8, y: 21))
            p.addLine(to: .init(x: 3, y: 29))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 11, y: 29))
            p.addLine(to: .init(x: 21, y: 29))
            context.addPath(p); context.setLineWidth(2); context.strokePath()
        }
    }
    private static func YINGSHI(in context: CGContext) {
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 3, y: 14))
            p.addLine(to: .init(x: 29, y: 14))
            p.addLine(to: .init(x: 29, y: 27))
            p.addCurve(to: .init(x: 27, y: 29), control1: .init(x: 29, y: 28.1045695), control2: .init(x: 28.1045695, y: 29))
            p.addLine(to: .init(x: 5, y: 29))
            p.addCurve(to: .init(x: 3, y: 27), control1: .init(x: 3.8954305, y: 29), control2: .init(x: 3, y: 28.1045695))
            p.closeSubpath()
            p.move(to: .init(x: 13, y: 17))
            p.addLine(to: .init(x: 13, y: 25))
            p.addLine(to: .init(x: 20, y: 21))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .evenOdd)
        }
        do {
            let p = CGMutablePath()
            p.move(to: .init(x: 3, y: 6))
            p.addLine(to: .init(x: 8, y: 5))
            p.addLine(to: .init(x: 13, y: 11))
            p.addLine(to: .init(x: 5, y: 13))
            p.closeSubpath()
            p.move(to: .init(x: 11, y: 4))
            p.addLine(to: .init(x: 16, y: 3))
            p.addLine(to: .init(x: 21, y: 9))
            p.addLine(to: .init(x: 16, y: 10))
            p.closeSubpath()
            p.move(to: .init(x: 19, y: 3))
            p.addLine(to: .init(x: 27, y: 2))
            p.addLine(to: .init(x: 29, y: 8))
            p.addLine(to: .init(x: 24, y: 9))
            p.closeSubpath()
            context.addPath(p); context.fillPath(using: .winding)
        }
    }
}
#endif
