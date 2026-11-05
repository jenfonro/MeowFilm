// SPDX-License-Identifier: GPL-3.0-only
// Geometry and colors adapted from iWebPlus static/themes/starlight/tonearm.svg.
#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicTonearmView: UIView {
    private static let rotationKey = "tonearm.rotation"
    private var playing = false
    private var dark = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .clear
        isOpaque = false
        isUserInteractionEnabled = false
        contentMode = .redraw
        layer.anchorPoint = CGPoint(x: 0.725, y: 0.075)
        layer.shadowOffset = CGSize(width: 3, height: 5)
        layer.shadowRadius = 4
        updateShadow()
        NotificationCenter.default.addObserver(
            self, selector: #selector(reduceMotionChanged),
            name: UIAccessibility.reduceMotionStatusDidChangeNotification, object: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(theme: PageTheme) {
        guard dark != theme.dark else { return }
        dark = theme.dark
        updateShadow()
        setNeedsDisplay()
    }

    func setPlaying(_ value: Bool) {
        let reduceMotion = UIAccessibility.isReduceMotionEnabled
        guard playing != value ||
                (reduceMotion && layer.animation(forKey: Self.rotationKey) != nil) else { return }
        let current = layer.presentation()?.transform ?? layer.transform
        let currentAngle = atan2(current.m12, current.m11)
        let targetAngle: CGFloat = value ? 10 * .pi / 180 : 0
        playing = value

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        layer.removeAnimation(forKey: Self.rotationKey)
        transform = CGAffineTransform(rotationAngle: targetAngle)
        if !reduceMotion {
            let animation = CABasicAnimation(keyPath: "transform.rotation.z")
            animation.fromValue = currentAngle
            animation.toValue = targetAngle
            animation.duration = 0.65
            animation.timingFunction = CAMediaTimingFunction(controlPoints: 0.2, 0.7, 0.25, 1)
            layer.add(animation, forKey: Self.rotationKey)
        }
        CATransaction.commit()
    }

    @objc private func reduceMotionChanged() {
        guard UIAccessibility.isReduceMotionEnabled else { return }
        setPlaying(playing)
    }

    private func updateShadow() {
        // starlight.css: drop-shadow(3px 5px 4px #7a728b18 / #0003).
        // Leave shadowPath unset so the complete artwork supplies its alpha silhouette.
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        layer.shadowColor = dark ? UIColor.black.cgColor : color(0x7a728b)
        layer.shadowOpacity = dark ? 0.2 : 24.0 / 255
        CATransaction.commit()
    }

    override func draw(_ rect: CGRect) {
        guard bounds.width > 0, bounds.height > 0,
              let context = UIGraphicsGetCurrentContext(),
              let space = CGColorSpace(name: CGColorSpace.sRGB),
              let pearl = CGGradient(
                colorsSpace: space,
                colors: [color(0xeee9db), color(0xf9fcf9), color(0xffffff),
                         color(0xeef1ed), color(0xe9e5d6)] as CFArray,
                locations: [0, 0.22, 0.53, 0.8, 1]),
              let cap = CGGradient(
                colorsSpace: space,
                colors: [color(0xffffff), color(0xecf0e9),
                         color(0xffffff), color(0xf4f5f3)] as CFArray,
                locations: [0, 0.3, 0.55, 1]) else { return }

        context.saveGState()
        context.translateBy(x: bounds.minX, y: bounds.minY)
        context.scaleBy(x: bounds.width / 80, y: bounds.height / 320)
        context.setMiterLimit(4)

        // tonearm.svg:11–12 — the two strokes share the original centerline.
        let arm = CGMutablePath()
        arm.move(to: CGPoint(x: 58, y: 24))
        arm.addLine(to: CGPoint(x: 58, y: 220))
        arm.addQuadCurve(to: CGPoint(x: 39, y: 275), control: CGPoint(x: 58, y: 251))
        arm.addLine(to: CGPoint(x: 27, y: 291))
        context.addPath(arm)
        context.setLineCap(.round)
        context.setLineJoin(.miter)
        context.setLineWidth(8)
        context.setStrokeColor(color(0xe9e4d6))
        context.strokePath()

        let metal = arm.copy(strokingWithWidth: 6.5, lineCap: .round,
                             lineJoin: .miter, miterLimit: 4)
        // SVG objectBoundingBox excludes the stroke: x = 27...58, not its outer edge.
        fill(metal, with: pearl, from: CGPoint(x: 27, y: 24),
             to: CGPoint(x: 58, y: 24), in: context)

        // tonearm.svg:13 — the highlight has its own, slightly offset curve.
        let highlight = CGMutablePath()
        highlight.move(to: CGPoint(x: 57, y: 25))
        highlight.addLine(to: CGPoint(x: 57, y: 220))
        highlight.addQuadCurve(to: CGPoint(x: 38, y: 274), control: CGPoint(x: 57, y: 250))
        highlight.addLine(to: CGPoint(x: 26, y: 290))
        context.addPath(highlight)
        context.setLineWidth(1.2)
        context.setStrokeColor(color(0xffffff, alpha: 0.9))
        context.strokePath()

        // tonearm.svg:14 — rectangular support, with the vertical cap gradient.
        let support = CGPath(roundedRect: CGRect(x: 43, y: 10, width: 30, height: 23),
                             cornerWidth: 3, cornerHeight: 3, transform: nil)
        fill(support, with: cap, from: CGPoint(x: 43, y: 10),
             to: CGPoint(x: 43, y: 33), in: context)
        context.addPath(support)
        context.setLineWidth(1)
        context.setStrokeColor(color(0xffffff, alpha: 0.6))
        context.strokePath()

        // tonearm.svg:15 — upper cap.
        let upperCap = CGPath(roundedRect: CGRect(x: 48, y: 2, width: 21, height: 10),
                              cornerWidth: 2, cornerHeight: 2, transform: nil)
        fill(upperCap, with: pearl, from: CGPoint(x: 48, y: 2),
             to: CGPoint(x: 69, y: 2), in: context)

        // tonearm.svg:16 — two separate, butt-capped grooves.
        context.move(to: CGPoint(x: 45, y: 18))
        context.addLine(to: CGPoint(x: 71, y: 18))
        context.move(to: CGPoint(x: 46, y: 21))
        context.addLine(to: CGPoint(x: 70, y: 21))
        context.setLineCap(.butt)
        context.setLineWidth(1)
        context.setStrokeColor(color(0xd9d5c4, alpha: 0.45))
        context.strokePath()

        // tonearm.svg:17 — arm sleeve.
        let sleeve = CGPath(roundedRect: CGRect(x: 51, y: 56, width: 15, height: 46),
                            cornerWidth: 3, cornerHeight: 3, transform: nil)
        fill(sleeve, with: pearl, from: CGPoint(x: 51, y: 56),
             to: CGPoint(x: 66, y: 56), in: context)
        context.addPath(sleeve)
        context.setLineWidth(1.2)
        context.setStrokeColor(color(0xffffff))
        context.strokePath()

        // tonearm.svg:18–20 — rotate both cartridge and stylus about (26, 289).
        context.saveGState()
        context.translateBy(x: 26, y: 289)
        context.rotate(by: 32 * .pi / 180)
        context.translateBy(x: -26, y: -289)
        let cartridge = CGPath(rect: CGRect(x: 18, y: 278, width: 17, height: 29),
                               transform: nil)
        fill(cartridge, with: pearl, from: CGPoint(x: 18, y: 278),
             to: CGPoint(x: 35, y: 278), in: context)
        context.addPath(cartridge)
        context.setLineJoin(.round)
        context.setLineWidth(1.3)
        context.setStrokeColor(color(0xffffff))
        context.strokePath()
        context.setFillColor(color(0xf1eee3))
        context.fill(CGRect(x: 23, y: 307, width: 7, height: 6))
        context.restoreGState()
        context.restoreGState()
    }

    private func fill(_ path: CGPath, with gradient: CGGradient, from start: CGPoint,
                      to end: CGPoint, in context: CGContext) {
        context.saveGState()
        context.addPath(path)
        context.clip()
        context.drawLinearGradient(gradient, start: start, end: end,
                                   options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
        context.restoreGState()
    }

    private func color(_ rgb: UInt32, alpha: CGFloat = 1) -> CGColor {
        // starlight.css:71 applies brightness(.94) to the artwork before its shadow.
        let brightness: CGFloat = dark ? 0.94 : 1
        return UIColor(red: CGFloat((rgb >> 16) & 0xff) / 255 * brightness,
                       green: CGFloat((rgb >> 8) & 0xff) / 255 * brightness,
                       blue: CGFloat(rgb & 0xff) / 255 * brightness, alpha: alpha).cgColor
    }
}
#endif
