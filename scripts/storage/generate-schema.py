"""Assemble platform-isolated dual-database SQLite schemas (data & cache) and enforce structural invariants."""
import argparse
import re
import sqlite3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCHEMA_ROOT = ROOT / "packages/storage/schema"
GENERATED_ROOT = ROOT / "packages/storage/generated"

SNAKE_CASE_RE = re.compile(r"^[a-z][a-z0-9_]*$")
TIMESTAMP_COL_RE = re.compile(r"(?:_at|_at_ms|_until|_until_ms|_time)$")
FORBIDDEN_TABLES = frozenset((
    "operation_receipts",
    "music_script_catalog_state",
    "music_anonymous_state",
    "windows_music_search_history_state",
    "music_search_history",
    "music_feedback_settings",
))
FORBIDDEN_TABLE_COLUMNS = {
    "music_songs": frozenset(("local_file_id", "lookup_key", "fingerprint")),
    "film_favorites": frozenset(("target_id", "position", "row_revision")),
    "music_play_contexts": frozenset((
        "source_label", "queue_source_id", "queue_source_title", "queue_source_kind", "queue_source_platform",
    )),
    "music_search_policy": frozenset(("search_playlists",)),
    "windows_music_audio_files": frozenset(("info_duration", "info_size", "info_quality_cache_signature")),
    "ios_music_audio_files": frozenset(("complete", "protected")),
    "ios_music_accounts": frozenset(("platform", "row_revision")),
}
FORBIDDEN_COLUMNS = frozenset((
    "shape_mask",
    "null_mask",
    "presence_low",
    "presence_high",
    "has_enabled",
    "has_auto_disable",
))

TARGETS = {
    "data-standalone": {
        "role": "data",
        "layers": ("data/core", "data/runtime"),
        "outputs": (GENERATED_ROOT / "schema-data-standalone.sql",),
        "forbidden_prefixes": (
            "windows_",
            "ios_",
            "cached_",
            "metadata_cache_",
            "music_songs",
            "music_play_contexts",
            "music_library_",
            "music_collections",
            "music_queue_",
            "music_history_",
            "music_catalog_source",
            "music_appearance",
            "music_playback_",
            "music_source_exclusions",
            "music_search_",
            "music_feedback_",
            "cat_scripts",
            "film_favorites",
            "film_favorite_",
            "film_source_",
            "film_video_",
            "novel_",
            "playback_volume",
        ),
    },
    "data-windows": {
        "role": "data",
        "layers": ("data/core", "data/runtime", "data/shared", "data/music", "data/windows"),
        "outputs": (GENERATED_ROOT / "schema-data-windows.sql",),
        "forbidden_prefixes": ("ios_", "cached_", "metadata_cache_"),
    },
    "data-ios": {
        "role": "data",
        "layers": ("data/core", "data/runtime", "data/shared", "data/music", "data/ios"),
        "outputs": (
            GENERATED_ROOT / "schema-data-ios.sql",
            ROOT / "native/MeowKit/Sources/MeowStorage/Resources/storage-data-schema.sql",
        ),
        "forbidden_prefixes": ("windows_", "cached_", "metadata_cache_"),
    },
    "cache-standalone": {
        "role": "cache",
        "layers": ("cache/core", "cache/runtime"),
        "outputs": (GENERATED_ROOT / "schema-cache-standalone.sql",),
        "forbidden_prefixes": ("windows_", "ios_", "cached_", "music_", "film_", "novel_", "cat_", "source_", "pan_"),
    },
    "cache-windows": {
        "role": "cache",
        "layers": ("cache/core", "cache/runtime", "cache/shared", "cache/windows"),
        "outputs": (GENERATED_ROOT / "schema-cache-windows.sql",),
        "forbidden_prefixes": ("ios_",),
    },
    "cache-ios": {
        "role": "cache",
        "layers": ("cache/core", "cache/runtime", "cache/shared", "cache/ios"),
        "outputs": (
            GENERATED_ROOT / "schema-cache-ios.sql",
            ROOT / "native/MeowKit/Sources/MeowStorage/Resources/storage-cache-schema.sql",
        ),
        "forbidden_prefixes": ("windows_",),
    },
}


def collect_sql(layers: tuple[str, ...]) -> str:
    parts: list[str] = []
    for layer in layers:
        layer_dir = SCHEMA_ROOT / layer
        if not layer_dir.is_dir():
            raise SystemExit(f"missing schema layer directory: {layer_dir.relative_to(ROOT)}")
        files = sorted(layer_dir.glob("*.sql"), key=lambda path: path.name)
        if not files:
            raise SystemExit(f"empty schema layer directory: {layer_dir.relative_to(ROOT)}")
        for path in files:
            rel = path.relative_to(SCHEMA_ROOT).as_posix()
            parts.append(f"-- module: {rel}\n{path.read_text(encoding='utf-8').rstrip()}")
    return "\n\n".join(parts) + "\n"


def index_column_lists(conn: sqlite3.Connection, table_name: str) -> list[list[str]]:
    covered: list[list[str]] = []
    pk_cols = [
        (row[5], row[1])
        for row in conn.execute(f'PRAGMA table_info("{table_name}")').fetchall()
        if row[5] > 0
    ]
    if pk_cols:
        pk_cols.sort(key=lambda item: item[0])
        covered.append([col for _, col in pk_cols])
    for idx in conn.execute(f'PRAGMA index_list("{table_name}")').fetchall():
        idx_name = idx[1]
        is_partial = idx[4]
        if is_partial:
            continue
        idx_cols = [
            row[2]
            for row in sorted(
                conn.execute(f'PRAGMA index_info("{idx_name}")').fetchall(),
                key=lambda r: r[0],
            )
            if row[2] is not None
        ]
        if idx_cols:
            covered.append(idx_cols)
    return covered


