// SPDX-License-Identifier: GPL-3.0-only
// Artwork adapted from iWebPlus starlight/disc.svg and generate-starlight-assets.mjs.
// Theme-colored default cover follows static/utils.js and app-colors.css.
#if os(iOS)
import UIKit
import MeowUI

@MainActor
private final class MusicRecordRotorView: UIView {
    private var theme = PageTheme()
    private var image: UIImage?

    private struct Fleck {
        let rect: CGRect
        let color: UIColor
    }

    private static let flecks: [Fleck] = {
        var seed: UInt32 = 0x51a711
        func random() -> Double {
            seed = seed &* 1_664_525 &+ 1_013_904_223
            return Double(seed) / 4_294_967_296
        }
        func rounded(_ value: Double) -> CGFloat { CGFloat((value * 100).rounded() / 100) }
        let colors: [UInt32] = [0xffffff, 0xffffff, 0xfffdf1, 0xeadcfe]
        return (0..<340).map { _ in
            let angle = random() * .pi * 2
            let radius = sqrt(300 * 300 + random() * (486 * 486 - 300 * 300))
            let size = rounded(0.65 + random() * 1.4)
            let color = UIColor(PageColor(rgb: colors[Int(random() * 4)]))
                .withAlphaComponent(rounded(0.28 + random() * 0.65))
            return Fleck(rect: CGRect(x: rounded(500 + cos(angle) * radius) - size,
                                      y: rounded(500 + sin(angle) * radius) - size,
                                      width: size * 2, height: size * 2), color: color)
        }
    }()

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .clear
        isOpaque = false
        isUserInteractionEnabled = false
        contentMode = .redraw
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(theme: PageTheme, image: UIImage?) {
        guard self.theme != theme || self.image !== image else { return }
        self.theme = theme
        self.image = image
        setNeedsDisplay()
    }

