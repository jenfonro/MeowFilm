"""Repository-owned output locations."""
from pathlib import Path
import stat
import sys

ROOT = Path(__file__).resolve().parents[1]
OUTPUTS = {"build": ("build", "ios"), "tmp": ("tmp",)}


def output_path(name, root=ROOT):
    if name not in OUTPUTS:
        raise ValueError("Unknown repository output: " + str(name))
    path = Path(root).resolve(strict=True)
    for part in OUTPUTS[name]:
        path = path / part
        try:
            info = path.lstat()
        except FileNotFoundError:
            return Path(root).resolve(strict=True).joinpath(*OUTPUTS[name])
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Repository output must not be a link: " + name)
        if not stat.S_ISDIR(info.st_mode):
            raise ValueError("Repository output is not a directory: " + name)
    return path


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: repository-paths.py build|tmp")
    print(output_path(sys.argv[1]))