def validate_sql(target_name: str, sql: str, forbidden_prefixes: tuple[str, ...]) -> dict[str, int]:
    conn = sqlite3.connect(":memory:")
    try:
        conn.execute("PRAGMA foreign_keys = ON")
        conn.executescript(sql)
        fk_errors = conn.execute("PRAGMA foreign_key_check").fetchall()
        if fk_errors:
            raise SystemExit(f"[{target_name}] foreign_key_check failed: {fk_errors}")

        tables = conn.execute(
            "SELECT name, sql FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
        ).fetchall()
        if not tables:
            raise SystemExit(f"[{target_name}] schema produced no tables")

        table_names = {name for name, _ in tables}
        for required in ("storage_identity", "owner_revisions"):
            if required not in table_names:
                raise SystemExit(f"[{target_name}] missing required core table: {required}")

        for name, ddl in tables:
            if not SNAKE_CASE_RE.match(name):
                raise SystemExit(f"[{target_name}] non-snake_case table name: {name}")
            if name in FORBIDDEN_TABLES:
                raise SystemExit(f"[{target_name}] forbidden legacy/dead table: {name}")
            for prefix in forbidden_prefixes:
                if name.startswith(prefix):
                    raise SystemExit(f"[{target_name}] forbidden cross-role/cross-platform table: {name}")
            if not re.search(r"\bSTRICT\b", ddl or "", re.I):
                raise SystemExit(f"[{target_name}] table is not STRICT: {name}")

            col_rows = conn.execute(f'PRAGMA table_info("{name}")').fetchall()
            if name == "owner_revisions":
                col_names = [row[1] for row in col_rows]
                if col_names != ["owner", "revision"]:
                    raise SystemExit(f"[{target_name}] owner_revisions must have exact columns [owner, revision], got {col_names}")
            if name == "cached_music_chorus":
                col_names = {row[1] for row in col_rows}
                if "cache_key" in col_names:
                    raise SystemExit(f"[{target_name}] cached_music_chorus must use composite natural PK without cache_key")

            for row in col_rows:
                col, col_type = row[1], (row[2] or "").upper()
                if not SNAKE_CASE_RE.match(col):
                    raise SystemExit(f"[{target_name}] non-snake_case column name: {name}.{col}")
                if col in FORBIDDEN_COLUMNS:
                    raise SystemExit(f"[{target_name}] forbidden pseudo-relational/bitmask column: {name}.{col}")
                if col in FORBIDDEN_TABLE_COLUMNS.get(name, ()):
                    raise SystemExit(f"[{target_name}] forbidden redundant/frankenstein column: {name}.{col}")
                if col.startswith("ctx_"):
                    raise SystemExit(f"[{target_name}] duplicated ctx_* column outside music_play_contexts: {name}.{col}")
                if TIMESTAMP_COL_RE.search(col) and col_type != "INTEGER":
                    raise SystemExit(f"[{target_name}] non-INTEGER timestamp column: {name}.{col} ({col_type})")

            fk_groups: dict[int, tuple[str, list[tuple[int, str]]]] = {}
            for fk in conn.execute(f'PRAGMA foreign_key_list("{name}")').fetchall():
                fk_id, seq, ref_table, from_col = fk[0], fk[1], fk[2], fk[3]
                if ref_table not in table_names:
                    raise SystemExit(f"[{target_name}] broken foreign key {name} -> {ref_table}")
                if fk_id not in fk_groups:
                    fk_groups[fk_id] = (ref_table, [])
                fk_groups[fk_id][1].append((seq, from_col))

            if fk_groups:
                indexed_lists = index_column_lists(conn, name)
                for fk_id, (ref_table, items) in fk_groups.items():
                    fk_cols = [col for _, col in sorted(items, key=lambda item: item[0])]
                    fk_len = len(fk_cols)
                    fk_set = set(fk_cols)
                    if not any(len(idx) >= fk_len and set(idx[:fk_len]) == fk_set for idx in indexed_lists):
                        raise SystemExit(
                            f"[{target_name}] unindexed foreign key {name}({', '.join(fk_cols)}) -> {ref_table}"
                        )

        indexes = conn.execute(
            "SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name NOT LIKE 'sqlite_%'"
        ).fetchone()[0]
        return {"tables": len(tables), "indexes": indexes, "bytes": len(sql.encode("utf-8"))}
    finally:
        conn.close()


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate or verify dual-database platform-isolated SQLite schemas.")
    parser.add_argument("--check", action="store_true", help="Verify generated outputs match source modules.")
    args = parser.parse_args()

    stray = sorted(SCHEMA_ROOT.glob("*.sql"))
    if stray:
        names = ", ".join(p.name for p in stray)
        raise SystemExit(f"unexpected flat SQL files in packages/storage/schema/: {names}")

    stats = {}
    for target_name, config in TARGETS.items():
        sql = collect_sql(config["layers"])
        stats[target_name] = validate_sql(target_name, sql, config["forbidden_prefixes"])
        for output in config["outputs"]:
            if not output.parent.is_dir() and "native/MeowKit" in output.as_posix() and not (ROOT / "native/MeowKit").is_dir():
                continue
            if args.check:
                if not output.is_file() or output.read_text(encoding="utf-8") != sql:
                    raise SystemExit(f"generated storage schema differs: {output.relative_to(ROOT)}")
            else:
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_text(sql, encoding="utf-8")

    summary = ", ".join(
        f"{name}({info['tables']} tables, {info['indexes']} indexes, {info['bytes']}B)"
        for name, info in stats.items()
    )
    print(f"Storage schemas {'verified' if args.check else 'generated'}: {summary}")


if __name__ == "__main__":
    main()
