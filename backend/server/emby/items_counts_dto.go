package emby

type embyItemsCountsDTO struct {
	MovieCount      int `json:"MovieCount"`
	SeriesCount     int `json:"SeriesCount"`
	EpisodeCount    int `json:"EpisodeCount"`
	GameCount       int `json:"GameCount"`
	ArtistCount     int `json:"ArtistCount"`
	ProgramCount    int `json:"ProgramCount"`
	GameSystemCount int `json:"GameSystemCount"`
	TrailerCount    int `json:"TrailerCount"`
	SongCount       int `json:"SongCount"`
	AlbumCount      int `json:"AlbumCount"`
	MusicVideoCount int `json:"MusicVideoCount"`
	BoxSetCount     int `json:"BoxSetCount"`
	BookCount       int `json:"BookCount"`
	ItemCount       int `json:"ItemCount"`
}
