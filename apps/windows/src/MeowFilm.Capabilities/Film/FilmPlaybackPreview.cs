using System.ComponentModel;

namespace MeowFilm.Capabilities;

public sealed class FilmPlaybackPage : INotifyPropertyChanged
{
    private FilmEpisodeItem selectedEpisode;
    private bool isListLayout, overviewExpanded;

    internal FilmPlaybackPage(
        FilmCardItem item,
        string metadata,
        string genres,
        string directors,
        string cast,
        string overview,
        string seasonLabel,
        IReadOnlyList<(string Title, string Overview, string Image, string Duration)> episodes,
        IReadOnlyList<FilmCardItem> recommendations)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(recommendations);
        if (episodes.Count == 0)
            throw new ArgumentException("A playback page requires an episode.", nameof(episodes));

        Item = item;
        Metadata = metadata;
        Genres = genres;
        Directors = directors;
        Cast = cast;
        Overview = overview;
        SeasonLabel = seasonLabel;
        Episodes = Array.AsReadOnly(episodes.Select((episode, index) => new FilmEpisodeItem(
            this, index + 1, episode.Title, episode.Overview, episode.Image, episode.Duration)).ToArray());
        Recommendations = Array.AsReadOnly(recommendations.ToArray());
        selectedEpisode = Episodes[0];
    }

    public FilmCardItem Item { get; private set; }
    public string Title => Item.Title;
    public string Poster => Item.Poster;
    public string Metadata { get; }
    public string Genres { get; }
    public string Directors { get; }
    public string Cast { get; }
    public string Overview { get; }
    public IReadOnlyList<FilmEpisodeItem> Episodes { get; }
    public IReadOnlyList<FilmCardItem> Recommendations { get; }
    public FilmEpisodeItem SelectedEpisode => selectedEpisode;
    public bool IsListLayout => isListLayout;
    public bool IsHorizontalLayout => !IsListLayout;
    public bool OverviewExpanded => overviewExpanded;
    public string SeasonLabel { get; }
    public string EpisodeCountLabel => $"共{Episodes.Count}集";

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void UpdateItem(FilmCardItem item)
    {
        if (ReferenceEquals(Item, item)) return;
        Item = item;
        Changed(nameof(Item));
        Changed(nameof(Title));
        Changed(nameof(Poster));
    }

    internal void SelectEpisode(FilmEpisodeItem episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        if (!Episodes.Contains(episode))
            throw new ArgumentException("The episode does not belong to this page.", nameof(episode));
        if (ReferenceEquals(selectedEpisode, episode)) return;

        var previous = selectedEpisode;
        selectedEpisode = episode;
        previous.NotifySelectionChanged();
        episode.NotifySelectionChanged();
        Changed(nameof(SelectedEpisode));
    }

    internal void SetListLayout(bool value)
    {
        if (isListLayout == value) return;
        isListLayout = value;
        Changed(nameof(IsListLayout));
        Changed(nameof(IsHorizontalLayout));
    }

    internal void ToggleOverview()
    {
        overviewExpanded = !overviewExpanded;
        Changed(nameof(OverviewExpanded));
    }

    private void Changed(string name) =>
        PropertyChanged?.Invoke(this, new(name));
}

public sealed class FilmEpisodeItem : INotifyPropertyChanged
{
    private readonly FilmPlaybackPage page;

    internal FilmEpisodeItem(
        FilmPlaybackPage page,
        int number,
        string title,
        string overview,
        string image,
        string duration)
    {
        this.page = page;
        Number = number;
        Title = title;
        Overview = overview;
        Image = image;
        Duration = duration;
    }

    public int Number { get; }
    public string Title { get; }
    public string Overview { get; }
    public string Image { get; }
    public string Duration { get; }
    public string Label => page.SeasonLabel == "正片" ? Title : $"第{Number}集 · {Title}";
    public bool IsSelected => ReferenceEquals(page.SelectedEpisode, this);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void NotifySelectionChanged() =>
        PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
}

internal static class FilmPlaybackPreview
{
    private const string AssetRoot =
        "pack://application:,,,/MeowFilm.Capabilities;component/Film/Presentation/Assets/";

    private const string MovieOverview =
        "一封多年未寄出的信，让离乡已久的青年重新回到东北小城。" +
        "父亲守着即将关闭的旧影院，旧友正准备各奔东西，一场突如其来的失踪打破了平静。" +
        "沿着铁路、河岸与老街留下的线索，几位故人逐渐拼起被时间藏住的往事，也重新理解了彼此的选择。";

    private static readonly (string Title, string Overview)[] MovieEpisodes =
    [
        ("正片", MovieOverview)
    ];

