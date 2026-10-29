#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicRecordDiscView: UIView {
    let coverHost = UIView()
    private let preview = MusicArtworkView(size: 0)
    private var colorway = PageColorway.lilac
    private var rotating = false
    private var stars: [CAShapeLayer] = []

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .clear
        isUserInteractionEnabled = false
        addSubview(coverHost)
        coverHost.addSubview(preview)
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
            star.fillColor = UIColor(white: 1, alpha: 0.8).cgColor
            let shimmer = CABasicAnimation(keyPath: "opacity")
            shimmer.fromValue = 0.3; shimmer.toValue = 0.95
            shimmer.autoreverses = true; shimmer.repeatCount = .infinity
            shimmer.duration = (3.4 + Double(index % 7) * 0.43) / 2
            shimmer.timeOffset = Double(index) * 0.71
            coverHost.layer.addSublayer(star)
            star.add(shimmer, forKey: "record.sparkle")
            stars.append(star)
        }
        let rotation = CABasicAnimation(keyPath: "transform.rotation.z")
        rotation.fromValue = 0; rotation.toValue = Double.pi * 2
        rotation.duration = 20; rotation.repeatCount = .infinity
        coverHost.layer.add(rotation, forKey: "record.rotation")
        coverHost.layer.speed = 0
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(theme: PageTheme, artwork: URL?, current: Bool) {
        if colorway != theme.palette { colorway = theme.palette; setNeedsDisplay() }
        preview.isHidden = current
        if !current { preview.load(artwork) }
    }

    func setRotating(_ value: Bool) {
        guard rotating != value else { return }
        let layer = coverHost.layer
        if value {
            let offset = layer.timeOffset
            layer.speed = 1; layer.timeOffset = 0; layer.beginTime = 0
            layer.beginTime = layer.convertTime(CACurrentMediaTime(), from: nil) - offset
        } else {
            let time = layer.convertTime(CACurrentMediaTime(), from: nil)
            layer.speed = 0; layer.timeOffset = time
        }
        rotating = value
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        coverHost.frame = bounds
        let size = bounds.width * 0.51
        preview.frame = CGRect(x: (bounds.width - size) / 2, y: (bounds.height - size) / 2,
                               width: size, height: size)
        preview.layer.cornerRadius = size / 2
        CATransaction.begin(); CATransaction.setDisableActions(true)
        for (index, star) in stars.enumerated() {
            let angle = Double(index) * 2.399963 + 0.45
            let radius = (31.2 + Double(index * 47 % 160) / 10) / 100
            star.position = CGPoint(x: bounds.midX + bounds.width * cos(angle) * radius,
                                    y: bounds.midY + bounds.height * sin(angle) * radius)
        }
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
        context.addEllipse(in: bounds); context.clip()
        context.drawRadialGradient(gradient, startCenter: center, startRadius: 0,
                                   endCenter: center, endRadius: bounds.width / sqrt(2), options: [])
        context.setStrokeColor(UIColor.white.withAlphaComponent(0.1).cgColor)
        context.setLineWidth(0.5)
        for index in 0..<22 {
            let inset = bounds.width * (0.02 + CGFloat(index) * 0.009)
            context.strokeEllipse(in: bounds.insetBy(dx: inset, dy: inset))
        }
        context.restoreGState()
    }
}

@MainActor
final class MusicTonearmView: UIView {
    private var playing = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .clear
        isUserInteractionEnabled = false
        layer.anchorPoint = CGPoint(x: 0.72, y: 0.08)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func setPlaying(_ value: Bool) {
        guard playing != value else { return }
        playing = value
        UIView.animate(withDuration: UIAccessibility.isReduceMotionEnabled ? 0 : 0.28) {
            self.transform = value ? .identity : CGAffineTransform(rotationAngle: -.pi / 7)
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
        context.setLineCap(.round)
        for (width, color) in [(CGFloat(8), UIColor(white: 0.9, alpha: 1)),
                               (CGFloat(6.5), UIColor(white: 0.98, alpha: 1)),
                               (CGFloat(1.2), UIColor.white)] {
            context.addPath(path); context.setLineWidth(x * width)
            context.setStrokeColor(color.cgColor); context.strokePath()
        }
        context.setFillColor(UIColor(white: 0.96, alpha: 1).cgColor)
        context.fill(CGRect(x: x * 43, y: y * 10, width: x * 30, height: y * 23))
        context.fill(CGRect(x: x * 51, y: y * 56, width: x * 15, height: y * 46))
        context.fill(CGRect(x: x * 18, y: y * 278, width: x * 17, height: y * 29))
    }
}
#endif
