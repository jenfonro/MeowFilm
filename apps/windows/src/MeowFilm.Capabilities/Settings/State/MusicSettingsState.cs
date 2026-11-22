using System.Collections.ObjectModel;
using System.ComponentModel;

namespace MeowFilm.Capabilities;

/// <summary>Isolated music settings, sources, quality, exclusions, appearance, and cache state slice.</summary>
public sealed class MusicSettingsState(MusicCapability owner) : INotifyPropertyChanged
{
    internal MusicCapability Owner { get; } = owner;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Notify(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public string Status => Owner.Status;
    public bool CanEdit => Owner.CanEdit;
    public bool CanEditQuality => Owner.CanEditQuality;
    public bool CanEditMetadata => Owner.CanEditMetadata;
    public bool CanEditSearchPreferences => Owner.CanEditSearchPreferences;
    public bool CanTransferAccounts => Owner.CanTransferAccounts;
    public bool CanClearPlaybackCache => Owner.CanClearPlaybackCache;

    // Sources
    public bool CanEditScripts => Owner.CanEditScripts;
    public bool CanEditOfficialSources => Owner.CanEditOfficialSources;
    public bool CanCheckSelectedSources => Owner.CanCheckSelectedSources;
    public bool CanRemoveSelectedSources => Owner.CanRemoveSelectedSources;
    public bool CanCancelSourceTransfer => Owner.CanCancelSourceTransfer;
    public ObservableCollection<MusicSourceRow> Sources => Owner.Sources;

    // Quality & Exclusions & Metadata & Search preferences
    public string[] PlaybackQualityOptions => Owner.PlaybackQualityOptions;
    public string PlaybackQualityRequest => Owner.PlaybackQualityRequest;
    public string QualityStatus => Owner.QualityStatus;
    public ObservableCollection<MusicQualityRow> QualityCategories => Owner.QualityCategories;
    public ObservableCollection<MusicQualityRow> SpatialQualities => Owner.SpatialQualities;
    public ObservableCollection<MusicMetadataPlatformRow> MetadataPlatforms => Owner.MetadataPlatforms;
    public bool HasSourceExclusions => Owner.HasSourceExclusions;
    public ObservableCollection<MusicSourceExclusionRow> SourceExclusions => Owner.SourceExclusions;
    public ObservableCollection<MusicExclusionOption> ExclusionSongs => Owner.ExclusionSongs;
    public ObservableCollection<MusicExclusionOption> ExclusionProviders => Owner.ExclusionProviders;
    public IReadOnlyList<MusicExclusionOption> ExclusionPlatforms => Owner.ExclusionPlatforms;
    public bool HasSearchHistory => Owner.HasSearchHistory;
    public string SearchPreferenceStatus => Owner.SearchPreferenceStatus;
    public MusicSearchKindSettings SongSearch => Owner.SongSearch;
    public MusicSearchKindSettings PlaylistSearch => Owner.PlaylistSearch;

    // Appearance
    public bool CanEditAppearance => Owner.CanEditAppearance;
    public bool CanEditFixedAppearance => Owner.CanEditFixedAppearance;
    public string AppearanceColorMode => Owner.AppearanceColorMode;
    public string AppearanceColorPalette => Owner.AppearanceColorPalette;
    public string AppearancePlaylistView => Owner.AppearancePlaylistView;
    public string AppearanceStatus => Owner.AppearanceStatus;
    public MusicCatalogChoice[] ColorModes => Owner.ColorModes;
    public MusicCatalogChoice[] PlaylistViews => Owner.PlaylistViews;

    // Home source settings
    public bool CanEditCatalogSource => Owner.CanEditCatalogSource;
    public bool CanReadCatalogSource => Owner.CanReadCatalogSource;
    public bool HasCatalogSourceError => Owner.HasCatalogSourceError;
    public string CatalogSourcePlatform => Owner.CatalogSourcePlatform;
    public string CatalogSourceStatus => Owner.CatalogSourceStatus;
    public MusicCatalogChoice[] CatalogSourceChoices => Owner.CatalogSourceChoices;
}
