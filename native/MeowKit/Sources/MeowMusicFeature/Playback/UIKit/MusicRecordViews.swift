#if os(iOS)
import UIKit
import MeowUI

@MainActor
private final class MusicRecordRotorView: UIView {
    private var palette = PageColorway.lilac

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .clear
        isOpaque = false
        isUserInteractionEnabled = false
        contentMode = .redraw
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(palette: PageColorway) {
        guard self.palette != palette else { return }
        self.palette = palette
        setNeedsDisplay()
    }

    override func draw(_ rect: CGRect) {
        guard bounds.width > 0, let context = UIGraphicsGetCurrentContext() else { return }
        let material = MusicRecordMaterials(palette: palette)
        context.saveGState()
        context.addEllipse(in: bounds)
        context.clip()

        let width = bounds.width
        for index in 0..<38 {
            let inset = width * (0.018 + CGFloat(index) * 0.0064)
            let alpha = index.isMultiple(of: 3) ? 0.13 : 0.07
            context.setStrokeColor(UIColor(material.ring).withAlphaComponent(alpha).cgColor)
            context.setLineWidth(index.isMultiple(of: 4) ? 0.7 : 0.45)
            context.strokeEllipse(in: bounds.insetBy(dx: inset, dy: inset))
        }

        for index in 0..<132 {
            let angle = CGFloat(index) * 2.399963 + CGFloat(index % 9) * 0.17
            let radius = width * (0.30 + CGFloat(index * 37 % 190) / 1_000)
            let edge = CGFloat(0.45 + Double(index % 5) * 0.16)
            let point = CGPoint(x: bounds.midX + cos(angle) * radius,
                                y: bounds.midY + sin(angle) * radius)
            let color = index.isMultiple(of: 4) ? UIColor(material.mist) : UIColor.white
            context.setFillColor(color.withAlphaComponent(0.12 + CGFloat(index % 6) * 0.025).cgColor)
            context.fillEllipse(in: CGRect(x: point.x - edge / 2, y: point.y - edge / 2,
                                           width: edge, height: edge))
        }
        context.restoreGState()
    }
}

@MainActor
final class MusicRecordDiscView: UIView {
    private let coverHost = MusicRecordRotorView()
    private let coverRing = UIView()
    private let preview = UIImageView()
    private let starHost = CALayer()
    private let sheen = CAGradientLayer()
    private let sheenMask = CAShapeLayer()
    private let starMask = CAShapeLayer()
    private var colorway = PageColorway.lilac
    private var rotating = false
    private var stars: [CAShapeLayer] = []

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .clear
        isUserInteractionEnabled = false
        contentMode = .redraw
        addSubview(coverHost)
        coverHost.addSubview(coverRing)
        coverHost.addSubview(preview)
        coverHost.layer.addSublayer(starHost)
        layer.addSublayer(sheen)

        preview.contentMode = .scaleAspectFill
        preview.clipsToBounds = true
        preview.backgroundColor = UIColor.white.withAlphaComponent(0.18)
        coverRing.isUserInteractionEnabled = false
        coverRing.layer.borderWidth = 1
        coverRing.layer.shadowColor = UIColor(red: 0.41, green: 0.33, blue: 0.47, alpha: 1).cgColor
        coverRing.layer.shadowOpacity = 0.14
        coverRing.layer.shadowRadius = 6
        coverRing.layer.shadowOffset = CGSize(width: 0, height: 3)

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
            star.fillColor = UIColor(red: 1, green: 1, blue: 0.94, alpha: 0.86).cgColor
            star.shadowColor = UIColor.white.cgColor
            star.shadowOpacity = 0.55
            star.shadowRadius = 2.5
            star.shadowOffset = .zero
            star.opacity = 0.55

