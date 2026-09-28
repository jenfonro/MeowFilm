package server

import (
	"errors"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/api"
	"github.com/jenfonro/meowfilm/server/dashboard"
	"github.com/jenfonro/meowfilm/server/emby"
	"github.com/jenfonro/meowfilm/server/netdisk"
	"github.com/jenfonro/meowfilm/server/static"
)

type Config struct {
	Addr       string
	TrustProxy bool
}

type Server struct {
	addr string
	db   *db.DB
	mux  *http.ServeMux
	h    http.Handler
	stop chan struct{}
	once sync.Once
}

func New(cfg Config) (*Server, error) {
	if strings.TrimSpace(cfg.Addr) == "" {
		return nil, errors.New("addr is required")
	}

	database, err := db.Open()
	if err != nil {
		return nil, err
	}
	netdisk.InitNetdiskProxyFromDB(database)

	authMw := auth.New(database, auth.Options{
		TrustProxy:   cfg.TrustProxy,
		CookieSecure: os.Getenv("MEOWFILM_COOKIE_SECURE") == "1",
	})

	mux := http.NewServeMux()

	embyHandler := emby.EmbyHandler(database)
	mux.Handle("/api/", api.Handler(database, authMw))
	mux.Handle("/emby/", embyHandler)
	mux.Handle("/Users/", embyHandler)
	mux.Handle("/Items/", embyHandler)
	mux.Handle("/Shows/", embyHandler)
	mux.Handle("/Videos/", embyHandler)
	mux.Handle("/videos/", embyHandler)
	mux.Handle("/Sessions/", embyHandler)
	mux.Handle("/System/", embyHandler)
	mux.Handle("/DisplayPreferences/", embyHandler)
	mux.Handle("/Library/", embyHandler)
	mux.Handle("/Genres", embyHandler)
	dashboardAPI := dashboard.Handler(database, authMw)
	staticHandler := static.Handler(authMw)
	mux.Handle("/dashboard/", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/dashboard/" {
			staticHandler.ServeHTTP(w, r)
			return
		}
		dashboardAPI.ServeHTTP(w, r)
	}))
	mux.Handle("/", static.Handler(authMw))

	root := authMw.Middleware(mux)
	handler := static.NoStoreForHTMLCSSJS(root)

	srv := &Server{addr: cfg.Addr, db: database, mux: mux, h: handler, stop: make(chan struct{})}
	srv.startPanDailyCleanup()
	return srv, nil
}

func (s *Server) Addr() string          { return s.addr }
func (s *Server) Handler() http.Handler { return s.h }

func (s *Server) Close() error {
	if s == nil || s.db == nil {
		return nil
	}
	s.once.Do(func() {
		if s.stop != nil {
			close(s.stop)
		}
	})
	return s.db.Close()
}

func (s *Server) startPanDailyCleanup() {
	if s == nil || s.db == nil || s.stop == nil {
		return
	}
	loc, err := time.LoadLocation("Asia/Shanghai")
	if err != nil {
		loc = time.FixedZone("Asia/Shanghai", 8*60*60)
	}
	type providerCleanupState struct {
		retryAt    time.Time
		successDay string
	}
	go func() {
		states := map[string]*providerCleanupState{
			"quark": {},
			"uc":    {},
		}
		attempts := map[string]func(*db.DB, time.Time) (bool, error){
			"quark": netdiskTryQuarkDailyCleanup,
			"uc":    netdiskTryUCDailyCleanup,
		}
		for {
			now := time.Now()
			day := now.In(loc).Format("2006-01-02")
			target := netdiskNextDailyCleanupTime(now, loc, 6, 0)
			for provider, st := range states {
				if st.successDay == day {
					continue
				}
				if !st.retryAt.IsZero() && st.retryAt.After(now) && st.retryAt.Before(target) {
					target = st.retryAt
				}
				if st.successDay != day && netdiskNextDailyCleanupTime(now, loc, 6, 0).Before(target) {
					target = netdiskNextDailyCleanupTime(now, loc, 6, 0)
				}
				_ = provider
			}
			timer := time.NewTimer(time.Until(target))
			select {
			case <-s.stop:
				if !timer.Stop() {
					select {
					case <-timer.C:
					default:
					}
				}
				return
			case <-timer.C:
			}

			runNow := time.Now()
			runDay := runNow.In(loc).Format("2006-01-02")
			scheduled := netdiskNextDailyCleanupTime(runNow.Add(-time.Second), loc, 6, 0)
			for provider, st := range states {
				if st.successDay == runDay {
					continue
				}
				shouldTry := false
				if !scheduled.After(runNow) {
					shouldTry = true
				}
				if !st.retryAt.IsZero() && !st.retryAt.After(runNow) {
					shouldTry = true
				}
				if !shouldTry {
					continue
				}
				done, _ := attempts[provider](s.db, runNow)
				if done {
					st.successDay = runDay
					st.retryAt = time.Time{}
				} else {
					st.retryAt = runNow.Add(10 * time.Minute)
				}
			}
		}
	}()
}

var (
	netdiskNextDailyCleanupTime = netdisk.NextDailyCleanupTime
	netdiskTryQuarkDailyCleanup = netdisk.TryQuarkDailyCleanup
	netdiskTryUCDailyCleanup    = netdisk.TryUCDailyCleanup
)
