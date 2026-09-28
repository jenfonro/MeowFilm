package emby_service

type ItemState struct {
	CanDelete    bool
	CanDownload  bool
	SupportsSync bool
	IsFolder     bool
	Type         string
}

const (
	ItemTypeMovie            = "Movie"
	ItemTypeSeries           = "Series"
	ItemTypeEpisode          = "Episode"
	ItemTypeSeason           = "Season"
	ItemTypeCollectionFolder = "CollectionFolder"
	MediaTypeVideo           = "Video"
)

func CollectionFolderState() ItemState {
	return ItemState{
		CanDelete:    false,
		CanDownload:  false,
		SupportsSync: true,
		IsFolder:     true,
		Type:         ItemTypeCollectionFolder,
	}
}

func MovieItemState(canDelete bool, canDownload bool) ItemState {
	return ItemState{
		CanDelete:    canDelete,
		CanDownload:  canDownload,
		SupportsSync: true,
		IsFolder:     false,
		Type:         ItemTypeMovie,
	}
}

func SeriesItemState(canDelete bool, canDownload bool) ItemState {
	return ItemState{
		CanDelete:    canDelete,
		CanDownload:  canDownload,
		SupportsSync: true,
		IsFolder:     true,
		Type:         ItemTypeSeries,
	}
}

func EpisodeItemState(canDelete bool, canDownload bool) ItemState {
	return ItemState{
		CanDelete:    canDelete,
		CanDownload:  canDownload,
		SupportsSync: true,
		IsFolder:     false,
		Type:         ItemTypeEpisode,
	}
}

func SeasonItemState() ItemState {
	return ItemState{
		CanDelete:    false,
		CanDownload:  false,
		SupportsSync: false,
		IsFolder:     true,
		Type:         ItemTypeSeason,
	}
}