            let scale = CAKeyframeAnimation(keyPath: "transform.scale")
            scale.values = [0.6, 1.5, 0.6]
            scale.keyTimes = [0, 0.48, 1]
            let opacity = CAKeyframeAnimation(keyPath: "opacity")
            opacity.values = [0.3, 0.95, 0.3]
            opacity.keyTimes = scale.keyTimes
            let shimmer = CAAnimationGroup()
            shimmer.animations = [scale, opacity]
            shimmer.duration = 3.4 + Double(index % 7) * 0.43
            shimmer.timeOffset = Double(index) * 0.71
            shimmer.repeatCount = .infinity
            shimmer.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            starHost.addSublayer(star)
            star.add(shimmer, forKey: "record.sparkle")
            stars.append(star)
        }

        let rotation = CABasicAnimation(keyPath: "transform.rotation.z")
        rotation.fromValue = 0
        rotation.toValue = Double.pi * 2
        rotation.duration = 32
        rotation.repeatCount = .infinity
        coverHost.layer.add(rotation, forKey: "record.rotation")
        coverHost.layer.speed = 0
        configure(theme: PageTheme(palette: colorway, dark: false), image: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(theme: PageTheme, image: UIImage?) {
        if colorway != theme.palette {
            colorway = theme.palette
            coverHost.configure(palette: theme.palette)
            setNeedsDisplay()
        }
        let material = MusicRecordMaterials(palette: theme.palette)
        let reflect = UIColor(material.reflect)
        let sheenAngle = 36 * Double.pi / 180
        sheen.endPoint = CGPoint(x: 0.5 + CGFloat(sin(sheenAngle)) * 0.5,
                                 y: 0.5 - CGFloat(cos(sheenAngle)) * 0.5)
        sheen.colors = [
            UIColor.clear.cgColor, UIColor.white.withAlphaComponent(0.20).cgColor,
            reflect.withAlphaComponent(0.25).cgColor, UIColor.clear.cgColor,
            UIColor.clear.cgColor, UIColor.white.withAlphaComponent(0.18).cgColor,
            UIColor.clear.cgColor, UIColor.clear.cgColor
        ]
        sheen.locations = [0, 0.08, 0.16, 0.27, 0.51, 0.60, 0.72, 1]
        coverRing.backgroundColor = UIColor(material.tone).withAlphaComponent(0.15)
        coverRing.layer.borderColor = UIColor.white.withAlphaComponent(0.34).cgColor
        preview.layer.borderColor = UIColor.white.withAlphaComponent(0.60).cgColor
        preview.image = image
    }

    func setRotating(_ value: Bool) {
        guard rotating != value else { return }
        let layer = coverHost.layer
        if value {
            let offset = layer.timeOffset
            layer.speed = 1
            layer.timeOffset = 0
            layer.beginTime = 0
            layer.beginTime = layer.convertTime(CACurrentMediaTime(), from: nil) - offset
        } else {
            let time = layer.convertTime(CACurrentMediaTime(), from: nil)
            layer.speed = 0
            layer.timeOffset = time
        }
        rotating = value
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        coverHost.frame = bounds
        coverHost.layer.cornerRadius = bounds.width / 2
        coverHost.layer.masksToBounds = true
        let size = bounds.width * 0.548
        let ring = min(9, max(7, bounds.width * 0.028))
        preview.frame = CGRect(x: (bounds.width - size) / 2, y: (bounds.height - size) / 2,
                               width: size, height: size)
        preview.layer.cornerRadius = size / 2
        preview.layer.borderWidth = 1
        coverRing.frame = preview.frame.insetBy(dx: -ring, dy: -ring)
        coverRing.layer.cornerRadius = coverRing.bounds.width / 2
        coverRing.layer.shadowPath = UIBezierPath(ovalIn: coverRing.bounds).cgPath

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        starHost.frame = bounds
        sheen.frame = bounds
        sheenMask.frame = bounds
        starMask.frame = bounds
        let materialInset = bounds.width * 0.11
        let sheenPath = UIBezierPath(ovalIn: bounds)
        sheenPath.append(UIBezierPath(ovalIn: bounds.insetBy(dx: materialInset, dy: materialInset)))
        sheenMask.fillRule = .evenOdd
        sheenMask.path = sheenPath.cgPath
        sheenMask.fillColor = UIColor.black.cgColor
        let starPath = UIBezierPath(ovalIn: bounds)
        starPath.append(UIBezierPath(ovalIn: bounds.insetBy(dx: materialInset, dy: materialInset)))
        starMask.fillRule = .evenOdd
        starMask.path = starPath.cgPath
        starMask.fillColor = UIColor.black.cgColor
        for (index, star) in stars.enumerated() {
            let angle = Double(index) * 2.399963 + 0.45
            let radius = (31.2 + Double(index * 47 % 160) / 10) / 100
            star.position = CGPoint(x: bounds.midX + bounds.width * cos(angle) * radius,
                                    y: bounds.midY + bounds.height * sin(angle) * radius)
        }
        layer.shadowPath = UIBezierPath(ovalIn: bounds).cgPath
        CATransaction.commit()
    }

    override func draw(_ rect: CGRect) {
        guard let context = UIGraphicsGetCurrentContext(),
              let space = CGColorSpace(name: CGColorSpace.sRGB) else { return }
        let material = MusicRecordMaterials(palette: colorway)
        let colors = [UIColor.clear, UIColor(material.ring), UIColor(material.tone),
                      UIColor(material.mist), UIColor.clear].map(\.cgColor)
        guard let gradient = CGGradient(colorsSpace: space, colors: colors as CFArray,
                                        locations: [0.36, 0.41, 0.53, 0.63, 0.71]) else { return }
        let center = CGPoint(x: bounds.midX, y: bounds.midY)
        context.saveGState()
        context.addEllipse(in: bounds)
        context.clip()
        context.drawRadialGradient(gradient, startCenter: center, startRadius: 0,
                                   endCenter: center, endRadius: bounds.width / sqrt(2), options: [])
        context.setStrokeColor(UIColor.white.withAlphaComponent(0.10).cgColor)
        context.setLineWidth(0.7)
        context.strokeEllipse(in: bounds.insetBy(dx: 0.5, dy: 0.5))
        context.restoreGState()
        layer.shadowColor = UIColor(red: 0.57, green: 0.51, blue: 0.68, alpha: 1).cgColor
        layer.shadowOpacity = 0.06
        layer.shadowRadius = 12
        layer.shadowOffset = CGSize(width: 0, height: 6)
    }
}

