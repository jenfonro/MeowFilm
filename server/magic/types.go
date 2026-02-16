package magic

type SeasonEpisode struct {
	Season  int
	Episode int
}

type Rule struct {
	Pattern string
	Flags   string
	Replace string
}
