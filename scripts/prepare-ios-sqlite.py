"""Build the single pinned SQLite framework used by Swift and embedded Node."""
import argparse
import hashlib
import json
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

_SCRIPTS_DIR = str(Path(__file__).resolve().parent)
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

from ios_prepare_common import digest, download_with_proxy, file_lock

ROOT = Path(__file__).resolve().parents[1]
LOCK_PATH = ROOT / "native/SQLite/sqlite.json"
TARGETS = {
    "macos": ("macosx", ("x86_64", "arm64"), "15.0", None),
    "ios-arm64": ("iphoneos", ("arm64",), "18.0", None),
    "ios-simulator": ("iphonesimulator", ("x86_64", "arm64"), "18.0", "simulator"),
}


def run(*args):
    subprocess.run(args, check=True)


def source(lock, cache):
    directory = cache / lock["version"] / "source"
    directory.mkdir(parents=True, exist_ok=True)
    files = {"sqlite3.c": lock["sourceSha256"], "sqlite3.h": lock["headerSha256"]}
    if not all((directory / name).is_file() for name in files):
        temporary = Path.home() / "Developer/temp"
        temporary.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="sqlite-download-", dir=temporary) as work:
            archive = Path(work) / "sqlite.zip"
            download_with_proxy(lock["url"], archive, proxy_timeout=10, direct_timeout=60)
            if digest(archive) != lock["archiveSha256"]:
                raise RuntimeError("SQLite archive hash differs")
            with zipfile.ZipFile(archive) as package:
                for name in files:
                    member = next(item for item in package.namelist() if item.endswith("/" + name))
                    (directory / name).write_bytes(package.read(member))
    for name, expected in files.items():
        if digest(directory / name) != expected:
            raise RuntimeError("SQLite source hash differs: " + name)
    return directory


