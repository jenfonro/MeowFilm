"""Private build support: OS-owned locks and checked, non-link source extraction."""
import contextlib
import hashlib
import os
from pathlib import Path, PurePosixPath
import subprocess
import sys
import tarfile
import time


@contextlib.contextmanager
def locked(path):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    with open(path, "a+b") as handle:
        if os.name == "nt":
            import msvcrt
            if os.fstat(handle.fileno()).st_size == 0:
                handle.write(b"\0")
                handle.flush()
            waiting = False
            while True:
                handle.seek(0)
                try:
                    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                    break
                except OSError:
                    if not waiting:
                        print("Waiting for film-kernel build lock", file=sys.stderr, flush=True)
                        waiting = True
                    time.sleep(0.5)
        else:
            import fcntl
            fcntl.flock(handle, fcntl.LOCK_EX)
        try:
            yield
        finally:
            if os.name == "nt":
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(handle, fcntl.LOCK_UN)


def extract(archive, output, expected):
    destination = Path(output)
    destination.mkdir(parents=True, exist_ok=False)
    entries = {}
    with tarfile.open(archive, "r:*") as contents:
        for member in contents:
            parts = PurePosixPath(member.name).parts
            if member.name.startswith("/") or any(p in ("..", ".") for p in parts):
                raise ValueError("Unsafe source archive path")
            if len(parts) < 2:
                if not member.isdir():
                    raise ValueError("Source archive must have a top-level directory")
                continue
            relative = "/".join(parts[1:])
            if "\\" in relative or ":" in relative or member.issym() or member.islnk():
                raise ValueError("Source archive contains a link or Windows alias")
            target = destination.joinpath(*parts[1:])
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            if not member.isfile() or relative.casefold() in entries:
                raise ValueError("Source archive contains duplicate or special entries")
            target.parent.mkdir(parents=True, exist_ok=True)
            data = contents.extractfile(member).read()
            target.write_bytes(data)
            target.chmod(0o755 if member.mode & 0o111 else 0o644)
            entries[relative.casefold()] = (relative, data)
    digest = hashlib.sha256()
    for name, data in sorted(entries.values()):
        digest.update(name.encode())
        digest.update(b"\0")
        digest.update(data)
        digest.update(b"\0")
    if digest.hexdigest() != expected:
        raise ValueError("Extracted source tree does not match lock")


if __name__ == "__main__":
    if sys.argv[1] == "lock-run":
        with locked(sys.argv[2]):
            sys.exit(subprocess.call(sys.argv[3:]))
    elif sys.argv[1] == "extract":
        extract(*sys.argv[2:])
    else:
        raise ValueError("Unknown film-kernel support command")