    override func draw(_ rect: CGRect) {
        guard bounds.width > 0, let context = UIGraphicsGetCurrentContext() else { return }
        context.saveGState()
        context.scaleBy(x: bounds.width / 1_000, y: bounds.height / 1_000)
        context.addEllipse(in: CGRect(x: 1, y: 1, width: 998, height: 998))
        context.addEllipse(in: CGRect(x: 220, y: 220, width: 560, height: 560))
        context.clip(using: .evenOdd)

        let glass = zip([UInt32(0xa0a9b4), 0xc1c8d0, 0xd4dce5, 0xe1e8ef, 0xf8ffff, 0xcfc4e8],
                        [CGFloat(0.12), 0.16, 0.15, 0.12, 0.17, 0.05])
            .map { UIColor(PageColor(rgb: $0)).withAlphaComponent($1).cgColor }
        if let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB),
                                     colors: glass as CFArray, locations: [0.53, 0.6, 0.76, 0.86, 0.96, 1]) {
            context.drawRadialGradient(gradient, startCenter: CGPoint(x: 500, y: 500), startRadius: 0,
                                       endCenter: CGPoint(x: 500, y: 500), endRadius: 499,
                                       options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
        }

        context.saveGState()
        context.addEllipse(in: CGRect(x: 4, y: 4, width: 992, height: 992))
        context.clip()
        let pearl = [UIColor.white.withAlphaComponent(0.24), UIColor.white.withAlphaComponent(0.04),
                     UIColor(PageColor(rgb: 0xd7c3ec)).withAlphaComponent(0.12),
                     UIColor.white.withAlphaComponent(0.20), UIColor.white.withAlphaComponent(0.03)]
        if let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB),
                                     colors: pearl.map(\.cgColor) as CFArray,
                                     locations: [0, 0.22, 0.53, 0.79, 1]) {
            context.drawLinearGradient(gradient, start: CGPoint(x: 4, y: 4),
                                       end: CGPoint(x: 996, y: 996), options: [])
        }
        context.restoreGState()

        let radii: [CGFloat] = [291, 305, 328, 349, 373, 400, 428, 456, 481, 498]
        for (index, radius) in radii.enumerated() {
            let colored = index.isMultiple(of: 3)
            let color = colored ? UIColor(PageColor(rgb: 0xc5b5db)).withAlphaComponent(0.22)
                : UIColor.white.withAlphaComponent(0.30)
            context.setStrokeColor(color.cgColor)
            context.setLineWidth(colored ? 1 : 1.5)
            context.strokeEllipse(in: CGRect(x: 500 - radius, y: 500 - radius,
                                             width: radius * 2, height: radius * 2))
        }
        for fleck in Self.flecks {
            context.setFillColor(fleck.color.cgColor)
            context.fillEllipse(in: fleck.rect)
        }
        context.restoreGState()
        drawCover(in: context)
    }

    private func drawCover(in context: CGContext) {
        let size = bounds.width * 0.548
        let cover = CGRect(x: (bounds.width - size) / 2, y: (bounds.height - size) / 2,
                           width: size, height: size)
        context.saveGState()
        context.addRect(bounds)
        context.addEllipse(in: cover)
        context.clip(using: .evenOdd)
        // A pearl separator, without an inner drop shadow or a dark colored spread.
        context.setFillColor(UIColor(PageColor(rgba: theme.dark ? 0xffffff22 : 0xffffff55)).cgColor)
        context.fillEllipse(in: cover.insetBy(dx: -10, dy: -10))
        context.setFillColor(UIColor(theme.top).withAlphaComponent(theme.dark ? 0.08 : 0.18).cgColor)
        context.fillEllipse(in: cover.insetBy(dx: -9, dy: -9))
        context.restoreGState()

        context.saveGState()
        context.addEllipse(in: cover)
        context.clip()
        if let image, image.size.width > 0, image.size.height > 0 {
            let scale = max(size / image.size.width, size / image.size.height)
            let width = image.size.width * scale, height = image.size.height * scale
            image.draw(in: CGRect(x: cover.midX - width / 2, y: cover.midY - height / 2,
                                  width: width, height: height))
        } else {
            drawDefaultCover(in: cover, context: context)
        }
        context.setStrokeColor(UIColor(PageColor(rgba: theme.dark ? 0xdfd0ed55 : 0xffffff99)).cgColor)
        context.setLineWidth(1)
        context.strokeEllipse(in: cover.insetBy(dx: 0.5, dy: 0.5))
        context.restoreGState()
    }

    private func drawDefaultCover(in cover: CGRect, context: CGContext) {
        context.saveGState()
        context.translateBy(x: cover.minX, y: cover.minY)
        context.scaleBy(x: cover.width / 320, y: cover.height / 320)
        let base = theme.dark ? theme.top.mixed(with: theme.accent, ratio: 0.23)
            : theme.top.mixed(with: PageColor(rgb: 0xffffff), ratio: 0.20)
        let body = theme.dark ? theme.top.mixed(with: theme.accent, ratio: 0.16)
            : theme.top.mixed(with: theme.accent, ratio: 0.08)
        let edge = theme.dark ? theme.bottom.mixed(with: theme.accent, ratio: 0.12)
            : theme.top.mixed(with: theme.accent, ratio: 0.28)
        let space = CGColorSpace(name: CGColorSpace.sRGB)
        if let gradient = CGGradient(colorsSpace: space,
                                     colors: [UIColor(base).cgColor, UIColor(body).cgColor,
                                              UIColor(edge).cgColor] as CFArray,
                                     locations: [0, 0.48, 1]) {
            // CSS 145deg gradient endpoints are the corner projections onto its axis.
            let angle: CGFloat = 145 * .pi / 180
            let dx = sin(angle), dy = -cos(angle)
            let extent = 160 * (abs(dx) + abs(dy))
            context.drawLinearGradient(gradient,
                                       start: CGPoint(x: 160 - dx * extent, y: 160 - dy * extent),
                                       end: CGPoint(x: 160 + dx * extent, y: 160 + dy * extent),
                                       options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
        }

        // Rasterize the native conic reflection only when artwork/theme/size changes.
        // Its pixels belong to the rotor, unlike the fixed outer lighting layer.
        let reflection = CAGradientLayer()
        reflection.frame = CGRect(x: 0, y: 0, width: 320, height: 320)
        reflection.type = .conic
        reflection.startPoint = CGPoint(x: 0.5, y: 0.5)
        let angle: CGFloat = -35 * .pi / 180
        reflection.endPoint = CGPoint(x: 0.5 + sin(angle) * 0.5, y: 0.5 - cos(angle) * 0.5)
        let clearWhite = UIColor.white.withAlphaComponent(0).cgColor
        reflection.colors = [
            clearWhite, UIColor.white.withAlphaComponent(0.32).cgColor, clearWhite,
            UIColor(theme.accent).withAlphaComponent(0.06).cgColor,
            clearWhite, UIColor.white.withAlphaComponent(0.24).cgColor, clearWhite, clearWhite
        ]
        reflection.locations = [0, 0.12, 0.27, 0.43, 0.56, 0.67, 0.82, 1]
        reflection.render(in: context)

        let light = [UIColor.white.withAlphaComponent(0.18).cgColor, clearWhite]
        if let gradient = CGGradient(colorsSpace: space, colors: light as CFArray,
                                     locations: [0, 1]) {
            let origin = CGPoint(x: 96, y: 64)
            context.drawRadialGradient(gradient, startCenter: origin, startRadius: 0,
                                       endCenter: origin, endRadius: 240,
                                       options: [.drawsAfterEndLocation])
        }
        // Separate the recorded bands with quiet run-outs, rather than drawing a
        // uniform target from the spindle to the edge. Thin paired edges catch light.
        let bands: [ClosedRange<CGFloat>] = [52...73, 80...101, 108...126, 134...149]
        for (index, band) in bands.enumerated() {
            for radius in stride(from: band.lowerBound, through: band.upperBound, by: 3) {
                let groove = CGRect(x: 160 - radius, y: 160 - radius,
                                    width: radius * 2, height: radius * 2)
                context.setStrokeColor(UIColor(theme.accent)
                    .withAlphaComponent(index.isMultiple(of: 2) ? 0.18 : 0.14).cgColor)
                context.setLineWidth(0.65)
                context.strokeEllipse(in: groove)
                context.setStrokeColor(UIColor.white.withAlphaComponent(0.30).cgColor)
                context.setLineWidth(0.55)
                context.strokeEllipse(in: groove.insetBy(dx: -0.7, dy: -0.7))
            }
        }

        // A small pressed hub retains the surface reflection; it is not an opaque label.
        let hub = [UIColor.white.withAlphaComponent(0.16).cgColor, clearWhite,
                   UIColor(theme.accent).withAlphaComponent(0.08).cgColor]
        if let gradient = CGGradient(colorsSpace: space, colors: hub as CFArray,
                                     locations: [0, 0.78, 1]) {
            context.drawRadialGradient(gradient, startCenter: CGPoint(x: 160, y: 160), startRadius: 0,
                                       endCenter: CGPoint(x: 160, y: 160), endRadius: 44, options: [])
        }
        for radius: CGFloat in [28, 44, 155] {
            let rim = CGRect(x: 160 - radius, y: 160 - radius, width: radius * 2, height: radius * 2)
            context.setStrokeColor(UIColor(theme.accent).withAlphaComponent(0.18).cgColor)
            context.setLineWidth(0.7)
            context.strokeEllipse(in: rim)
            context.setStrokeColor(UIColor.white.withAlphaComponent(0.50).cgColor)
            context.setLineWidth(0.8)
            context.strokeEllipse(in: rim.insetBy(dx: -0.8, dy: -0.8))
        }
        context.setFillColor(UIColor.white.withAlphaComponent(0.75).cgColor)
        context.fillEllipse(in: CGRect(x: 156, y: 156, width: 8, height: 8))
        context.restoreGState()
    }
}