@MainActor
final class MusicTonearmView: UIView {
    private var playing = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .clear
        isUserInteractionEnabled = false
        contentMode = .redraw
        layer.anchorPoint = CGPoint(x: 0.725, y: 0.075)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func setPlaying(_ value: Bool) {
        guard playing != value else { return }
        playing = value
        let transform = value ? CGAffineTransform(rotationAngle: 10 * .pi / 180) : .identity
        guard !UIAccessibility.isReduceMotionEnabled else {
            self.transform = transform
            return
        }
        UIView.animate(withDuration: 0.65, delay: 0,
                       options: [.beginFromCurrentState, .allowUserInteraction, .curveEaseInOut]) {
            self.transform = transform
        }
    }

    override func draw(_ rect: CGRect) {
        guard let context = UIGraphicsGetCurrentContext() else { return }
        let x = bounds.width / 80, y = bounds.height / 320
        let path = CGMutablePath()
        path.move(to: CGPoint(x: x * 58, y: y * 24))
        path.addLine(to: CGPoint(x: x * 58, y: y * 220))
        path.addQuadCurve(to: CGPoint(x: x * 39, y: y * 275),
                          control: CGPoint(x: x * 58, y: y * 251))
        path.addLine(to: CGPoint(x: x * 27, y: y * 291))

        context.saveGState()
        context.translateBy(x: x * 2.5, y: y * 4)
        context.addPath(path)
        context.setLineCap(.round)
        context.setLineWidth(x * 10)
        context.setStrokeColor(UIColor(white: 0.35, alpha: 0.10).cgColor)
        context.strokePath()
        context.restoreGState()

        context.setLineCap(.round)
        for (width, color) in [
            (CGFloat(9), UIColor(red: 0.80, green: 0.78, blue: 0.84, alpha: 1)),
            (CGFloat(7), UIColor(red: 0.96, green: 0.95, blue: 0.98, alpha: 1)),
            (CGFloat(2), UIColor.white.withAlphaComponent(0.92))
        ] {
            context.addPath(path)
            context.setLineWidth(x * width)
            context.setStrokeColor(color.cgColor)
            context.strokePath()
        }

        let pivot = CGPoint(x: x * 58, y: y * 24)
        context.setFillColor(UIColor(red: 0.90, green: 0.88, blue: 0.93, alpha: 1).cgColor)
        context.fillEllipse(in: CGRect(x: pivot.x - x * 15, y: pivot.y - x * 15,
                                       width: x * 30, height: x * 30))
        context.setStrokeColor(UIColor.white.withAlphaComponent(0.86).cgColor)
        context.setLineWidth(x * 2)
        context.strokeEllipse(in: CGRect(x: pivot.x - x * 11, y: pivot.y - x * 11,
                                         width: x * 22, height: x * 22))
        UIColor(white: 0.98, alpha: 1).setFill()
        UIBezierPath(roundedRect: CGRect(x: x * 50.5, y: y * 56, width: x * 15, height: y * 46),
                     cornerRadius: x * 4).fill()

        context.saveGState()
        context.translateBy(x: x * 27, y: y * 291)
        context.rotate(by: -0.55)
        let head = UIBezierPath(roundedRect: CGRect(x: -x * 10, y: -y * 10, width: x * 22, height: y * 31),
                                cornerRadius: x * 3)
        UIColor(red: 0.91, green: 0.89, blue: 0.94, alpha: 1).setFill()
        head.fill()
        UIColor.white.withAlphaComponent(0.88).setStroke()
        head.lineWidth = x
        head.stroke()
        UIColor(white: 0.45, alpha: 0.7).setStroke()
        context.setLineWidth(x * 1.2)
        context.move(to: CGPoint(x: 0, y: y * 20))
        context.addLine(to: CGPoint(x: 0, y: y * 31))
        context.strokePath()
        context.restoreGState()
    }
}
#endif
