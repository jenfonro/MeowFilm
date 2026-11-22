using System.Collections;

namespace MeowFilm.Capabilities;

internal interface IShelfSection
{
    IEnumerable Items { get; }
    double HorizontalOffset { get; set; }
    bool IsBannerSection { get; }
    bool IsEntrySection { get; }
}
