//
//  AppKitExtend.swift
//  KSPlayer
//
//  Created by kintan on 2018/3/9.
//

/// 'NSWindow' is unavailable in Mac Catalyst
#if !canImport(UIKit)
import AppKit
import CoreMedia

public typealias UIViewController = NSViewController
public typealias UIColor = NSColor
public typealias UIViewContentMode = ContentMode
public typealias UIFont = NSFont
public typealias UIFontDescriptor = NSFontDescriptor
public typealias UIImage = NSImage
public typealias UIImageView = NSImageView
public typealias UITouch = NSTouch
public typealias UIEvent = NSEvent
public extension UIFontDescriptor.SymbolicTraits {
    static var traitItalic = italic
    static var traitBold = bold
}

extension NSScreen {
    var scale: CGFloat {
        backingScaleFactor
    }
}

public extension NSView {
    @objc internal var contentMode: UIViewContentMode {
        get {
            if let contentsGravity = backingLayer?.contentsGravity {
                switch contentsGravity {
                case .resize:
                    return .scaleToFill
                case .resizeAspect:
                    return .scaleAspectFit
                case .resizeAspectFill:
                    return .scaleAspectFill
                default:
                    return .scaleAspectFit
                }
            } else {
                return .scaleAspectFit
            }
        }
        set {
            switch newValue {
            case .scaleToFill:
                backingLayer?.contentsGravity = .resize
            case .scaleAspectFit:
                backingLayer?.contentsGravity = .resizeAspect
            case .scaleAspectFill:
                backingLayer?.contentsGravity = .resizeAspectFill
            case .center:
                backingLayer?.contentsGravity = .center
            default:
                break
            }
        }
    }

    var center: CGPoint {
        CGPoint(x: frame.midX, y: frame.midY)
    }

    var alpha: CGFloat {
        get {
            alphaValue
        }
        set {
            alphaValue = newValue
        }
    }

    var clipsToBounds: Bool {
        get {
            if let layer {
                return layer.masksToBounds
            } else {
                return false
            }
        }
        set {
            backingLayer?.masksToBounds = newValue
        }
    }

    class func animate(withDuration duration: TimeInterval, animations: @escaping () -> Void, completion: ((Bool) -> Void)? = nil) {
        CATransaction.begin()
        CATransaction.setAnimationDuration(duration)
        CATransaction.setCompletionBlock {
            completion?(true)
        }
        animations()
        CATransaction.commit()
    }

    class func animate(withDuration duration: TimeInterval, animations: @escaping () -> Void) {
        animate(withDuration: duration, animations: animations, completion: nil)
    }

    func layoutIfNeeded() {
        layer?.layoutIfNeeded()
    }

    func centerRotate(byDegrees: Double) {
        layer?.position = center
        layer?.anchorPoint = CGPoint(x: 0.5, y: 0.5)
        layer?.setAffineTransform(CGAffineTransform(rotationAngle: CGFloat(Double.pi * byDegrees / 180.0)))
    }
}

public extension NSImage {
    convenience init(cgImage: CGImage) {
        self.init(cgImage: cgImage, size: NSSize.zero)
    }

    @available(macOS 11.0, *)
    convenience init?(systemName: String) {
        self.init(systemSymbolName: systemName, accessibilityDescription: nil)
    }
}

@objc public enum ContentMode: Int {
    case scaleToFill

    case scaleAspectFit // contents scaled to fit with fixed aspect. remainder is transparent

    case scaleAspectFill // contents scaled to fill with fixed aspect. some portion of content may be clipped.

    case redraw // redraw on bounds change (calls -setNeedsDisplay)

    case center // contents remain same size. positioned adjusted.

    case top

    case bottom

    case left

    case right

    case topLeft

    case topRight

    case bottomLeft

    case bottomRight
}

#endif
