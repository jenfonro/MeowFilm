package dashboard

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
)

const videoSourceDeletePath = "/dashboard/video/source/sites/delete"

func newVideoSourceDeleteHandler(t *testing.T) (*db.DB, http.Handler) {
	t.Helper()
	t.Setenv("MEOWFILM_DB_FILE", filepath.Join(t.TempDir(), "sites.db"))
	database, err := db.Open()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = database.Close() })
	if err := database.ReplaceVideoSourceSites([]db.VideoSourceSite{
		{Key: "alpha", Name: "Site A", API: "/spider/alpha/3"},
		{Key: "beta", Name: "Site B", API: "/spider/beta/3"},
		{Key: "gamma", Name: "Site C", API: "/spider/gamma/3"},
	}); err != nil {
		t.Fatal(err)
	}
	for index, key := range []string{"alpha", "beta", "gamma"} {
		if err := database.UpsertVideoSourceSiteState(key, func(state *db.VideoSourceSiteState) {
			state.Enabled = key != "beta"
			state.Home = true
			state.Search = true
			state.Availability = "valid"
			state.OrderIndex = index
		}); err != nil {
			t.Fatal(err)
		}
	}
	if err := database.UpdateAppConfig(func(cfg *db.AppConfig) {
		cfg.VideoSourceSearchCoverSite = "alpha"
	}); err != nil {
		t.Fatal(err)
	}
	for _, role := range []string{"admin", "user"} {
		id, err := database.CreateUser("source-delete-"+role, "unused-test-hash", role)
		if err != nil {
			t.Fatal(err)
		}
		if err := database.InsertToken("source-delete-"+role+"-token", id, time.Now().Add(time.Hour)); err != nil {
			t.Fatal(err)
		}
	}
	authMw := auth.New(database, auth.Options{})
	return database, authMw.Middleware(Handler(database, authMw))
}

func videoSourceDeleteRequest(handler http.Handler, method, path, role string, values url.Values) *httptest.ResponseRecorder {
	request := httptest.NewRequest(method, path, strings.NewReader(values.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	if role != "" {
		request.AddCookie(&http.Cookie{Name: auth.CookieName, Value: "source-delete-" + role + "-token"})
	}
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	return response
}

func readVideoSourceDeleteResponse(t *testing.T, response *httptest.ResponseRecorder) ([]string, string) {
	t.Helper()
	if response.Code != http.StatusOK {
		t.Fatalf("unexpected response: %d %s", response.Code, response.Body.String())
	}
	var body struct {
		Success bool `json:"success"`
		Sites   []struct {
			Key string `json:"key"`
		} `json:"sites"`
		CoverSite string `json:"coverSite"`
	}
	if err := json.Unmarshal(response.Body.Bytes(), &body); err != nil || !body.Success {
		t.Fatalf("invalid response: %s err=%v", response.Body.String(), err)
	}
	keys := []string{}
	for _, site := range body.Sites {
		keys = append(keys, site.Key)
	}
	return keys, body.CoverSite
}

func TestVideoSourceDeleteRouteRequiresAdminPostAndValidKey(t *testing.T) {
	database, handler := newVideoSourceDeleteHandler(t)
	for _, tc := range []struct {
		name, method, role, key string
		status                  int
	}{
		{"anonymous", http.MethodPost, "", "alpha", http.StatusUnauthorized},
		{"non-admin", http.MethodPost, "user", "alpha", http.StatusForbidden},
		{"get", http.MethodGet, "admin", "alpha", http.StatusMethodNotAllowed},
		{"blank", http.MethodPost, "admin", " \n ", http.StatusBadRequest},
		{"missing", http.MethodPost, "admin", "missing", http.StatusNotFound},
	} {
		t.Run(tc.name, func(t *testing.T) {
			response := videoSourceDeleteRequest(handler, tc.method, videoSourceDeletePath, tc.role, url.Values{"key": {tc.key}})
			if response.Code != tc.status {
				t.Fatalf("status=%d want=%d body=%s", response.Code, tc.status, response.Body.String())
			}
			if rows, err := database.ListVideoSourceSites(); err != nil || len(rows) != 3 {
				t.Fatalf("rejected request changed sites: rows=%v err=%v", rows, err)
			}
		})
	}
}

func TestVideoSourceDeleteUpdatesListAndCoverWithoutTouchingOtherSites(t *testing.T) {
	database, handler := newVideoSourceDeleteHandler(t)
	beforeStates, _ := database.ReadVideoSourceSiteStates()
	response := videoSourceDeleteRequest(handler, http.MethodPost, videoSourceDeletePath, "admin", url.Values{"key": {" alpha "}})
	keys, cover := readVideoSourceDeleteResponse(t, response)
	if !reflect.DeepEqual(keys, []string{"beta", "gamma"}) || cover != "gamma" {
		t.Fatalf("wrong remaining order or enabled cover fallback: keys=%v cover=%q", keys, cover)
	}
	states, err := database.ReadVideoSourceSiteStates()
	if err != nil || len(states) != 2 || !reflect.DeepEqual(states["beta"], beforeStates["beta"]) {
		t.Fatalf("wrong state cleanup: states=%v err=%v", states, err)
	}
	response = videoSourceDeleteRequest(handler, http.MethodGet, "/dashboard/video/source/sites", "admin", nil)
	refreshed, refreshedCover := readVideoSourceDeleteResponse(t, response)
	if !reflect.DeepEqual(refreshed, keys) || refreshedCover != cover {
		t.Fatal("refresh restored a deleted site or stale cover")
	}
	for _, key := range []string{"beta", "gamma"} {
		response = videoSourceDeleteRequest(handler, http.MethodPost, videoSourceDeletePath, "admin", url.Values{"key": {key}})
	}
	keys, cover = readVideoSourceDeleteResponse(t, response)
	if len(keys) != 0 || cover != "" {
		t.Fatalf("last site cannot be removed: keys=%v cover=%q", keys, cover)
	}
}

func TestVideoSourceDeleteCanBeRestoredByExplicitImport(t *testing.T) {
	_, handler := newVideoSourceDeleteHandler(t)
	response := videoSourceDeleteRequest(handler, http.MethodPost, videoSourceDeletePath, "admin", url.Values{"key": {"alpha"}})
	readVideoSourceDeleteResponse(t, response)
	response = videoSourceDeleteRequest(handler, http.MethodPost, "/dashboard/video/source/sites/import", "admin", url.Values{
		"sites": {`[{"key":"alpha","name":"Site A","api":"/spider/alpha/3"},{"key":"beta","name":"Site B","api":"/spider/beta/3"},{"key":"gamma","name":"Site C","api":"/spider/gamma/3"}]`},
	})
	keys, _ := readVideoSourceDeleteResponse(t, response)
	found := false
	for _, key := range keys {
		found = found || key == "alpha"
	}
	if !found || len(keys) != 3 {
		t.Fatalf("explicit import did not restore the site: %v", keys)
	}
}

func TestVideoSourceDeleteReportsDatabaseFailure(t *testing.T) {
	database, _ := newVideoSourceDeleteHandler(t)
	_ = database.Close()
	request := httptest.NewRequest(http.MethodPost, videoSourceDeletePath, strings.NewReader("key=alpha"))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	handleDashboardVideoSourceSiteDelete(response, request, database)
	if response.Code != http.StatusInternalServerError || !strings.Contains(response.Body.String(), `"success":false`) {
		t.Fatalf("database failure reported as success: %d %s", response.Code, response.Body.String())
	}
}
