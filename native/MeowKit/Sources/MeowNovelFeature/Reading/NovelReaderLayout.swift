#if os(iOS)
import UIKit
import CoreText

public struct NovelReaderElement {
    public let blockIndex: Int
    public let range: NSRange?
    public let frame: CGRect
    public let imageOffset: CGFloat
    public let imageHeight: CGFloat
    let chapterStart: Double
    let chapterWeight: Double
    let chapterTotal: Double
    let text: NSAttributedString?
}
public struct NovelReaderPage {
    public var elements: [NovelReaderElement]
    public var height: CGFloat
}

/// Page geometry is disposable. UTF-16 text positions and image-relative
/// positions are the durable anchors across fonts, Safe Area and image arrival.
public enum NovelReaderLayout {
    public static func pages(content: NovelContent, settings: NovelReadingSettings, size: CGSize,
                             imageSizes: [Int: CGSize] = [:]) -> [NovelReaderPage] {
        let margin = CGFloat(settings.margin)
        let width = max(1, min(size.width - margin * 2, CGFloat(settings.contentWidth)))
        let left = (size.width - width) / 2
        let height = max(1, size.height)
        let scrolling = settings.mode == .scroll
        let palette = NovelReadingSettings.themes[min(max(0, settings.theme), 7)]
        let color = UIColor(novelHex: settings.foreground.isEmpty ? palette.foreground : settings.foreground)
        let font = font(settings: settings)
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineSpacing = settings.lineSpacing; paragraph.paragraphSpacing = settings.paragraphSpacing
        let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: paragraph]
        var pages: [NovelReaderPage] = [], elements: [NovelReaderElement] = [], y: CGFloat = 0
        let weights = content.blocks.map { Double($0.type == "image" ? 1 : max(1, ($0.text ?? "").utf16.count)) }
        let total = max(1, weights.reduce(0, +))
        var consumed: Double = 0
        func finish() {
            pages.append(.init(elements: elements, height: scrolling ? max(1, y) : height))
            elements = []; y = 0
        }
        for (blockIndex, block) in content.blocks.enumerated() {
            let startWeight = consumed, weight = weights[blockIndex]
            consumed += weight
            if block.type == "image" {
                let natural = imageSizes[blockIndex] ?? CGSize(width: width, height: width)
                let fullHeight = max(1, width * natural.height / max(1, natural.width))
                var offset: CGFloat = 0
                while offset < fullHeight {
                    if !scrolling, height - y < 1 { finish() }
                    let part = scrolling ? fullHeight : min(fullHeight - offset, height - y)
                    elements.append(.init(blockIndex: blockIndex, range: nil,
                        frame: CGRect(x: left, y: y, width: width, height: part), imageOffset: offset,
                        imageHeight: fullHeight, chapterStart: startWeight, chapterWeight: weight, chapterTotal: total, text: nil))
                    y += part; offset += part
                    if !scrolling, offset < fullHeight { finish() }
                }
                y += settings.paragraphSpacing
                continue
            }
            let value = NSAttributedString(string: block.text ?? "", attributes: attributes)
            let length = value.length
            guard length > 0 else { continue }
            let setter = CTFramesetterCreateWithAttributedString(value)
            var start = 0
            while start < length {
                if !scrolling, height - y < font.lineHeight + CGFloat(settings.lineSpacing) { finish() }
                let available = scrolling ? CGFloat.greatestFiniteMagnitude : height - y
                let remaining = CFRange(location: start, length: length - start)
                let suggested = CTFramesetterSuggestFrameSizeWithConstraints(setter, remaining, nil,
                    CGSize(width: width, height: available), nil)
                let frameHeight = min(available, max(font.lineHeight, ceil(suggested.height) + 1))
                let path = CGPath(rect: CGRect(x: 0, y: 0, width: width, height: frameHeight), transform: nil)
                let frame = CTFramesetterCreateFrame(setter, remaining, path, nil)
                let visible = CTFrameGetVisibleStringRange(frame).length
                guard visible > 0 else {
                    // An unusually large font still gets a visible line on one page.
                    let composed = (value.string as NSString).rangeOfComposedCharacterSequence(at: start)
                    let count = composed.length
                    elements.append(.init(blockIndex: blockIndex, range: NSRange(location: start, length: count),
                        frame: CGRect(x: left, y: y, width: width, height: frameHeight), imageOffset: 0, imageHeight: 0,
                        chapterStart: startWeight, chapterWeight: weight, chapterTotal: total,
                        text: value.attributedSubstring(from: NSRange(location: start, length: count))))
                    start += count; y += frameHeight
                    if !scrolling { finish() }; continue
                }
                elements.append(.init(blockIndex: blockIndex, range: NSRange(location: start, length: visible),
                    frame: CGRect(x: left, y: y, width: width, height: frameHeight), imageOffset: 0, imageHeight: 0,
                    chapterStart: startWeight, chapterWeight: weight, chapterTotal: total,
                    text: value.attributedSubstring(from: NSRange(location: start, length: visible))))
                start += visible; y += frameHeight
                if !scrolling, start < length { finish() }
            }
            y += settings.paragraphSpacing
        }
        if !elements.isEmpty || pages.isEmpty { finish() }
        return pages
    }
    static func font(settings: NovelReadingSettings) -> UIFont {
        guard !settings.fontName.isEmpty, let selected = UIFont(name: settings.fontName, size: settings.fontSize) else {
            return UIFont.systemFont(ofSize: settings.fontSize, weight: .init(rawValue: settings.fontWeight))
        }
        let descriptor = UIFontDescriptor(fontAttributes: [.family: selected.familyName,
            .traits: [UIFontDescriptor.TraitKey.weight: settings.fontWeight]])
        return UIFont(descriptor: descriptor, size: settings.fontSize)
    }
    public static func page(for anchor: NovelAnchor, pages: [NovelReaderPage]) -> Int {
        for (index, page) in pages.enumerated() {
            for element in page.elements where element.blockIndex == anchor.blockIndex {
                if let range = element.range, anchor.utf16Offset >= range.location, anchor.utf16Offset < NSMaxRange(range) { return index }
                if element.range == nil {
                    let position = CGFloat(imageRatio(anchor, element: element)) * element.imageHeight
                    if position >= element.imageOffset, position < element.imageOffset + element.frame.height { return index }
                }
            }
        }
        return min(max(0, Int(anchor.ratio * Double(max(0, pages.count - 1)))), max(0, pages.count - 1))
    }
    static func anchor(at y: CGFloat, page: NovelReaderPage, chapter: NovelChapter) -> NovelAnchor {
        let item = page.elements.first { $0.frame.maxY > y } ?? page.elements.last
        guard let item else { return .init(chapterUrl: chapter.chapterUrl, chapterTitle: chapter.name, chapterIndex: chapter.index) }
        var offset = item.range?.location ?? 0
        if let text = item.text, let range = item.range {
            let setter = CTFramesetterCreateWithAttributedString(text)
            let frame = CTFramesetterCreateFrame(setter, CFRange(), CGPath(rect: CGRect(origin: .zero, size: item.frame.size), transform: nil), nil)
            let lines = CTFrameGetLines(frame) as! [CTLine]
            var origins = Array(repeating: CGPoint.zero, count: lines.count)
            CTFrameGetLineOrigins(frame, CFRange(), &origins)
            let localY = max(0, y - item.frame.minY)
            if let index = origins.indices.first(where: {
                var descent: CGFloat = 0, leading: CGFloat = 0
                CTLineGetTypographicBounds(lines[$0], nil, &descent, &leading)
                return item.frame.height - origins[$0].y + descent + leading > localY
            }) ?? origins.indices.last {
                offset = range.location + CTLineGetStringRange(lines[index]).location
            }
        }
        return .init(chapterUrl: chapter.chapterUrl, chapterTitle: chapter.name, chapterIndex: chapter.index,
            blockIndex: item.blockIndex, utf16Offset: offset,
            ratio: min(1, max(0, (item.chapterStart + (item.range == nil ?
                Double(min(1, max(0, (item.imageOffset + y - item.frame.minY) / max(1, item.imageHeight)))) * item.chapterWeight :
                Double(offset))) / item.chapterTotal)))
    }
    static func y(for anchor: NovelAnchor, page: NovelReaderPage) -> CGFloat {
        for item in page.elements where item.blockIndex == anchor.blockIndex {
            if item.range == nil { return item.frame.minY + CGFloat(imageRatio(anchor, element: item)) * item.imageHeight - item.imageOffset }
            guard let range = item.range, anchor.utf16Offset >= range.location, anchor.utf16Offset <= NSMaxRange(range), let text = item.text else { continue }
            let setter = CTFramesetterCreateWithAttributedString(text)
            let frame = CTFramesetterCreateFrame(setter, CFRange(), CGPath(rect: CGRect(origin: .zero, size: item.frame.size), transform: nil), nil)
            let lines = CTFrameGetLines(frame) as! [CTLine]
            var origins = Array(repeating: CGPoint.zero, count: lines.count); CTFrameGetLineOrigins(frame, CFRange(), &origins)
            for (index, line) in lines.enumerated() {
                let lineRange = CTLineGetStringRange(line)
                if anchor.utf16Offset - range.location < lineRange.location + lineRange.length {
                    var ascent: CGFloat = 0
                    CTLineGetTypographicBounds(line, &ascent, nil, nil)
                    return max(0, item.frame.minY + item.frame.height - origins[index].y - ascent)
                }
            }
            return item.frame.minY
        }
        return 0
    }
    private static func imageRatio(_ anchor: NovelAnchor, element: NovelReaderElement) -> Double {
        min(1, max(0, (anchor.ratio * element.chapterTotal - element.chapterStart) / element.chapterWeight))
    }
}

