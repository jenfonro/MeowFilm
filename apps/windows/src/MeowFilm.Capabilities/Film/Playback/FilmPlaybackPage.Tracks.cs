using System.ComponentModel;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

public sealed class FilmTrackOption : INotifyPropertyChanged
{
    private bool isSelected;

    internal FilmTrackOption(int? trackIndex, string label, bool isSelected)
    {
        TrackIndex = trackIndex;
        Label = label;
        this.isSelected = isSelected;
    }

    public int? TrackIndex { get; }
    public string Label { get; }
    public bool IsSelected => isSelected;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Select(bool value)
    {
        if (isSelected == value) return;
        isSelected = value;
        PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
    }
}

public sealed class FilmChapterOption : INotifyPropertyChanged
{
    private bool isCurrent;

    internal FilmChapterOption(int index, string title, double startSeconds, double endSeconds, bool isCurrent)
    {
        Index = index;
        Title = title;
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
        StartLabel = FilmJson.Time(startSeconds);
        AccessibleName = $"{title} {StartLabel}";
        this.isCurrent = isCurrent;
    }

    public int Index { get; }
    public string Title { get; }
    public double StartSeconds { get; }
    public double EndSeconds { get; }
    public string StartLabel { get; }
    public string AccessibleName { get; }
    public bool IsCurrent => isCurrent;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetCurrent(bool value)
    {
        if (isCurrent == value) return;
        isCurrent = value;
        PropertyChanged?.Invoke(this, new(nameof(IsCurrent)));
    }
}

public sealed partial class FilmPlaybackPage
{
    private IReadOnlyList<FilmTrackOption> videoTracks = [];
    private IReadOnlyList<FilmTrackOption> audioTracks = [];
    private IReadOnlyList<FilmTrackOption> subtitleTracks = [];
    private IReadOnlyList<FilmChapterOption> chapters = [];
    private int? selectedVideoTrackIndex;
    private int? selectedAudioTrackIndex;
    private int? selectedSubtitleTrackIndex;
    private int activeChapterIndex = -1;
    private string activeChapterTitle = "";

    public IReadOnlyList<FilmTrackOption> VideoTracks => videoTracks;
    public IReadOnlyList<FilmTrackOption> AudioTracks => audioTracks;
    public IReadOnlyList<FilmTrackOption> SubtitleTracks => subtitleTracks;
    public IReadOnlyList<FilmChapterOption> Chapters => chapters;
    public bool HasMultipleVideoTracks => videoTracks.Count > 1;
    public bool HasAudioTracks => audioTracks.Count > 0;
    public bool HasSubtitleTracks => subtitleTracks.Count > 0;
    public bool HasChapters => chapters.Count > 0;
    public int? SelectedVideoTrackIndex => selectedVideoTrackIndex;
    public int? SelectedAudioTrackIndex => selectedAudioTrackIndex;
    public int? SelectedSubtitleTrackIndex => selectedSubtitleTrackIndex;
    public string SelectedVideoTrackLabel => videoTracks.FirstOrDefault(t => t.IsSelected)?.Label ?? "";
    public string SelectedAudioTrackLabel => audioTracks.FirstOrDefault(t => t.IsSelected)?.Label ?? "";
    public string SelectedSubtitleTrackLabel => subtitleTracks.FirstOrDefault(t => t.IsSelected)?.Label ?? "";
    public int ActiveChapterIndex => activeChapterIndex;
    public string ActiveChapterTitle => activeChapterTitle;

    internal void SyncMediaTracksAndChapters(FilmMediaTracks? mediaTracks,
        IReadOnlyList<FilmMediaChapter> mediaChapters, double position)
    {
        SyncTracks(mediaTracks);
        SyncChapters(mediaChapters, position);
    }

