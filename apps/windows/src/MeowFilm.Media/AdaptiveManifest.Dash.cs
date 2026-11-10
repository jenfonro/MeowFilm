using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MeowFilm.Media;

internal sealed partial class AdaptiveManifest
{
    private static readonly XNamespace DashNamespace = "urn:mpeg:dash:schema:mpd:2011";
    private string Dash(string text, Uri parent)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumBytes, MaxCharactersFromEntities = 0,
                IgnoreComments = true, IgnoreProcessingInstructions = false
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException) { throw Invalid(); }
        var mpd = document.Root;
        if (mpd is null || mpd.Name != DashNamespace + "MPD") throw Unsupported();
        if (mpd.Descendants().Any(e => e.Name.LocalName == "ContentProtection"))
            throw new PlaybackException(PlaybackError.AdaptiveDrmUnsupported);
        Shape(mpd);
        if (Attr(mpd, "type", "static") != "static") throw Unsupported();
        var periods = mpd.Elements(DashNamespace + "Period").ToArray();
        if (periods.Length != 1) throw Unsupported();
        var period = periods[0];
        if (Attr(period, "start", "PT0S") != "PT0S") throw Unsupported();
        double seconds = Duration(Attr(period, "duration", Attr(mpd, "mediaPresentationDuration", "")));
        var rootBase = Base(mpd, parent);
        var periodBase = Base(period, rootBase);
        var output = new XElement(DashNamespace + "MPD",
            new XAttribute("type", "static"),
            new XAttribute("profiles", "urn:mpeg:dash:profile:isoff-main:2011"),
            new XAttribute("minBufferTime", "PT1S"),
            new XAttribute("mediaPresentationDuration", XmlConvert.ToString(TimeSpan.FromSeconds(seconds))));
        var outputPeriod = new XElement(DashNamespace + "Period", new XAttribute("start", "PT0S"),
            new XAttribute("duration", XmlConvert.ToString(TimeSpan.FromSeconds(seconds))));
        output.Add(outputPeriod);
        int sets = 0, tracks = 0;
        foreach (var set in period.Elements(DashNamespace + "AdaptationSet"))
        {
            if (++sets > 16) throw new PlaybackException(PlaybackError.AdaptiveBudget);
            var setBase = Base(set, periodBase);
            var outputSet = new XElement(DashNamespace + "AdaptationSet", new XAttribute("id", sets));
            CopyPresentationAttributes(set, outputSet);
            outputPeriod.Add(outputSet);
            int representations = 0;
            foreach (var rep in set.Elements(DashNamespace + "Representation"))
            {
                if (++representations > 16 || ++tracks > 64) throw new PlaybackException(PlaybackError.AdaptiveBudget);
                string mime = Attr(rep, "mimeType", Attr(set, "mimeType", ""));
                if (mime is not ("audio/mp4" or "video/mp4")) throw Unsupported();
                string id = Attr(rep, "id", "");
                if (id.Length == 0 || id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
                    throw Invalid();
                long bandwidth = UInt(Attr(rep, "bandwidth", ""), 1_000_000_000);
                if (bandwidth == 0) throw Invalid();
                var repBase = Base(rep, setBase);
                var outputRep = new XElement(DashNamespace + "Representation",
                    new XAttribute("id", tracks), new XAttribute("bandwidth", bandwidth), new XAttribute("mimeType", mime));
                CopyPresentationAttributes(rep, outputRep);
                outputSet.Add(outputRep);
                var descriptor = SegmentDescriptor(rep) ?? SegmentDescriptor(set) ?? SegmentDescriptor(period);
                if (descriptor is null) throw Unsupported();
                long scale = UInt(Attr(descriptor, "timescale", "1"), 1_000_000_000);
                if (scale == 0 || Attr(descriptor, "presentationTimeOffset", "0") != "0") throw Unsupported();
                var list = new XElement(DashNamespace + "SegmentList", new XAttribute("timescale", scale));
                outputRep.Add(list);
                if (descriptor.Name.LocalName == "SegmentList")
                {
                    var init = descriptor.Element(DashNamespace + "Initialization") ?? throw Unsupported();
                    list.Add(new XElement(DashNamespace + "Initialization", new XAttribute("sourceURL",
                        Resource(repBase, Attr(init, "sourceURL", ""),
                            AdaptiveResourceKind.Mp4Init, DashRange((string?)init.Attribute("range"))))));
                    var urls = descriptor.Elements(DashNamespace + "SegmentURL").ToArray();
                    if (urls.Length is 0 or > 4096) throw new PlaybackException(PlaybackError.AdaptiveBudget);
                    var timeline = Timeline(descriptor, scale, seconds, urls.Length);
                    list.Add(TimelineElement(timeline));
                    foreach (var url in urls)
                        list.Add(new XElement(DashNamespace + "SegmentURL", new XAttribute("media",
                            Resource(repBase, Attr(url, "media", ""),
                                AdaptiveResourceKind.Mp4Segment, DashRange((string?)url.Attribute("mediaRange"))))));
                }
                else
                {
                    long number = UInt(Attr(descriptor, "startNumber", "1"), 1L << 52);
                    string initialization = Attr(descriptor, "initialization", "");
                    string template = Attr(descriptor, "media", "");
                    var timeline = Timeline(descriptor, scale, seconds, null);
                    list.Add(new XElement(DashNamespace + "Initialization", new XAttribute("sourceURL",
                        Resource(repBase, Expand(initialization, id, bandwidth, number, 0),
                            AdaptiveResourceKind.Mp4Init))));
                    list.Add(TimelineElement(timeline));
                    foreach (var (time, duration) in timeline)
                    {
                        list.Add(new XElement(DashNamespace + "SegmentURL", new XAttribute("media",
                            Resource(repBase, Expand(template, id, bandwidth, number++, time),
                                AdaptiveResourceKind.Mp4Segment))));
                    }
                }
            }
            if (representations == 0) throw Invalid();
        }
        if (sets == 0) throw Invalid();
        return output.ToString(SaveOptions.DisableFormatting);
    }
    private Uri Base(XElement element, Uri parent)
    {
        var bases = element.Elements(DashNamespace + "BaseURL").ToArray();
        if (bases.Length > 1) throw Unsupported();
        return bases.Length == 0 ? parent : transport.Resolve(parent, bases[0].Value.Trim());
    }
    private static XElement? SegmentDescriptor(XElement element)
    {
        var descriptors = element.Elements().Where(e => e.Name.LocalName is "SegmentList" or "SegmentTemplate").ToArray();
        if (descriptors.Length > 1) throw Invalid();
        return descriptors.SingleOrDefault();
    }
    private static string Attr(XElement element, string name, string fallback) => (string?)element.Attribute(name) ?? fallback;
    private static double Duration(string value)
    {
        try
        {
            double seconds = XmlConvert.ToTimeSpan(value).TotalSeconds;
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 7 * 86400) throw Unsupported();
            return seconds;
        }
        catch (FormatException) { throw Invalid(); }
        catch (OverflowException) { throw Invalid(); }
    }
    private static (long Offset, long Count)? DashRange(string? range)
    {
        if (range is null) return null;
        var parts = range.Split('-');
        if (parts.Length != 2) throw Invalid();
        long start = UInt(parts[0], 1L << 40), end = UInt(parts[1], 1L << 40);
        if (end < start || end - start >= 64 * 1024 * 1024) throw Invalid();
        return (start, end - start + 1);
    }
    private static List<(long Time, long Duration)> Timeline(XElement descriptor, long scale, double seconds, int? count)
    {
        var result = new List<(long, long)>();
        long last = 0;
        var timeline = descriptor.Element(DashNamespace + "SegmentTimeline");
        if (timeline is not null)
        {
            foreach (var s in timeline.Elements(DashNamespace + "S"))
            {
                long time = UInt(Attr(s, "t", last.ToString(CultureInfo.InvariantCulture)), 1L << 52);
                long duration = UInt(Attr(s, "d", ""), 1L << 40);
                long repeat = UInt(Attr(s, "r", "0"), 4095); // Negative/unbounded live repetition is unsupported.
                if (time < last || duration == 0 || result.Count + repeat + 1 > 4096) throw Invalid();
                for (int i = 0; i <= repeat; i++)
                {
                    result.Add((time, duration)); time = checked(time + duration);
                }
                last = time;
            }
            if (count.HasValue && count != result.Count || result.Count == 0) throw Invalid();
        }
        else
        {
            long duration = UInt(Attr(descriptor, "duration", ""), 1L << 40);
            if (duration == 0) throw Invalid();
            double requested = count ?? Math.Ceiling(seconds * scale / duration);
            if (requested is < 1 or > 4096) throw new PlaybackException(PlaybackError.AdaptiveBudget);
            for (int i = 0; i < requested; i++) result.Add((checked(i * duration), duration));
        }
        if (result.Count > 4096) throw new PlaybackException(PlaybackError.AdaptiveBudget);
        return result;
    }
    private static XElement TimelineElement(List<(long Time, long Duration)> timeline) =>
        new(DashNamespace + "SegmentTimeline", timeline.Select(s => new XElement(DashNamespace + "S",
            new XAttribute("t", s.Time), new XAttribute("d", s.Duration))));

    private static string Expand(string template, string id, long bandwidth, long number, long time)
    {
        if (template.Length == 0) throw Invalid();
        var result = new StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '$') { result.Append(template[i]); continue; }
            int end = template.IndexOf('$', i + 1);
            if (end < 0) throw Invalid();
            string term = template[(i + 1)..end];
            if (term.Length == 0) result.Append('$');
            else if (term == "RepresentationID") result.Append(id);
            else
            {
                string[] parts = term.Split('%');
                long value = parts[0] switch
                {
                    "Bandwidth" => bandwidth, "Number" => number, "Time" => time, _ => throw Unsupported()
                };
                int digits = 0;
                if (parts.Length == 2 && parts[1].StartsWith('0') && parts[1].EndsWith('d'))
                    digits = (int)UInt(parts[1][1..^1], 12);
                else if (parts.Length != 1) throw Invalid();
                result.Append(value.ToString("D" + digits, CultureInfo.InvariantCulture));
            }
            i = end;
        }
        return result.ToString();
    }
    private static void CopyPresentationAttributes(XElement source, XElement destination)
    {
        foreach (string name in new[] { "codecs", "width", "height", "frameRate", "audioSamplingRate", "lang", "contentType" })
            if (source.Attribute(name) is { } attribute)
            {
                string value = attribute.Value;
                if (value.Length > 256 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && !" .,_-/".Contains(c))) throw Invalid();
                destination.SetAttributeValue(name, value);
            }
        foreach (var channels in source.Elements(DashNamespace + "AudioChannelConfiguration"))
        {
            if (Attr(channels, "schemeIdUri", "") != "urn:mpeg:dash:23003:3:audio_channel_configuration:2011") throw Unsupported();
            var n = UInt(Attr(channels, "value", ""), 32);
            if (n == 0) throw Invalid();
            destination.Add(new XElement(DashNamespace + "AudioChannelConfiguration",
                new XAttribute("schemeIdUri", "urn:mpeg:dash:23003:3:audio_channel_configuration:2011"),
                new XAttribute("value", n)));
        }
    }
    private static void Shape(XElement node)
    {
        if (node.Name.Namespace != DashNamespace) throw Unsupported();
        (string Attributes, string Children) allowed = node.Name.LocalName switch
        {
            "MPD" => ("type profiles mediaPresentationDuration minBufferTime id", "BaseURL Period"),
            "Period" => ("id start duration", "BaseURL AdaptationSet SegmentList SegmentTemplate"),
            "AdaptationSet" => ("id group mimeType codecs contentType lang segmentAlignment startWithSAP width height frameRate audioSamplingRate", "BaseURL Representation SegmentList SegmentTemplate AudioChannelConfiguration"),
            "Representation" => ("id bandwidth mimeType codecs width height frameRate audioSamplingRate startWithSAP", "BaseURL SegmentList SegmentTemplate AudioChannelConfiguration"),
            "BaseURL" => ("", ""),
            "SegmentList" => ("timescale duration startNumber presentationTimeOffset", "Initialization SegmentTimeline SegmentURL"),
            "SegmentTemplate" => ("timescale duration startNumber presentationTimeOffset media initialization", "SegmentTimeline"),
            "SegmentTimeline" => ("", "S"),
            "S" => ("t d r", ""),
            "SegmentURL" => ("media mediaRange", ""),
            "Initialization" => ("sourceURL range", ""),
            "AudioChannelConfiguration" => ("schemeIdUri value", ""),
            _ => throw Unsupported()
        };
        foreach (var attribute in node.Attributes())
            if (!attribute.IsNamespaceDeclaration &&
                (attribute.Name.Namespace != XNamespace.None ||
                 !allowed.Attributes.Split(' ').Contains(attribute.Name.LocalName, StringComparer.Ordinal))) throw Unsupported();
        foreach (var child in node.Elements())
        {
            if (!allowed.Children.Split(' ').Contains(child.Name.LocalName, StringComparer.Ordinal)) throw Unsupported();
            Shape(child);
        }
        if (node.Nodes().Any(n => n is XProcessingInstruction ||
            n is XText t && node.Name.LocalName != "BaseURL" && !string.IsNullOrWhiteSpace(t.Value))) throw Invalid();
        foreach (string singleton in new[] { "SegmentTimeline", "Initialization" })
            if (node.Elements(DashNamespace + singleton).Skip(1).Any()) throw Invalid();
    }
}
