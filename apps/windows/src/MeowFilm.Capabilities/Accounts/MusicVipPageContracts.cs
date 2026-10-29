namespace MeowFilm.Capabilities;

public interface IMusicVipPageSource
{
    event Action? VipPageInvalidated;
    Task<MusicVipPageSnapshot> ReadVipPageAsync(CancellationToken token = default);
    Task<MusicVipSignReceipt> SignVipPageAsync(long expectedGeneration,
        CancellationToken token = default);
}

public sealed record MusicVipAccountSummary(
    long Generation,
    string DisplayName,
    string Avatar,
    string Membership);

public sealed record MusicVipSignDay(
    string DayText,
    bool Signed,
    string SongCoverUrl,
    long SignTime,
    bool Today);

public sealed record MusicVipSignCardData(
    string Text,
    string SubText,
    bool RedPrize,
    bool RedDay,
    string ButtonText,
    string Background,
    IReadOnlyList<MusicVipSignDay> Days)
{
    public bool SignedToday => Days.Any(day => day.Today && day.Signed);
}

public sealed record MusicVipBannerData(
    string ImageUrl,
    string ResourceType,
    string ResourceId);

public sealed record MusicVipSignReceipt(
    long Generation,
    MusicVipSignCardData? RefreshedSign);

public sealed record MusicVipPageSnapshot(
    long Generation,
    MusicVipAccountSummary? Account,
    MusicVipSignCardData? Sign,
    MusicVipBannerData? Banner,
    string AccountError = "",
    string SignError = "",
    string BannerError = "",
    bool SignConfirmed = false,
    bool SignUnconfirmed = false)
{
    public bool SignedToday => SignConfirmed || Sign?.SignedToday == true;
}
