"""Generate platform copies of the single current SQL schema, or verify them."""
import argparse
import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODULES = ROOT / "packages/storage/schema"
TARGETS = (
    ROOT / "packages/storage/schema.sql",
    ROOT / "native/MeowKit/Sources/MeowStorage/Resources/storage-schema.sql",
)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location("music_records", ROOT / "scripts/storage-music-records.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.generate(args.check)
    spec = importlib.util.spec_from_file_location("native_records", ROOT / "scripts/storage-native-records.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.generate(args.check)
    modules = sorted(MODULES.glob("*.sql"), key=lambda path: path.name)
    source = "\n\n".join(f"-- module: {path.name}\n{path.read_text(encoding='utf-8').rstrip()}"
                         for path in modules) + "\n"
    for target in TARGETS:
        if args.check:
            if not target.is_file() or target.read_text(encoding="utf-8") != source:
                raise SystemExit(f"storage schema resource differs: {target.relative_to(ROOT)}")
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(source, encoding="utf-8")


if __name__ == "__main__":
    main()