    private static readonly (string Title, string Overview)[] TvEpisodes =
    [
        ("旧事", "桦林的清晨，一辆来历不明的出租车让王响想起多年前的旧案。他找到龚彪，决定先从车牌查起。"),
        ("重逢", "多年未见的老友重新聚到一起，马德胜带来一份旧记录。三人各执一词，却都放不下当年的遗憾。"),
        ("来信", "一封被遗忘的信件牵出王阳的往事。王响沿着儿子走过的街道寻找线索，旧日生活渐渐清晰。"),
        ("暗流", "工厂里的传闻与舞厅里的相遇交织在一起。沈墨试着守住自己的生活，一次意外却改变了所有人的计划。"),
        ("寻人", "王响和龚彪走访旧厂区，在熟悉的门牌后发现陌生的故事。一位老邻居的回忆让调查出现新的方向。"),
        ("雪夜", "一场大雪困住了归途，也让几位年轻人有机会说出心事。看似平静的夜晚，一条重要线索悄然浮现。"),
        ("旧照", "马德胜从一张合影里认出被忽略的人。三人循着照片走访当年的同事，发现各自记忆中的细节并不相同。"),
        ("岔路", "王阳与沈墨面对艰难的选择，父子之间的隔阂越来越深。王响终于意识到，自己错过的不只是一次谈话。"),
        ("回声", "新的证词让旧案的时间线发生变化。龚彪独自追查车辆去向，意外听到一个反复出现在往事里的名字。"),
        ("远行", "有人准备离开桦林，有人仍在等待一个答案。临行前的见面揭开了隐瞒多年的约定，真相逐渐靠近。"),
        ("守望", "追索多年的线索汇到同一处，三位老友决定完成最后一次走访。王响必须在执念与眼前的生活之间作出选择。"),
        ("往前看", "旧事终于有了回音，迟到的告别也等来了说出口的机会。列车再次驶过桦林，留下的人开始迎接新的季节。")
    ];

    private static readonly (string Title, string Overview)[] AnimeEpisodes =
    [
        ("启程", "年轻的旅人与守护森林的同伴在小镇相遇。一张残缺的地图让他们踏上寻找旧日星塔的旅程。"),
        ("林中来客", "夜行的精灵误把营火当作求救信号，带来山谷里失去方向的消息。旅人决定绕路帮助它寻找家人。"),
        ("失落的地图", "集市上的旧书商认出了地图上的文字，却不愿轻易解释。为换取线索，两位同伴接下一份修复古书的委托。"),
        ("雨中的约定", "连日大雨让山路中断，队伍暂住在一座钟楼里。守钟人讲起多年前的约定，让大家重新思考等待的意义。"),
        ("风之渡口", "前往对岸的渡船停航，旅人需要找到使风车重新转动的方法。一位沉默的工匠加入了这次尝试。"),
        ("微光", "矿洞深处传来微弱的歌声，似乎与地图上的星纹有关。众人放下分歧，协力引导迷路的孩子返回地面。"),
        ("远方的信", "信使送来一封无法投递的旧信，同伴认出了收信人的名字。送信的绕行让一段被误解的友情重新接续。"),
        ("山城庆典", "山城正在准备一年一度的灯火庆典，旅人受邀帮忙。热闹背后，一位少年正为是否离开家乡而犹豫。"),
        ("遗忘的名字", "星塔的守门人遗失了自己的记忆，只记得一个名字。旅人从残存的壁画里寻找答案，也看见了同伴的过去。"),
        ("雪境", "队伍穿越无人的雪原，食物与体力都接近极限。彼此留下的小小照顾，成为坚持走下去的力量。"),
        ("群星之下", "抵达星塔后，旅人发现地图指向的并非宝藏。每个人都必须回答一个关于失去与守护的问题。"),
        ("新的旅途", "星塔重新亮起，同行的人在晨光中道别。带着一路收获的故事，旅人选择走向地图上尚未命名的远方。")
    ];

    private static readonly (string Title, string Overview)[] ShowEpisodes =
    [
        ("回到乡野", "嘉宾抵达山间小院，清理房间、分配农具，并向邻居请教当季的种植安排。第一顿晚饭从一道家常菜开始。"),
        ("清晨集市", "大家带着有限的采购预算走进早市，寻找新鲜食材。讨价还价之余，也听摊主讲起小镇的生活变化。"),
        ("开灶第一餐", "为了招待前来帮忙的村民，嘉宾分工准备一桌饭菜。生火、择菜和掌勺中的小意外，让合作慢慢有了默契。"),
        ("雨天来客", "突来的春雨打乱了田间计划，老朋友带着家乡食物登门。众人围坐屋檐下，交换各自最近的生活故事。"),
        ("赶集日", "小院准备把亲手制作的点心带去镇上义卖。设计摊位、核算成本与招呼客人的工作，让每个人都忙得充实。"),
        ("山野寻味", "在当地向导的带领下，嘉宾认识山野里的时令食材。回到小院后，大家尝试把沿途的见闻变成一道新菜。"),
        ("夏夜长谈", "忙完一天的农活，众人在院子里搭起露天茶桌。一场关于朋友、家庭与工作节奏的谈话持续到夜深。"),
        ("一起收获", "第一批作物迎来收获，嘉宾邀请邻居一同下地。分装、称重与配送环环相扣，亲手劳动终于结出了果实。"),
        ("老街相会", "大家走访小镇老街，学习修补旧物与传统手艺。一家经营多年的小店，让嘉宾重新体会到陪伴的分量。"),
        ("村中放映", "小院为村民筹备一场露天放映，邀请孩子们制作手绘请柬。银幕亮起之前，每个人都找到了能帮上忙的位置。"),
        ("季末家宴", "离开前，嘉宾用这一季收获的食材准备家宴。曾经来过小院的朋友再次聚齐，分享最难忘的一次相处。"),
        ("再出发", "大家整理工具、归还借物，并给下一季的小院留下一封信。告别的早餐之后，每个人带着新的生活习惯踏上归途。")
    ];

