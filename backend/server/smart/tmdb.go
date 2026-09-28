package smart

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/tmdb"
)

type smartTMDBCredits struct {
	MediaType string // "movie" | "tv"
	ID        int
	Cast      []smartTMDBCast
	Crew      []smartTMDBCrew
}

type smartTMDBCast struct {
	ID      int
	Name    string
	Role    string
	Profile string
	Order   int
}

type smartTMDBCrew struct {
	ID      int
	Name    string
	Job     string
	Dept    string
	Profile string
}

func tmdbImageURL(database *db.DB, path string, size string) string {
	p := strings.TrimSpace(path)
	if p == "" {
		return ""
	}
	if !strings.HasPrefix(p, "/") {
		p = "/" + p
	}
	sz := strings.TrimSpace(size)
	if sz == "" {
		sz = "w500"
	}
	return tmdb.JoinImage(tmdb.ResolveImageBase(database), "t/p/"+sz+p)
}