@MainActor
final class MusicRecordDiscView: UIView {
    private let outerShadow = CALayer()
    private let shadowMask = CAShapeLayer()
    private let coverHost = MusicRecordRotorView()
    private let starHost = CALayer()
    private let sheen = CAGradientLayer()
    private let sheenMask = CALayer()
    private let starMask = CALayer()
    private var theme = PageTheme()
    private var rotating = false
    private var suspended = false
    private var rotationStartTime: CFTimeInterval?
    private var rotationStartAngle: CGFloat = 0
    private static let rotationDuration: CFTimeInterval = 32
    private var stars: [CAShapeLayer] = []

    override var isHidden: Bool {
        didSet { updateRotation() }
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .clear
        isOpaque = false
        isUserInteractionEnabled = false
        contentMode = .redraw
        layer.addSublayer(outerShadow)
        outerShadow.mask = shadowMask
        shadowMask.fillRule = .evenOdd
        addSubview(coverHost)
        coverHost.layer.addSublayer(starHost)
        layer.addSublayer(sheen)

        sheen.type = .conic
        sheen.startPoint = CGPoint(x: 0.5, y: 0.5)
        sheen.endPoint = CGPoint(x: 0.78, y: 0.08)
        sheen.mask = sheenMask
        starHost.mask = starMask

        for index in 0..<24 {
            let star = CAShapeLayer()
            let edge = CGFloat(3 + index % 4)
            let points = [CGPoint(x: 0.5, y: 0), CGPoint(x: 0.6, y: 0.39),
                          CGPoint(x: 1, y: 0.5), CGPoint(x: 0.6, y: 0.6),
                          CGPoint(x: 0.5, y: 1), CGPoint(x: 0.4, y: 0.6),
                          CGPoint(x: 0, y: 0.5), CGPoint(x: 0.4, y: 0.39)]
            let path = CGMutablePath()
            for (offset, point) in points.enumerated() {
                let position = CGPoint(x: point.x * edge, y: point.y * edge)
                if offset == 0 { path.move(to: position) } else { path.addLine(to: position) }
            }
            path.closeSubpath()
            star.path = path
            star.bounds = CGRect(x: 0, y: 0, width: edge, height: edge)
            star.fillColor = UIColor(PageColor(rgb: 0xffffef)).cgColor
            star.opacity = 0.55
            star.transform = CATransform3DMakeScale(0.8, 0.8, 1)
            let core = CAShapeLayer()
            core.frame = star.bounds
            core.path = UIBezierPath(ovalIn: star.bounds.insetBy(dx: edge * 0.23, dy: edge * 0.23)).cgPath
            core.fillColor = UIColor.white.withAlphaComponent(0.6).cgColor
            core.shadowColor = UIColor(PageColor(rgb: 0xffffdf)).cgColor
            core.shadowOpacity = 0.55
            core.shadowRadius = 2
            core.shadowOffset = .zero
            core.shadowPath = UIBezierPath(ovalIn: star.bounds.insetBy(dx: edge * 0.23 - 1,
                                                                      dy: edge * 0.23 - 1)).cgPath
            star.addSublayer(core)
            starHost.addSublayer(star)
            stars.append(star)
        }

        coverHost.layer.speed = 0
        NotificationCenter.default.addObserver(
            self, selector: #selector(suspendRotation),
            name: UIApplication.willResignActiveNotification, object: nil)
        NotificationCenter.default.addObserver(
            self, selector: #selector(resumeRotation),
            name: UIApplication.didBecomeActiveNotification, object: nil)
        configure(theme: theme, image: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(theme: PageTheme, image: UIImage?) {
        if self.theme != theme {
            self.theme = theme
            setNeedsDisplay()
        }
        let material = MusicRecordMaterials(palette: theme.palette)
        let reflect = UIColor(material.reflect)
        let sheenAngle = 36 * Double.pi / 180
        sheen.endPoint = CGPoint(x: 0.5 + CGFloat(sin(sheenAngle)) * 0.5,
                                 y: 0.5 - CGFloat(cos(sheenAngle)) * 0.5)
        let highlight = theme.dark ? UIColor(PageColor(rgba: 0xe8deff30))
            : UIColor.white.withAlphaComponent(0.20)
        // Preserve the highlight's RGB at transparent stops. Transparent black
        // introduces dark bands when Core Animation interpolates the color channels.
        let clearHighlight = highlight.withAlphaComponent(0).cgColor
        sheen.colors = theme.dark
            ? [clearHighlight, highlight.cgColor, clearHighlight,
               clearHighlight, highlight.cgColor, clearHighlight]
            : [clearHighlight, highlight.cgColor, reflect.cgColor, reflect.withAlphaComponent(0).cgColor,
               clearHighlight, highlight.cgColor, clearHighlight]
        sheen.locations = theme.dark ? [0.04, 0.12, 0.24, 0.51, 0.60, 0.70]
            : [0.04, 0.12, 0.18, 0.27, 0.51, 0.60, 0.72]
        let shadowRadius: CGFloat = theme.dark ? 16 : 12
        if outerShadow.shadowRadius != shadowRadius { setNeedsLayout() }
        outerShadow.shadowColor = UIColor(PageColor(rgba: theme.dark ? 0x00000022 : 0x9183ae0a)).cgColor
        outerShadow.shadowOpacity = 1
        outerShadow.shadowRadius = shadowRadius
        outerShadow.shadowOffset = CGSize(width: 0, height: 8)
        coverHost.configure(theme: theme, image: image)
    }

    func setRotating(_ value: Bool) {
        rotating = value
        updateRotation()
    }

    /// The owner calls this only after the outgoing disc has completely left.
    /// Entering, cancellation, artwork updates and visibility changes never reset it.
    func resetAfterDeparture() {
        rotating = false
        freezeRotation()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        coverHost.layer.transform = CATransform3DIdentity
        coverHost.layer.beginTime = 0
        coverHost.layer.timeOffset = 0
        CATransaction.commit()
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        updateRotation()
    }

    @objc private func suspendRotation() {
        suspended = true
        updateRotation()
    }

    @objc private func resumeRotation() {
        suspended = false
        updateRotation()
    }

    private func updateRotation() {
        guard rotating, window != nil, !isHidden, !suspended else {
            freezeRotation()
            return
        }
        let layer = coverHost.layer
        if rotationStartTime != nil {
            guard layer.animation(forKey: "record.rotation") == nil else { return }
            // If UIKit discarded the animation, its clock still identifies the phase.
            freezeRotation()
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        let offset = layer.timeOffset
        layer.speed = 1
        layer.timeOffset = 0
        layer.beginTime = 0
        layer.beginTime = layer.convertTime(CACurrentMediaTime(), from: nil) - offset
        let time = layer.convertTime(CACurrentMediaTime(), from: nil)
        rotationStartTime = time
        rotationStartAngle = atan2(layer.transform.m12, layer.transform.m11)
        let rotation = CABasicAnimation(keyPath: "transform.rotation.z")
        rotation.fromValue = rotationStartAngle
        rotation.toValue = rotationStartAngle + .pi * 2
        rotation.duration = Self.rotationDuration
        rotation.beginTime = time
        rotation.repeatCount = .infinity
        rotation.timingFunction = CAMediaTimingFunction(name: .linear)
        layer.add(rotation, forKey: "record.rotation")
        installSparkles(at: time)
        CATransaction.commit()
    }

    private func freezeRotation() {
        guard let startTime = rotationStartTime else { return }
        let layer = coverHost.layer
        let time = layer.convertTime(CACurrentMediaTime(), from: nil)
        let angle: CGFloat
        if layer.animation(forKey: "record.rotation") != nil, let visible = layer.presentation() {
            angle = atan2(visible.transform.m12, visible.transform.m11)
        } else {
            angle = rotationStartAngle + CGFloat((time - startTime) / Self.rotationDuration) * .pi * 2
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        // The held pose belongs to the model, so animation removal cannot expose zero.
        layer.speed = 0
        layer.timeOffset = time
        layer.transform = CATransform3DMakeRotation(angle, 0, 0, 1)
        layer.removeAnimation(forKey: "record.rotation")
        CATransaction.commit()
        rotationStartTime = nil
    }

    private func installSparkles(at time: CFTimeInterval) {
        for (index, star) in stars.enumerated() where star.animation(forKey: "record.sparkle") == nil {
            let scale = CAKeyframeAnimation(keyPath: "transform.scale")
            scale.values = [0.6, 1.5, 0.6]
            scale.keyTimes = [0, 0.48, 1]
            scale.timingFunctions = [.init(name: .easeInEaseOut), .init(name: .easeInEaseOut)]
            let opacity = CAKeyframeAnimation(keyPath: "opacity")
            opacity.values = [0.3, 0.95, 0.3]
            opacity.keyTimes = scale.keyTimes
            opacity.timingFunctions = scale.timingFunctions
            let shimmer = CAAnimationGroup()
            shimmer.animations = [scale, opacity]
            shimmer.duration = 3.4 + Double(index % 7) * 0.43
            shimmer.beginTime = time
            shimmer.timeOffset = time + Double(index) * 0.71
            shimmer.repeatCount = .infinity
            star.add(shimmer, forKey: "record.sparkle")
        }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        // A rotating view's frame is undefined; keep its untransformed geometry fixed.
        coverHost.bounds = CGRect(origin: .zero, size: bounds.size)
        coverHost.center = CGPoint(x: bounds.midX, y: bounds.midY)
        starHost.frame = bounds
        sheen.frame = bounds
        let maskChanged = sheenMask.bounds.size != bounds.size || sheenMask.contents == nil
        sheenMask.frame = bounds
        starMask.frame = bounds
        if maskChanged, bounds.width > 0, bounds.height > 0 {
            let mask = UIGraphicsImageRenderer(bounds: bounds).image { renderer in
                let context = renderer.cgContext
                context.addEllipse(in: bounds)
                context.clip()
                let colors = [UIColor.clear.cgColor, UIColor.black.cgColor]
                if let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB),
                                             colors: colors as CFArray, locations: [0.39, 0.40]) {
                    let center = CGPoint(x: bounds.midX, y: bounds.midY)
                    context.drawRadialGradient(gradient, startCenter: center, startRadius: 0,
                                               endCenter: center, endRadius: bounds.width / sqrt(2),
                                               options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
                }
            }
            sheenMask.contents = mask.cgImage
            starMask.contents = mask.cgImage
        }
        for (index, star) in stars.enumerated() {
            let angle = Double(index) * 2.399963 + 0.45
            let radius = (31.2 + Double(index * 47 % 160) / 10) / 100
            star.position = CGPoint(x: bounds.midX + bounds.width * cos(angle) * radius,
                                    y: bounds.midY + bounds.height * sin(angle) * radius)
        }
        // CSS outer box-shadow excludes the disc interior, even through transparent covers.
        let shadowPadding = outerShadow.shadowRadius * 3 + abs(outerShadow.shadowOffset.height)
        outerShadow.frame = bounds.insetBy(dx: -shadowPadding, dy: -shadowPadding)
        let discPath = UIBezierPath(ovalIn: outerShadow.bounds.insetBy(dx: shadowPadding, dy: shadowPadding))
        outerShadow.shadowPath = discPath.cgPath
        shadowMask.frame = outerShadow.bounds
        let outside = UIBezierPath(rect: outerShadow.bounds)
        outside.append(discPath)
        shadowMask.path = outside.cgPath
        CATransaction.commit()
    }

    override func draw(_ rect: CGRect) {
        guard let context = UIGraphicsGetCurrentContext(),
              let space = CGColorSpace(name: CGColorSpace.sRGB) else { return }
        let material = MusicRecordMaterials(palette: theme.palette)
        // Keep the reference colored glass. Lift only its inner edge toward the
        // page color, so removing the dark collar does not erase the whole disc.
        let ring = UIColor(theme.dark ? material.ring :
            PageColor(rgb: material.ring.mixed(with: theme.top, ratio: 0.55).rgb,
                      alpha: material.ring.alpha))
        let colors = [ring.withAlphaComponent(0), ring, UIColor(material.tone), UIColor(material.mist),
                      UIColor(material.mist).withAlphaComponent(0)].map(\.cgColor)
        guard let gradient = CGGradient(colorsSpace: space, colors: colors as CFArray,
                                        locations: [0.36, 0.43, 0.53, 0.63, 0.71]) else { return }
        let center = CGPoint(x: bounds.midX, y: bounds.midY)
        context.saveGState()
        context.addEllipse(in: bounds)
        let coverInset = bounds.width * (1 - 0.548) / 2
        context.addEllipse(in: bounds.insetBy(dx: coverInset, dy: coverInset))
        context.clip(using: .evenOdd)
        context.drawRadialGradient(gradient, startCenter: center, startRadius: 0,
                                   endCenter: center, endRadius: bounds.width / sqrt(2), options: [])
        context.setStrokeColor(UIColor(PageColor(rgba: theme.dark ? 0xcfc6e11c : 0xcfc6e12b)).cgColor)
        context.setLineWidth(1)
        context.strokeEllipse(in: bounds.insetBy(dx: 0.5, dy: 0.5))
        context.restoreGState()
    }
}
#endif
