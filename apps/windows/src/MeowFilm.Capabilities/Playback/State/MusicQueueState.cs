using System.Collections.ObjectModel;
using System.ComponentModel;

namespace MeowFilm.Capabilities;

/// <summary>Isolated play queue &amp; recommendation state slice.</summary>
public sealed class MusicQueueState(MusicCapability owner) : INotifyPropertyChanged
{
    internal MusicCapability Owner { get; } = owner;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Notify(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public ReadOnlyObservableCollection<MusicQueueSurfaceRow> QueueSurfaceRows => Owner.QueueSurfaceRows;
    public string QueueSurfaceCountText => Owner.QueueSurfaceCountText;
    public string QueueSurfaceStatus => Owner.QueueSurfaceStatus;
    public bool HasQueueSurfaceItems => Owner.HasQueueSurfaceItems;
    public bool CanLoadMoreQueueSurface => Owner.CanLoadMoreQueueSurface;
    public bool CanClearQueueSurface => Owner.CanClearQueueSurface;

    public bool HasRecommendationPanel => Owner.HasRecommendationPanel;
    public string RecommendationTitle => Owner.RecommendationTitle;
    public IReadOnlyList<MusicRecommendationCandidate> RecommendationCandidates => Owner.RecommendationCandidates;
    public bool CanChooseRecommendation => Owner.CanChooseRecommendation;
    public bool CanRetryRecommendation => Owner.CanRetryRecommendation;
    public bool CanReconcileRecommendation => Owner.CanReconcileRecommendation;
}
