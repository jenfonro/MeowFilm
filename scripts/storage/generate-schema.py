"""Assemble platform-isolated SQLite schemas from packages/storage/schema/ and validate them."""
import argparse
import re
import sqlite3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCHEMA_ROOT = ROOT / "packages/storage/schema"
GENERATED_ROOT = ROOT / "packages/storage/generated"

TARGETS = {
    "standalone": {
        "layers": ("core", "runtime"),
        "outputs": (GENERATED_ROOT / "schema-standalone.sql",),
        "forbidden_prefixes": ("windows_", "ios_", "music_songs", "music_owner_state", "music_library_", "music_collections", "music_queue_", "music_history_"),
    },
    "windows": {
        "layers": ("core", "runtime", "music", "windows"),
        "outputs": (GENERATED_ROOT / "schema-windows.sql",),
        "forbidden_prefixes": ("ios_",),
    },
    "ios": {
        "layers": ("core", "runtime", "music", "ios"),
        "outputs": (
            GENERATED_ROOT / "schema-ios.sql",
            ROOT / "native/MeowKit/Sources/MeowStorage/Resources/storage-schema.sql",
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
        for required in ("storage_identity", "owner_revisions", "operation_receipts"):
            if required not in table_names:
                raise SystemExit(f"[{target_name}] missing required core table: {required}")

        for name, ddl in tables:
            for prefix in forbidden_prefixes:
                if name.startswith(prefix):
                    raise SystemExit(f"[{target_name}] forbidden cross-platform table: {name}")
            if not re.search(r"\bSTRICT\b", ddl or "", re.I):
                raise SystemExit(f"[{target_name}] table is not STRICT: {name}")
            cols = [row[1] for row in conn.execute(f'PRAGMA table_info("{name}")').fetchall()]
            for col in cols:
                if col in ("shape_mask", "null_mask"):
                    raise SystemExit(f"[{target_name}] forbidden pseudo-relational column {name}.{col}")
            for fk in conn.execute(f'PRAGMA foreign_key_list("{name}")').fetchall():
                ref_table = fk[2]
                if ref_table not in table_names:
                    raise SystemExit(f"[{target_name}] broken foreign key {name} -> {ref_table}")

        indexes = conn.execute(
            "SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name NOT LIKE 'sqlite_%'"
        ).fetchone()[0]
        return {"tables": len(tables), "indexes": indexes, "bytes": len(sql.encode("utf-8"))}
    finally:
        conn.close()


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate or verify platform-isolated SQLite schemas.")
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