def build(target, lock, source_path, cache):
    sdk, architectures, minimum, variant = TARGETS[target]
    sdk_path = subprocess.check_output(["xcrun", "--sdk", sdk, "--show-sdk-path"], text=True).strip()
    compiler = subprocess.check_output(["xcrun", "--sdk", sdk, "--find", "clang"], text=True).strip()
    identity = hashlib.sha256(LOCK_PATH.read_bytes() + Path(__file__).read_bytes() + sdk_path.encode() +
                              subprocess.check_output([compiler, "--version"])).hexdigest()
    directory = cache / "artifacts" / identity / target
    framework = directory / "MeowSQLite.framework"
    receipt_path = directory / "receipt.json"
    if receipt_path.is_file():
        receipt = json.loads(receipt_path.read_text())
        if all((framework / file).is_file() and digest(framework / file) == expected
               for file, expected in receipt["files"].items()):
            return framework, receipt
        raise RuntimeError("Shared SQLite artifact hash differs")
    directory.mkdir(parents=True, exist_ok=True)
    header = (source_path / "sqlite3.h").read_text()
    symbols = sorted(set(re.findall(r"\bsqlite3_[A-Za-z0-9_]+(?=\s*\()", header)) |
                     {"sqlite3_version", "sqlite3_temp_directory", "sqlite3_data_directory"})
    prefix = directory / "prefix.h"
    prefix.write_text("".join(f"#define {name} meow_{name}\n" for name in symbols))
    headers = framework / "Headers"
    headers.mkdir(parents=True, exist_ok=True)
    # redefine_extname is an assembler name, including Mach-O's leading '_'.
    # Both Swift and C++ retain sqlite3_* API names and bind the prefixed core.
    (headers / "sqlite3.h").write_text(
        "".join(f"#pragma redefine_extname {name} _meow_{name}\n" for name in symbols) + header +
        "\n#ifdef __cplusplus\nextern \"C\" {\n#endif\n"
        "int meow_sqlite3_disable_close_checkpoint(sqlite3 *database);\n"
        "#ifdef __cplusplus\n}\n#endif\n")
    configuration = directory / "configuration.c"
    configuration.write_text(
        '#include "sqlite3.h"\n'
        'SQLITE_API int meow_sqlite3_disable_close_checkpoint(sqlite3 *database) {\n'
        '    return sqlite3_db_config(database, SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE, 1, 0);\n}\n')
    modules = framework / "Modules"
    modules.mkdir(parents=True, exist_ok=True)
    (modules / "module.modulemap").write_text(
        'framework module MeowSQLite { umbrella header "sqlite3.h" export * }\n')
    outputs = []
    for arch in architectures:
        platform = "macos" if sdk == "macosx" else "ios"
        triple = f"{arch}-apple-{platform}{minimum}" + ("-simulator" if variant else "")
        output = directory / ("sqlite-" + arch)
        run(compiler, "-target", triple, "-isysroot", sdk_path, "-O2", "-fvisibility=hidden",
            "-dynamiclib", "-install_name", "@rpath/MeowSQLite.framework/MeowSQLite",
            "-DSQLITE_API=__attribute__((visibility(\"default\")))",
            *("-D" + option for option in lock["compileOptions"]),
            "-I", str(source_path), "-include", str(prefix), str(source_path / "sqlite3.c"),
            str(configuration), "-o", str(output))
        outputs.append(output)
    if len(outputs) > 1:
        run("xcrun", "lipo", "-create", *(str(file) for file in outputs), "-output", str(framework / "MeowSQLite"))
    else:
        shutil.copy2(outputs[0], framework / "MeowSQLite")
    with (framework / "Info.plist").open("wb") as file:
        plistlib.dump({"CFBundleIdentifier": "com.meowfilm.sqlite", "CFBundleName": "MeowSQLite",
                       "CFBundleExecutable": "MeowSQLite", "CFBundlePackageType": "FMWK",
                       "CFBundleShortVersionString": lock["version"], "CFBundleVersion": lock["version"],
                       "MinimumOSVersion": minimum}, file)
    exported = subprocess.check_output(["xcrun", "nm", "-gUj", "-arch", architectures[0],
                                        str(framework / "MeowSQLite")], text=True).splitlines()
    if not exported or any(not name.startswith("_meow_sqlite3_") for name in exported):
        raise RuntimeError("SQLite export boundary differs")
    probe = directory / "linkage.c"
    probe.write_text('#include <MeowSQLite/sqlite3.h>\n'
                     'int meow_storage_linkage(void) { return sqlite3_libversion_number(); }\n')
    for arch in architectures:
        platform = "macos" if sdk == "macosx" else "ios"
        triple = f"{arch}-apple-{platform}{minimum}" + ("-simulator" if variant else "")
        linked = directory / ("linkage-" + arch)
        run(compiler, "-target", triple, "-isysroot", sdk_path, "-dynamiclib",
            "-F", str(directory), "-framework", "MeowSQLite", str(probe), "-o", str(linked))
        linked.unlink()
    probe.unlink()
    receipt = {"version": lock["version"], "target": target, "identity": identity,
               "lockSha256": digest(LOCK_PATH), "buildScriptSha256": digest(Path(__file__)),
               "compileOptions": lock["compileOptions"],
               "files": {str(path.relative_to(framework)): digest(path)
                         for path in sorted(framework.rglob("*")) if path.is_file()}}
    receipt_path.write_text(json.dumps(receipt, indent=2) + "\n")
    for output in outputs:
        output.unlink()
    return framework, receipt


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", choices=TARGETS, action="append", required=True)
    args = parser.parse_args()
    lock = json.loads(LOCK_PATH.read_text())
    cache = Path.home() / "Developer/cache/sqlite"
    cache.mkdir(parents=True, exist_ok=True)
    output = ROOT / "native/SQLite/.cache/Artifacts"
    output.mkdir(parents=True, exist_ok=True)
    with file_lock(cache / "build.lock"):
        source_path = source(lock, cache)
        receipts = {}
        for target in TARGETS:
            recorded = output / target / "sqlite-build.json"
            if recorded.is_file():
                receipts[target] = json.loads(recorded.read_text())
        for target in dict.fromkeys(args.target):
            framework, receipt = build(target, lock, source_path, cache)
            destination = output / target / "MeowSQLite.framework"
            destination.parent.mkdir(parents=True, exist_ok=True)
            if destination.exists():
                shutil.rmtree(destination)
            shutil.copytree(framework, destination)
            receipts[target] = receipt
        xcframework = output / "MeowSQLite.xcframework"
        with tempfile.TemporaryDirectory(prefix="sqlite-package-", dir=output) as directory:
            packaged = Path(directory) / "MeowSQLite.xcframework"
            frameworks = [output / target / "MeowSQLite.framework" for target in TARGETS
                          if (output / target / "MeowSQLite.framework").is_dir()]
            run("xcodebuild", "-create-xcframework",
                *(argument for framework in frameworks for argument in ("-framework", str(framework))),
                "-output", str(packaged))
            if xcframework.exists():
                shutil.rmtree(xcframework)
            shutil.move(packaged, xcframework)
        information = plistlib.loads((xcframework / "Info.plist").read_bytes())
        for target, receipt in receipts.items():
            _, architectures, _, variant = TARGETS[target]
            platform = "macos" if target == "macos" else "ios"
            selected = [item for item in information["AvailableLibraries"]
                        if item["SupportedPlatform"] == platform
                        and item.get("SupportedPlatformVariant") == variant
                        and set(item["SupportedArchitectures"]) == set(architectures)]
            if len(selected) != 1:
                raise RuntimeError("SQLite XCFramework target differs")
            receipt["xcframeworkIdentifier"] = selected[0]["LibraryIdentifier"]
            (output / target / "sqlite-build.json").write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__":
    main()
