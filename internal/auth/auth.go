package auth

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"time"

	"golang.org/x/crypto/bcrypt"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/internal/limit"
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
	if exceeded, err := limit.GuardLogin(a.db); err == nil && exceeded {
		return http.StatusServiceUnavailable, limit.PublicCode()
	}
	u := strings.TrimSpace(username)
	p := password
	if u == "" || p == "" {
		return http.StatusBadRequest, "用户名与密码不能为空"
	}
	row, err := a.db.GetUserAuthByUsername(u)
	if err != nil {
		if err == sql.ErrNoRows {
			return http.StatusUnauthorized, "用户名或密码错误"
		}
		return http.StatusInternalServerError, "请求失败"
	}
	if strings.TrimSpace(row.Status) != "active" {
		return http.StatusForbidden, "该账户已禁用"
	}
	if err := bcrypt.CompareHashAndPassword([]byte(row.PasswordHash), []byte(p)); err != nil {
		return http.StatusUnauthorized, "用户名或密码错误"
	}
	token, err := a.issueToken(row.ID)
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
	row, err := a.db.ResolveToken(token)
	if err != nil {
		return nil, time.Time{}
	}
	return &User{
		ID:       row.UserID,
		Username: row.Username,
		Role:     row.Role,
		Status:   row.Status,
	}, row.ExpiresAt
}

func (a *Auth) issueToken(userID int64) (string, error) {
	if userID <= 0 {
		return "", errors.New("invalid user id")
	}
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	token := base64.RawURLEncoding.EncodeToString(b)
	now := time.Now()
	exp := now.Add(tokenTTL)
	if err := a.db.InsertToken(token, userID, exp); err != nil {
		return "", err
	}
	return token, nil
}

func (a *Auth) deleteToken(token string) {
	_ = a.db.DeleteToken(token)
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