@MainActor
final class NovelReaderCanvas: UIView {
    var page = NovelReaderPage(elements: [], height: 1) { didSet { setNeedsDisplay() } }
    var viewportY: CGFloat = 0 { didSet { setNeedsDisplay() } }
    var images: [Int: UIImage] = [:] { didSet { setNeedsDisplay() } }
    override func draw(_ rect: CGRect) {
        guard let context = UIGraphicsGetCurrentContext() else { return }
        context.translateBy(x: 0, y: -viewportY)
        let visible = rect.offsetBy(dx: 0, dy: viewportY)
        for item in page.elements where item.frame.intersects(visible) {
            if let text = item.text {
                context.saveGState()
                context.translateBy(x: item.frame.minX, y: item.frame.maxY)
                context.scaleBy(x: 1, y: -1); context.textMatrix = .identity
                let setter = CTFramesetterCreateWithAttributedString(text)
                let path = CGPath(rect: CGRect(origin: .zero, size: item.frame.size), transform: nil)
                CTFrameDraw(CTFramesetterCreateFrame(setter, CFRange(), path, nil), context)
                context.restoreGState()
            } else if let image = images[item.blockIndex] {
                context.saveGState(); context.clip(to: item.frame)
                image.draw(in: CGRect(x: item.frame.minX, y: item.frame.minY - item.imageOffset,
                    width: item.frame.width, height: item.imageHeight)); context.restoreGState()
            }
        }
    }
}
extension UIColor {
    convenience init(novelHex: String) {
        let text = novelHex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))
        let value = UInt64(text, radix: 16) ?? 0
        self.init(red: CGFloat((value >> 16) & 255) / 255, green: CGFloat((value >> 8) & 255) / 255,
                  blue: CGFloat(value & 255) / 255, alpha: 1)
    }
}
#endif
