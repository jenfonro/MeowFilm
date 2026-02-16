package catpawopen

// Server is an admin-managed CatPawOpen server entry.
type Server struct {
	Name    string `json:"name"`
	APIBase string `json:"apiBase"`
}

type SearchItem struct {
	ID     string
	Name   string
	Pic    string
	Remark string
}

type Episode struct {
	Name string
	URL  string
	Flag string
}

type Pan struct {
	Label    string
	Episodes []Episode
}
