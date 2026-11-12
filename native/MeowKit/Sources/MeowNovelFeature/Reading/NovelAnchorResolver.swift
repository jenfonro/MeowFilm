import Foundation

/// Content-changing transforms can split or remove text runs. An offset is
/// reused only if the pointed-to block still matches; otherwise locate the
/// unique unchanged block, then fall back to the chapter's relative position.
public enum NovelAnchorResolver {
    public static func resolve(_ anchor: NovelAnchor?, previous: NovelContent?, next: NovelContent) -> NovelAnchor? {
        guard var anchor else { return nil }
        guard let previous, previous.blocks.indices.contains(anchor.blockIndex), !next.blocks.isEmpty else {
            return ratioAnchor(anchor, blocks: next.blocks)
        }
        let block = previous.blocks[anchor.blockIndex]
        if next.blocks.indices.contains(anchor.blockIndex), next.blocks[anchor.blockIndex] == block {
            anchor.ratio = relocatedRatio(anchor, previous: previous.blocks, next: next.blocks, nextIndex: anchor.blockIndex)
            anchor.utf16Offset = composedOffset(anchor.utf16Offset, text: block.text ?? ""); return anchor
        }
        let matches = next.blocks.indices.filter { next.blocks[$0] == block }
        if matches.count == 1 {
            anchor.ratio = relocatedRatio(anchor, previous: previous.blocks, next: next.blocks, nextIndex: matches[0])
            anchor.blockIndex = matches[0]; anchor.utf16Offset = composedOffset(anchor.utf16Offset, text: block.text ?? ""); return anchor
        }
        return ratioAnchor(anchor, blocks: next.blocks)
    }
    private static func relocatedRatio(_ anchor: NovelAnchor, previous: [NovelBlock], next: [NovelBlock], nextIndex: Int) -> Double {
        func weights(_ blocks: [NovelBlock]) -> [Double] { blocks.map { Double($0.type == "image" ? 1 : max(1, ($0.text ?? "").utf16.count)) } }
        let old = weights(previous), new = weights(next)
        let fraction = previous[anchor.blockIndex].type == "image" ?
            min(1, max(0, anchor.ratio * old.reduce(0, +) - old.prefix(anchor.blockIndex).reduce(0, +))) :
            Double(composedOffset(anchor.utf16Offset, text: previous[anchor.blockIndex].text ?? ""))
        return (new.prefix(nextIndex).reduce(0, +) + fraction) / max(1, new.reduce(0, +))
    }
    private static func ratioAnchor(_ anchor: NovelAnchor, blocks: [NovelBlock]) -> NovelAnchor {
        var anchor = anchor
        guard !blocks.isEmpty else { anchor.blockIndex = 0; anchor.utf16Offset = 0; return anchor }
        let weights = blocks.map { $0.type == "image" ? 1 : max(1, ($0.text ?? "").utf16.count) }
        let target = Double(weights.reduce(0, +)) * min(1, max(0, anchor.ratio))
        var consumed = 0
        for (index, weight) in weights.enumerated() {
            if target < Double(consumed + weight) || index == blocks.count - 1 {
                anchor.blockIndex = index
                anchor.utf16Offset = composedOffset(max(0, Int(target) - consumed), text: blocks[index].text ?? "")
                return anchor
            }
            consumed += weight
        }
        return anchor
    }
    private static func composedOffset(_ offset: Int, text: String) -> Int {
        let value = text as NSString, clamped = min(max(0, offset), value.length)
        guard clamped < value.length else { return clamped }
        return value.rangeOfComposedCharacterSequence(at: clamped).location
    }
}