    internal static FilmPlaybackPage Create(FilmCardItem item, string sectionKey)
    {
        ArgumentNullException.ThrowIfNull(item);
        var preview = sectionKey switch
        {
            "movie" => (
                Metadata: "2023 / 中国大陆 / 118分钟",
                Genres: "剧情 / 家庭 / 悬疑",
                Directors: "辛爽",
                Cast: "范伟 / 秦昊 / 陈明昊",
                Overview: MovieOverview,
                SeasonLabel: "正片",
                Duration: "118分钟",
                Episodes: MovieEpisodes),
            "tv" => (
                Metadata: "2023 / 中国大陆 / 第1季 / 12集",
                Genres: "剧情 / 家庭 / 犯罪",
                Directors: "辛爽",
                Cast: "范伟 / 秦昊 / 陈明昊 / 李庚希 / 刘奕铁 / 蒋奇明",
                Overview: "东北小城桦林，一起意外让退休工人王响重新追索多年前的旧案。" +
                    "他与龚彪、马德胜结伴寻找线索，沿着出租车、旧厂区和一张张熟悉的面孔，走回曾经改变命运的秋天。" +
                    "两代人的相遇、错过与守望逐渐交织，藏在日常生活背后的秘密也终于浮出水面。",
                SeasonLabel: "第1季",
                Duration: "60分钟",
                Episodes: TvEpisodes),
            "anime" => (
                Metadata: "2023 / 日本 / 第1季 / 12集",
                Genres: "动画 / 奇幻 / 冒险",
                Directors: "斋藤圭一郎",
                Cast: "种崎敦美 / 市之濑加那 / 小林千晃",
                Overview: "一位年轻的旅人带着残缺的地图，与几名性格迥异的伙伴踏上寻找星塔的旅途。" +
                    "从林间小镇到遥远雪原，他们帮助陌生人修补生活里的遗憾，也逐渐学会理解彼此。" +
                    "当传说中的终点终于出现，关于勇气、记忆与告别的答案却藏在来时的路上。",
                SeasonLabel: "第1季",
                Duration: "24分钟",
                Episodes: AnimeEpisodes),
            "show" => (
                Metadata: "2023 / 中国大陆 / 第1季 / 12集",
                Genres: "真人秀 / 生活",
                Directors: "王征宇",
                Cast: "黄磊 / 何炅 / 张子枫",
                Overview: "几位朋友暂别城市，在山间小院共同生活一个季节。" +
                    "他们自己买菜做饭、学习农事，也邀请沿途结识的朋友来院里坐坐。" +
                    "从清晨集市到夏夜长谈，一顿饭、一场雨和一次收获，记录下平凡日子里的相处与变化。",
                SeasonLabel: "第1季",
                Duration: "90分钟",
                Episodes: ShowEpisodes),
            _ => throw new ArgumentException("Unknown film section.", nameof(sectionKey))
        };

        var episodes = preview.Episodes.Select((episode, index) => (
            episode.Title,
            episode.Overview,
            Image: AssetRoot + $"episode-{index + 1:00}.jpg",
            preview.Duration)).ToArray();
        return new FilmPlaybackPage(
            item, preview.Metadata, preview.Genres, preview.Directors, preview.Cast,
            preview.Overview, preview.SeasonLabel, episodes, CreateRecommendations(item));
    }

    private static IReadOnlyList<FilmCardItem> CreateRecommendations(FilmCardItem item)
    {
        FilmCardItem[] candidates =
        [
            new("preview:tv:33404425", "隐秘的角落", AssetRoot + "poster-bad-kids.jpg", ""),
            new("preview:tv:33447642", "沉默的真相", AssetRoot + "poster-long-night.jpg", ""),
            new("preview:tv:26883064", "白夜追凶", AssetRoot + "poster-day-and-night.jpg", ""),
            new("preview:tv:26930540", "无证之罪", AssetRoot + "poster-burning-ice.jpg", ""),
            new("preview:tv:35332289", "开端", AssetRoot + "poster-reset.jpg", ""),
            new("preview:tv:35465232", "狂飙", AssetRoot + "poster-knockout.jpg", ""),
            new("preview:tv:35588177", "漫长的季节", AssetRoot + "poster-long-season.jpg", "")
        ];
        return candidates.Where(candidate => candidate.Title != item.Title).Take(6).ToArray();
    }
}
