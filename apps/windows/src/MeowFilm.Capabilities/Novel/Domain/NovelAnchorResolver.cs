using System.Globalization;

namespace MeowFilm.Capabilities;

internal enum NovelAnchorStrategy
{
    Exact,
    Unique,
    Ratio
}

/// <summary>
/// Relocates a reading anchor across content transforms and source switches, aligning
/// UTF-16 offsets strictly to Unicode extended grapheme cluster boundaries.
/// </summary>
internal static class NovelAnchorResolver
{
    internal static (NovelAnchor Anchor, NovelAnchorStrategy Strategy) Resolve(
        NovelAnchor? anchor,
        IReadOnlyList<NovelBlock>? previous,
        IReadOnlyList<NovelBlock> next,
        NovelChapter chapter)
    {
        if (anchor is null)
        {
            return (new NovelAnchor(chapter.ChapterUrl, chapter.Name, chapter.Index, 0, 0, 0), NovelAnchorStrategy.Ratio);
        }

        if (previous is not null &&
            anchor.BlockIndex >= 0 &&
            anchor.BlockIndex < previous.Count &&
            next.Count > 0)
        {
            var block = previous[anchor.BlockIndex];
            if (anchor.BlockIndex < next.Count && next[anchor.BlockIndex] == block)
            {
                double ratio = RelocatedRatio(anchor, previous, next, anchor.BlockIndex);
                int offset = block.Type == "text" ? AlignUtf16Offset(anchor.Utf16Offset, block.Text) : 0;
                return (new NovelAnchor(chapter.ChapterUrl, chapter.Name, chapter.Index, anchor.BlockIndex, offset, ratio),
                    NovelAnchorStrategy.Exact);
            }

            int matchIndex = -1;
            int matchCount = 0;
            for (int i = 0; i < next.Count; i++)
            {
                if (next[i] == block)
                {
                    matchIndex = i;
                    matchCount++;
                    if (matchCount > 1) break;
                }
            }

            if (matchCount == 1)
            {
                double ratio = RelocatedRatio(anchor, previous, next, matchIndex);
                int offset = block.Type == "text" ? AlignUtf16Offset(anchor.Utf16Offset, block.Text) : 0;
                return (new NovelAnchor(chapter.ChapterUrl, chapter.Name, chapter.Index, matchIndex, offset, ratio),
                    NovelAnchorStrategy.Unique);
            }
        }

        return (RatioAnchor(anchor.Ratio, next, chapter), NovelAnchorStrategy.Ratio);
    }

    internal static NovelAnchor RatioAnchor(double ratio, IReadOnlyList<NovelBlock> blocks, NovelChapter chapter)
    {
        double clamped = double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : 0;
        if (blocks.Count == 0)
        {
            return new NovelAnchor(chapter.ChapterUrl, chapter.Name, chapter.Index, 0, 0, clamped);
        }

        double total = 0;
        for (int i = 0; i < blocks.Count; i++) total += Weight(blocks[i]);
        double target = total * clamped;
        double consumed = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            double weight = Weight(blocks[i]);
            if (target < consumed + weight || i == blocks.Count - 1)
            {
                int offset = blocks[i].Type == "text"
                    ? AlignUtf16Offset(Math.Max(0, (int)(target - consumed)), blocks[i].Text)
                    : 0;
                return new NovelAnchor(chapter.ChapterUrl, chapter.Name, chapter.Index, i, offset, clamped);
            }
            consumed += weight;
        }

        return new NovelAnchor(chapter.ChapterUrl, chapter.Name, chapter.Index, 0, 0, clamped);
    }

    internal static int AlignUtf16Offset(int offset, string text)
    {
        if (string.IsNullOrEmpty(text) || offset <= 0) return 0;
        if (offset >= text.Length) return text.Length;
        var span = text.AsSpan();
        int index = 0;
        while (index < span.Length)
        {
            int length = StringInfo.GetNextTextElementLength(span[index..]);
            if (length <= 0) break;
            if (offset < index + length) return index;
            index += length;
        }
        return span.Length;
    }

    private static double Weight(NovelBlock block) =>
        block.Type == "image" ? 1 : Math.Max(1, block.Text.Length);

    private static double RelocatedRatio(
        NovelAnchor anchor,
        IReadOnlyList<NovelBlock> previous,
        IReadOnlyList<NovelBlock> next,
        int nextIndex)
    {
        double oldTotal = 0;
        double oldPrefix = 0;
        for (int i = 0; i < previous.Count; i++)
        {
            double w = Weight(previous[i]);
            if (i < anchor.BlockIndex) oldPrefix += w;
            oldTotal += w;
        }

        double newTotal = 0;
        double newPrefix = 0;
        for (int i = 0; i < next.Count; i++)
        {
            double w = Weight(next[i]);
            if (i < nextIndex) newPrefix += w;
            newTotal += w;
        }

        double anchorRatio = double.IsFinite(anchor.Ratio) ? Math.Clamp(anchor.Ratio, 0, 1) : 0;
        var block = previous[anchor.BlockIndex];
        double fraction = block.Type == "image"
            ? Math.Clamp(anchorRatio * oldTotal - oldPrefix, 0, 1)
            : AlignUtf16Offset(anchor.Utf16Offset, block.Text);
        return Math.Clamp((newPrefix + fraction) / Math.Max(1, newTotal), 0, 1);
    }
}
