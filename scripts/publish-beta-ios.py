"""Publish a rolling, non-Latest beta prerelease using only GITHUB_TOKEN."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys

TAG = "beta-ios"
ASSETS = ("meowfilm-beta-ios.ipa", "meowfilm-beta-ios.sha256", "beta-ios-build.json")


def api(endpoint, method="GET", data=None, missing=False):
    command = ["gh", "api", "--method", method, endpoint]
    if data is not None:
        command += ["--input", "-"]
    result = subprocess.run(command, input=None if data is None else json.dumps(data),
                            text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        if missing and "(HTTP 404)" in result.stderr:
            return None
        raise RuntimeError(result.stderr.strip())
    return json.loads(result.stdout) if result.stdout.strip() else None


def main(directory):
    repo = os.environ["GH_REPO"]
    sha = os.environ["GITHUB_SHA"]
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo) or not re.fullmatch(r"[a-f0-9]{40}", sha):
        raise ValueError("Invalid repository or source commit")
    if repo != os.environ["GITHUB_REPOSITORY"] or os.environ["GITHUB_REF"] != "refs/heads/main":
        raise ValueError("Only this repository's main branch can publish beta-ios")
    base = f"repos/{repo}"
    metadata_path = Path(directory).resolve() / "beta-ios-build.json"
    metadata = json.loads(metadata_path.read_text())
    if (metadata["commit"] != sha or metadata["sourceModified"] or
            metadata["bundleIdentifier"] != "dev.meowfilm.aggregate" or "runURL" not in metadata):
        raise ValueError("Build identity mismatch")
    assets = [metadata_path.parent / name for name in ASSETS]
    if not all(path.is_file() for path in assets):
        raise ValueError("Incomplete release assets")
    if api(base + "/git/ref/heads/main")["object"]["sha"] != sha:
        print("A newer main commit exists; this obsolete run will not replace beta-ios.")
        return
    # Repository immutability is checked during setup with the owner's CLI.
    # Its settings endpoint needs Administration, which this CI token never has.
    release = api(base + f"/releases/tags/{TAG}", missing=True)
    if release and (release.get("immutable") or not release["prerelease"]):
        raise RuntimeError("Refusing to overwrite an immutable or non-beta release")
    notes = (
        f"## meowfilm beta-ios\n\n"
        f"- Version: `{metadata['version']}` / build `{metadata['buildVersion']}`\n"
        f"- Commit: `{sha}`\n"
        f"- Bundle ID: `{metadata['bundleIdentifier']}` (stable)\n"
        f"- Target: iPhone / arm64 / iOS {metadata['minimumOSVersion']}+\n"
        f"- Package: unsigned IPA; install and sign with AltStore.\n"
        f"- CI uses no Apple certificate, provisioning profile or account.\n"
        f"- AltStore/Apple signing, refresh and App ID limits still apply.\n"
        f"- Build only; no device installation or business/UI acceptance was performed.\n"
        f"- [Build run]({metadata['runURL']})\n\n"
        f"SHA-256 (`{metadata['ipa']}`): `{metadata['ipaSHA256']}`\n"
    )
    if release is None:
        release = api(base + "/releases", "POST", {
            "tag_name": TAG, "target_commitish": sha, "name": TAG, "body": notes,
            "draft": True, "prerelease": True, "make_latest": "false",
        })
    # Only completed build products reach this step. Never delete the release.
    subprocess.run(["gh", "release", "upload", TAG, *(str(p) for p in assets),
                    "--repo", repo, "--clobber"], check=True)
    tag = api(base + f"/git/ref/tags/{TAG}", missing=True)
    if tag is None:
        api(base + "/git/refs", "POST", {"ref": f"refs/tags/{TAG}", "sha": sha})
    elif tag["object"]["sha"] != sha:
        # Only this explicitly designated rolling beta tag is ever moved.
        api(base + f"/git/refs/tags/{TAG}", "PATCH", {"sha": sha, "force": True})
    release = api(base + f"/releases/{release['id']}", "PATCH", {
        "name": TAG, "body": notes, "draft": False, "prerelease": True,
        "make_latest": "false", "target_commitish": sha,
    })
    if release["draft"] or not release["prerelease"]:
        raise RuntimeError("The result is not a published prerelease")
    latest = api(base + "/releases/latest", missing=True)
    if latest and latest["id"] == release["id"]:
        raise RuntimeError("beta-ios must never be Latest")
    if api(base + f"/git/ref/tags/{TAG}")["object"]["sha"] != sha:
        raise RuntimeError("Published beta tag does not match the IPA commit")
    print(json.dumps({"tag": TAG, "commit": sha, "release": release["html_url"],
                      "prerelease": True, "latest": False}, indent=2))
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
        stream.write(f"## beta-ios\n\n[Download unsigned IPA]({release['html_url']})\n\n"
                     f"Commit: `{sha}` · build `{metadata['buildVersion']}` · no Actions artifacts.\n")


if __name__ == "__main__":
    main(sys.argv[1])