    internal void SyncTracks(FilmMediaTracks? mediaTracks)
    {
        bool hadMultipleVideo = HasMultipleVideoTracks;
        bool hadAudio = HasAudioTracks;
        bool hadSubtitle = HasSubtitleTracks;
        int? previousVideo = selectedVideoTrackIndex;
        int? previousAudio = selectedAudioTrackIndex;
        int? previousSubtitle = selectedSubtitleTrackIndex;
        string previousVideoLabel = SelectedVideoTrackLabel;
        string previousAudioLabel = SelectedAudioTrackLabel;
        string previousSubtitleLabel = SelectedSubtitleTrackLabel;

        var nextVideo = BuildTrackOptions(mediaTracks?.Video, mediaTracks?.VideoIndex, includeDisable: false);
        var nextAudio = BuildTrackOptions(mediaTracks?.Audio, mediaTracks?.AudioIndex, includeDisable: false);
        var nextSubtitle = BuildTrackOptions(mediaTracks?.Subtitles, mediaTracks?.SubtitleIndex, includeDisable: true);

        if (ApplyTrackOptions(ref videoTracks, nextVideo)) Changed(nameof(VideoTracks));
        if (ApplyTrackOptions(ref audioTracks, nextAudio)) Changed(nameof(AudioTracks));
        if (ApplyTrackOptions(ref subtitleTracks, nextSubtitle)) Changed(nameof(SubtitleTracks));

        selectedVideoTrackIndex = mediaTracks?.VideoIndex;
        selectedAudioTrackIndex = mediaTracks?.AudioIndex;
        selectedSubtitleTrackIndex = mediaTracks?.SubtitleIndex;

        if (hadMultipleVideo != HasMultipleVideoTracks) Changed(nameof(HasMultipleVideoTracks));
        if (hadAudio != HasAudioTracks) Changed(nameof(HasAudioTracks));
        if (hadSubtitle != HasSubtitleTracks) Changed(nameof(HasSubtitleTracks));
        if (previousVideo != selectedVideoTrackIndex) Changed(nameof(SelectedVideoTrackIndex));
        if (previousAudio != selectedAudioTrackIndex) Changed(nameof(SelectedAudioTrackIndex));
        if (previousSubtitle != selectedSubtitleTrackIndex) Changed(nameof(SelectedSubtitleTrackIndex));
        if (previousVideoLabel != SelectedVideoTrackLabel) Changed(nameof(SelectedVideoTrackLabel));
        if (previousAudioLabel != SelectedAudioTrackLabel) Changed(nameof(SelectedAudioTrackLabel));
        if (previousSubtitleLabel != SelectedSubtitleTrackLabel) Changed(nameof(SelectedSubtitleTrackLabel));
    }

    internal void SyncChapters(IReadOnlyList<FilmMediaChapter> mediaChapters, double position)
    {
        bool hadChapters = HasChapters;
        var built = new FilmChapterOption[mediaChapters.Count];
        for (int i = 0; i < mediaChapters.Count; i++)
        {
            var ch = mediaChapters[i];
            string title = ch.Title.Trim();
            if (title.Length == 0) title = $"章节 {i + 1}";
            double start = double.IsFinite(ch.StartSeconds) ? Math.Max(0, ch.StartSeconds) : 0;
            double end = double.IsFinite(ch.EndSeconds) ? Math.Max(start, ch.EndSeconds) : start;
            built[i] = new FilmChapterOption(ch.Index, title, start, end, isCurrent: false);
        }

        if (!SameChapterStructure(chapters, built))
        {
            chapters = built;
            Changed(nameof(Chapters));
            if (hadChapters != HasChapters) Changed(nameof(HasChapters));
        }

        UpdateActiveChapter(position);
    }

    internal void UpdateActiveChapter(double position)
    {
        int nextIndex = -1;
        if (chapters.Count > 0 && HasOutput && double.IsFinite(position))
        {
            for (int i = 0; i < chapters.Count; i++)
            {
                var ch = chapters[i];
                if (position >= ch.StartSeconds && ch.EndSeconds > ch.StartSeconds && position < ch.EndSeconds)
                {
                    nextIndex = i;
                    break;
                }
            }
            if (nextIndex < 0)
            {
                for (int i = chapters.Count - 1; i >= 0; i--)
                {
                    if (position >= chapters[i].StartSeconds)
                    {
                        nextIndex = i;
                        break;
                    }
                }
            }
        }

        for (int i = 0; i < chapters.Count; i++)
            chapters[i].SetCurrent(i == nextIndex);

        string nextTitle = nextIndex >= 0 ? chapters[nextIndex].Title : "";
        if (activeChapterIndex != nextIndex)
        {
            activeChapterIndex = nextIndex;
            Changed(nameof(ActiveChapterIndex));
        }
        if (activeChapterTitle != nextTitle)
        {
            activeChapterTitle = nextTitle;
            Changed(nameof(ActiveChapterTitle));
        }
    }

