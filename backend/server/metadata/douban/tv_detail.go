package douban

import (
	"encoding/json"
	"fmt"
	"net/url"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

type TVDetail struct {
	Title         string   `json:"title"`
	OriginalTitle string   `json:"original_title"`
	LocalizedName []string `json:"localized_name"`
	Aka           []string `json:"aka"`
	EpisodesCount any      `json:"episodes_count"`
	Pubdate       []string `json:"pubdate"`
}

func FetchTVDetail(database *db.DB, doubanID string) (*TVDetail, error) {
	id := strings.TrimSpace(doubanID)
	if id == "" {
		return nil, fmt.Errorf("missing douban id")
	}
	body, err := FetchRexxarJSON(database, "/rexxar/api/v2/tv/"+url.PathEscape(id), nil)
	if err != nil {
		return nil, err
	}
	if len(body) == 0 {
		return nil, fmt.Errorf("douban tv detail payload empty")
	}
	var payload TVDetail
	if err := json.Unmarshal(body, &payload); err != nil {
		return nil, err
	}
	return &payload, nil
}
