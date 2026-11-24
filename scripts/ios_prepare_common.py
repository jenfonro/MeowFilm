"""Shared utilities for iOS native dependency preparation scripts."""
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import urllib.request

if os.name != "nt":
    import fcntl
else:
    fcntl = None


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


sha256 = digest


def identity(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


def write_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    temporary.replace(path)


def inventory(directory):
    directory = Path(directory)
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("missing or linked artifact directory")
    result = {}
    for path in directory.rglob("*"):
        if path.is_symlink() or not (path.is_file() or path.is_dir()):
            raise ValueError("non-regular artifact entry")
        if path.is_file():
            result[path.relative_to(directory).as_posix()] = digest(path)
    return result


def verify_inventory(directory, expected):
    if not expected or inventory(directory) != expected:
        raise ValueError("artifact file inventory/hash mismatch")


def run(command, cwd=None, env=None, log=None):
    if log:
        log = Path(log)
        log.parent.mkdir(parents=True, exist_ok=True)
        with log.open("a", encoding="utf-8") as stream:
            stream.write("\n" + " ".join(map(str, command)) + "\n")
            stream.flush()
            subprocess.run(command, cwd=cwd, env=env, stdout=stream, stderr=stream, check=True)
        return None
    return subprocess.check_output(command, cwd=cwd, env=env, text=True).strip()


@contextmanager
def file_lock(path):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as guard:
        if fcntl is not None:
            fcntl.flock(guard, fcntl.LOCK_EX)
        yield


def download_with_proxy(url, destination, *, user_agent="Meowfilm-source-prepare",
                        proxy_timeout=10, direct_timeout=60, use_proxy_only_on_darwin=False):
    request = urllib.request.Request(url, headers={"User-Agent": user_agent})
    if not url.startswith(("https://", "http://")):
        attempts = [(None, direct_timeout)]
    elif use_proxy_only_on_darwin and platform.system() != "Darwin":
        attempts = [({}, direct_timeout)]
    else:
        proxy = "http://127.0.0.1:7890" if sys.platform == "win32" else "http://10.0.0.1:7890"
        attempts = [({"http": proxy, "https": proxy}, proxy_timeout), ({}, direct_timeout)]
    destination = Path(destination)
    for index, (proxies, timeout) in enumerate(attempts):
        try:
            if proxies is None:
                opener = urllib.request.urlopen(request, timeout=timeout)
            else:
                opener = urllib.request.build_opener(urllib.request.ProxyHandler(proxies)).open(request, timeout=timeout)
            with opener as response, destination.open("wb") as output:
                for block in iter(lambda: response.read(1024 * 1024), b""):
                    output.write(block)
            return
        except OSError:
            if index == len(attempts) - 1:
                raise
