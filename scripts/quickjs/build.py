#!/usr/bin/env python3
"""Rebuild the pinned release-sync task-context loader/WASM (requires emsdk 5.0.1)."""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request


ROOT = Path(__file__).resolve().parents[2]
POLICY = ROOT / "scripts/quickjs-runtime.json"
VARIANT = "packages/variant-quickjs-wasmfile-release-sync"
PATCHED = ("vendor/quickjs/quickjs.c", "vendor/quickjs/quickjs.h", "c/interface.c")


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def write_changed(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists() or path.read_bytes() != data:
        path.write_bytes(data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", type=Path, default=ROOT / "tmp/quickjs-build")
    parser.add_argument("--emcc", default="emcc", help="emsdk 5.0.1 emcc executable; honors EM_CONFIG")
    args = parser.parse_args()
    policy = json.loads(POLICY.read_text(encoding="utf-8"))
    pin = policy["taskContext"]
    version = subprocess.check_output([args.emcc, "--version"], text=True).splitlines()[0]
    if not re.search(r"\b" + re.escape(pin["toolchainVersion"]) + r"\b", version):
        raise RuntimeError(f"Expected Emscripten {pin['toolchainVersion']}, got {version}")
    patch = ROOT / pin["patch"]["path"]
    patch_data = patch.read_bytes()
    if sha256(patch_data) != pin["patch"]["sha256"]:
        raise RuntimeError("Task context patch differs from the reviewed policy")
    if sha256(Path(__file__).read_bytes()) != pin["build"]["sha256"]:
        raise RuntimeError("Build script differs from the reviewed policy")
    work = args.work_dir.resolve()
    work.mkdir(parents=True, exist_ok=True)
    archive = work / "upstream.tar.gz"
    if not archive.exists():
        url = f"https://codeload.github.com/justjake/quickjs-emscripten/tar.gz/{pin['gitHead']}"
        with urllib.request.urlopen(url, timeout=60) as response:
            data = response.read()
        if sha256(data) != pin["upstreamArchiveSha256"]:
            raise RuntimeError("Upstream archive hash mismatch")
        archive.write_bytes(data)
    if sha256(archive.read_bytes()) != pin["upstreamArchiveSha256"]:
        raise RuntimeError("Upstream archive hash mismatch")

    # Materialize only the pinned native build inputs. Compare bytes before
    # writing so repeat invocations retain the upstream make/object cache.
    inputs = {}
    with tarfile.open(archive, "r:gz") as source:
        for member in source.getmembers():
            name = PurePosixPath(member.name)
            relative = PurePosixPath(*name.parts[1:])
            if ".." in relative.parts or relative.is_absolute():
                raise RuntimeError("Unsafe upstream archive path")
            path = relative.as_posix()
            needed = (path.startswith(("vendor/quickjs/", "c/", "templates/"))
                      or path in (f"{VARIANT}/Makefile", "scripts/emcc.sh",
                                  "exportedRuntimeMethods.json"))
            if needed and member.isfile():
                inputs[path] = source.extractfile(member).read()
            elif needed and member.issym():
                raise RuntimeError(f"Unexpected source symlink: {path}")
    # Patch a tiny temporary tree, not the cached tree, so a patch update never
    # silently keeps part of an older patch or forces a clean rebuild.
    with tempfile.TemporaryDirectory(prefix="quickjs-patch-") as directory:
        stage = Path(directory)
        for name in PATCHED:
            write_changed(stage / name, inputs[name])
        subprocess.run(["patch", "--batch", "--fuzz=0", "-p1", "-i", str(patch)],
                       cwd=stage, check=True)
        for name in PATCHED:
            inputs[name] = (stage / name).read_bytes()
    source = work / "source"
    for name, data in inputs.items():
        write_changed(source / name, data)
    (source / "scripts/emcc.sh").chmod(0o755)

    # The upstream generate.ts needs a Node dev toolchain just to select C
    # exports. These are its release-sync (DEBUG=false, ASYNCIFY=false) rules.
    interface = inputs["c/interface.c"].decode("utf-8")
    declarations = re.findall(r"^([\w()* ]+[\s*]+)(QTS_\w+)\((.*?)\) ?\{$",
                              interface, re.MULTILINE)
    declarations = [(kind, name) for kind, name, _ in declarations
                    if not re.search(r"\b(?:DebugOnly|AsyncifyOnly)\(", kind)]
    symbols = ["_" + name for _, name in declarations] + ["_malloc", "_free"]
    sync_symbols = [name for kind, name in declarations if "MaybeAsync(" not in kind]
    async_imports = re.findall(r"^\s*EM_JS\(MaybeAsync\([^,\n]+,\s*(\w+),",
                               interface, re.MULTILINE)
    wrapper = source / VARIANT / "build/wrapper"
    for name, values in (("symbols", symbols), ("asyncify-remove", sync_symbols),
                         ("asyncify-imports", async_imports)):
        write_changed(wrapper / f"{name}.json",
                      (json.dumps(values, separators=(",", ":")) + "\n").encode())

    # Upstream's compact Makefile omits header/template/flag dependencies.
    # Add one content-addressed prerequisite in its existing build directory;
    # unchanged inputs keep the normal incremental object/link cache valid.
    stamp = hashlib.sha256()
    for name, data in sorted(inputs.items()):
        stamp.update(name.encode() + b"\0" + data + b"\0")
    stamp.update(version.encode() + b"\0" + Path(__file__).read_bytes())
    build = source / VARIANT / "build"
    write_changed(build / "reviewed-inputs", (stamp.hexdigest() + "\n").encode())
    write_changed(build / "reviewed-inputs.mk",
                  b"$(BUILD_WRAPPER)/interface.o $(VARIANT_QUICKJS_OBJS) "
                  b"dist/emscripten-module.mjs: build/reviewed-inputs\n")
    subprocess.run(["make", "-f", "Makefile", "-f", "build/reviewed-inputs.mk",
                    "-j", str(os.cpu_count() or 1), "dist/emscripten-module.mjs",
                    f"EMCC={shutil.which(args.emcc) or args.emcc}",
                    "CFLAGS_ESM=-s EXPORT_ES6=1 -s ENVIRONMENT=node"],
                   cwd=source / VARIANT, check=True)
    for artifact in pin["artifacts"]:
        destination = ROOT / artifact["path"]
        data = (source / VARIANT / "dist" / destination.name).read_bytes()
        write_changed(destination, data)
        print(json.dumps({"path": artifact["path"], "bytes": len(data), "sha256": sha256(data)}))


if __name__ == "__main__":
    main()