    internal Task SelectVideoTrackAsync(int index) => RequireSession().SelectVideoTrackAsync(index);
    internal Task SelectAudioTrackAsync(int index) => RequireSession().SelectAudioTrackAsync(index);
    internal Task SelectSubtitleTrackAsync(int? index) => RequireSession().SelectSubtitleTrackAsync(index);

    internal Task SelectChapterAsync(FilmChapterOption chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        double target = Duration > 0
            ? Math.Clamp(chapter.StartSeconds, 0, Duration)
            : Math.Max(0, chapter.StartSeconds);
        return SeekAsync(target);
    }

    private static FilmTrackOption[] BuildTrackOptions(IReadOnlyList<FilmMediaTrack>? items,
        int? selectedIndex, bool includeDisable)
    {
        if (items is null || items.Count == 0) return [];
        var list = new List<FilmTrackOption>(items.Count + (includeDisable ? 1 : 0));
        if (includeDisable)
            list.Add(new FilmTrackOption(null, "关闭", selectedIndex is null));
        for (int i = 0; i < items.Count; i++)
        {
            var track = items[i];
            list.Add(new FilmTrackOption(track.Index, FormatTrackLabel(track, i + 1),
                selectedIndex == track.Index));
        }
        return [.. list];
    }

    private static bool ApplyTrackOptions(ref IReadOnlyList<FilmTrackOption> current, FilmTrackOption[] next)
    {
        if (current.Count == next.Length)
        {
            bool sameStructure = true;
            for (int i = 0; i < current.Count; i++)
            {
                if (current[i].TrackIndex != next[i].TrackIndex || current[i].Label != next[i].Label)
                {
                    sameStructure = false;
                    break;
                }
            }
            if (sameStructure)
            {
                for (int i = 0; i < current.Count; i++)
                    current[i].Select(next[i].IsSelected);
                return false;
            }
        }
        current = next;
        return true;
    }

    private static bool SameChapterStructure(IReadOnlyList<FilmChapterOption> current, FilmChapterOption[] next)
    {
        if (current.Count != next.Length) return false;
        for (int i = 0; i < current.Count; i++)
        {
            if (current[i].Index != next[i].Index || current[i].Title != next[i].Title ||
                current[i].StartSeconds != next[i].StartSeconds || current[i].EndSeconds != next[i].EndSeconds)
                return false;
        }
        return true;
    }

    private static string FormatTrackLabel(FilmMediaTrack track, int ordinal)
    {
        string title = track.Title.Trim();
        string lang = FormatLanguage(track.Language.Trim());
        string codec = track.Codec.Trim().ToUpperInvariant();
        string primary = title.Length > 0 ? title
            : lang.Length > 0 ? lang
            : track.Kind switch
            {
                FilmTrackKind.Video => $"视频轨 {ordinal}",
                FilmTrackKind.Audio => $"音轨 {ordinal}",
                _ => $"字幕 {ordinal}"
            };
        var details = new List<string>(2);
        if (title.Length > 0 && lang.Length > 0 && !title.Contains(lang, StringComparison.OrdinalIgnoreCase))
            details.Add(lang);
        if (codec.Length > 0)
            details.Add(codec);
        return details.Count > 0 ? $"{primary} · {string.Join(" · ", details)}" : primary;
    }

    private static string FormatLanguage(string code) => code.ToLowerInvariant() switch
    {
        "" or "und" or "unknown" => "",
        "zh" or "zho" or "chi" => "中文",
        "zh-cn" or "zh-hans" or "chs" or "sc" => "简体中文",
        "zh-tw" or "zh-hk" or "zh-hant" or "cht" or "tc" => "繁体中文",
        "en" or "eng" => "英语",
        "ja" or "jpn" => "日语",
        "ko" or "kor" => "韩语",
        "fr" or "fra" or "fre" => "法语",
        "de" or "deu" or "ger" => "德语",
        "es" or "spa" => "西班牙语",
        "ru" or "rus" => "俄语",
        "it" or "ita" => "意大利语",
        "pt" or "por" => "葡萄牙语",
        "th" or "tha" => "泰语",
        "vi" or "vie" => "越南语",
        _ => code
    };
}
