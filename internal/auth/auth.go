package auth

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"strings"
	"time"

	"golang.org/x/crypto/bcrypt"

	"github.com/jenfonro/meowfilm/internal/db"
)

const CookieName = "meowfilm_auth"

var tokenTTL = 30 * 24 * time.Hour

type Options struct {
	TrustProxy   bool
	CookieSecure bool
}

type User struct {
	ID       int64  `json:"userId"`
	Username string `json:"username"`
	Role     string `json:"role"`
	Status   string `json:"status"`
}

type Auth struct {
	db           *db.DB
	cookieSecure bool
}

type ctxKey int

const (
	userKey ctxKey = iota
	tokenKey
)

func New(database *db.DB, opts Options) *Auth {
	return &Auth{
		db:           database,
		cookieSecure: opts.CookieSecure,
	}
}

func (a *Auth) Middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		token := strings.TrimSpace(readCookie(r))
		if token == "" {
			next.ServeHTTP(w, r.WithContext(context.WithValue(r.Context(), userKey, (*User)(nil))))
			return
		}

		u, exp := a.resolveToken(token)
		if u == nil || exp.Before(time.Now()) {
			a.deleteToken(token)
			clearCookie(w, a.cookieSecure)
			next.ServeHTTP(w, r.WithContext(context.WithValue(r.Context(), userKey, (*User)(nil))))
			return
		}

		if u.Status != "active" {
			a.deleteToken(token)
			clearCookie(w, a.cookieSecure)
		}

		ctx := context.WithValue(r.Context(), userKey, u)
		ctx = context.WithValue(ctx, tokenKey, token)
		next.ServeHTTP(w, r.WithContext(ctx))
	})
}

func CurrentUser(r *http.Request) *User {
	if r == nil {
		return nil
	}
	v := r.Context().Value(userKey)
	u, _ := v.(*User)
	return u
}

func RequireLoginPage(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		u := CurrentUser(r)
		if u != nil && u.Status == "active" {
			next.ServeHTTP(w, r)
			return
		}
		http.Redirect(w, r, "/", http.StatusFound)
	})
}

func (a *Auth) RequireAdmin(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		u := CurrentUser(r)
		if u == nil {
			writeJSON(w, http.StatusUnauthorized, map[string]any{"success": false, "message": "Unauthorized"})
			return
		}
		if u.Role != "admin" {
			writeJSON(w, http.StatusForbidden, map[string]any{"success": false, "message": "无权限操作"})
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (a *Auth) RequireAuthAPI(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		u := CurrentUser(r)
		if u == nil {
			writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "Unauthorized"})
			return
		}
		if u.Status != "active" {
			writeJSON(w, http.StatusForbidden, map[string]any{"error": "该账户已禁用"})
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (a *Auth) Login(w http.ResponseWriter, username, password string) (status int, msg string) {
	u := strings.TrimSpace(username)
	p := password
	if u == "" || p == "" {
		return http.StatusBadRequest, "用户名与密码不能为空"
	}
	var hashed string
	var role string
	var statusStr string
	err := a.db.SQL().QueryRow(`SELECT password, role, status FROM users WHERE username = ? LIMIT 1`, u).Scan(&hashed, &role, &statusStr)
	if err != nil {
		if err == sql.ErrNoRows {
			return http.StatusUnauthorized, "用户名或密码错误"
		}
		return http.StatusInternalServerError, "请求失败"
	}
	if statusStr != "active" {
		return http.StatusForbidden, "该账户已禁用"
	}
	if err := bcrypt.CompareHashAndPassword([]byte(hashed), []byte(p)); err != nil {
		return http.StatusUnauthorized, "用户名或密码错误"
	}
	token, err := a.issueToken(u)
	if err != nil || token == "" {
		return http.StatusInternalServerError, "请求失败"
	}
	writeCookie(w, token, a.cookieSecure)
	return http.StatusOK, ""
}

func (a *Auth) Logout(w http.ResponseWriter, r *http.Request) {
	token := strings.TrimSpace(readCookie(r))
	if token != "" {
		a.deleteToken(token)
	}
	clearCookie(w, a.cookieSecure)
}

func (a *Auth) resolveToken(token string) (*User, time.Time) {
	var u User
	var expMS int64
	err := a.db.SQL().QueryRow(`
		SELECT u.id, u.username, u.role, u.status, t.expires_at
		FROM auth_tokens t JOIN users u ON u.id = t.user_id
		WHERE t.token = ? LIMIT 1
	`, token).Scan(&u.ID, &u.Username, &u.Role, &u.Status, &expMS)
	if err != nil {
		return nil, time.Time{}
	}
	return &u, time.UnixMilli(expMS)
}

func (a *Auth) issueToken(username string) (string, error) {
	var userID int64
	if err := a.db.SQL().QueryRow(`SELECT id FROM users WHERE username = ? LIMIT 1`, username).Scan(&userID); err != nil {
		return "", err
	}
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	token := base64.RawURLEncoding.EncodeToString(b)
	now := time.Now()
	exp := now.Add(tokenTTL)
	_, err := a.db.SQL().Exec(`INSERT INTO auth_tokens(token, user_id, created_at, expires_at) VALUES (?,?,?,?)`,
		token, userID, now.UnixMilli(), exp.UnixMilli())
	if err != nil {
		return "", err
	}
	return token, nil
}

func (a *Auth) deleteToken(token string) {
	_, _ = a.db.SQL().Exec(`DELETE FROM auth_tokens WHERE token = ?`, token)
}

func readCookie(r *http.Request) string {
	if r == nil {
		return ""
	}
	c, err := r.Cookie(CookieName)
	if err != nil || c == nil {
		return ""
	}
	return c.Value
}

func writeCookie(w http.ResponseWriter, token string, secure bool) {
	http.SetCookie(w, &http.Cookie{
		Name:     CookieName,
		Value:    token,
		Path:     "/",
		HttpOnly: true,
		SameSite: http.SameSiteLaxMode,
		Secure:   secure,
		MaxAge:   int(tokenTTL.Seconds()),
	})
}

func clearCookie(w http.ResponseWriter, secure bool) {
	http.SetCookie(w, &http.Cookie{
		Name:     CookieName,
		Value:    "",
		Path:     "/",
		HttpOnly: true,
		SameSite: http.SameSiteLaxMode,
		Secure:   secure,
		MaxAge:   -1,
	})
}

func writeJSON(w http.ResponseWriter, status int, payload any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(payload)
}
