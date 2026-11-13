"""Check the current native source/dependency boundary, without compiling code."""
from pathlib import Path
import json
import re
import subprocess
import sys

# App source directories are declared by the project generator, not duplicated here.
import runpy
APPLICATION_DIRECTORIES = set(runpy.run_path(str(Path(__file__).with_name("generate-ios-project.py")))["APPLICATION_DIRECTORIES"])
SHARED_TARGETS = {"MeowContracts", "MeowJSON", "MeowStorage", "MeowAccounts", "MeowPlayback", "MeowUI", "MeowRuntime"}
# Native packages linked by the current client.
LOCAL_PRODUCTS = {
    "MeowVideoKernel": ("videokernel", "../VideoKernel"),
}

def application_membership(base, project_text):
    """Exact source closure in each target, not just PBXFileReference presence."""
    failures = []
    expected_app, expected_test, expected_runtime = set(), set(), set()
    for path in base.rglob("*.swift"):
        relative = path.relative_to(base)
        if relative.parts[0] == ".cache":
            continue
        if relative.parts[0] in APPLICATION_DIRECTORIES:
            expected_app.add(relative.as_posix())
        elif relative.parts[0] == "UITests":
            expected_test.add(relative.as_posix())
        elif relative.parts[0] == "RuntimeTests":
            expected_runtime.add(relative.as_posix())
        else:
            failures.append("Unowned App source: " + relative.as_posix())
    for directory in APPLICATION_DIRECTORIES | {"UITests", "RuntimeTests"}:
        if not any((base / directory).rglob("*.swift")):
            failures.append("Empty App source directory: " + directory)
    objects = dict(re.findall(r"^\s*([A-F0-9]{24}) = \{ (.*) \};$", project_text, re.M))
    referenced = set()
    for target_name, expected in (
        ("Meowfilm", expected_app),
        ("MeowfilmAcceptanceUITests", expected_test),
        ("MeowfilmRuntimeTests", expected_runtime),
    ):
        targets = [text for text in objects.values()
                   if "isa = PBXNativeTarget;" in text and "name = " + target_name + ";" in text]
        if len(targets) != 1:
            failures.append("Invalid App target membership: " + target_name)
            continue
        phases = re.search(r"buildPhases = \(([^)]*)\)", targets[0])
        phase_ids = re.findall(r"[A-F0-9]{24}", phases[1]) if phases else []
        sources = [objects[p] for p in phase_ids if p in objects and "isa = PBXSourcesBuildPhase;" in objects[p]]
        actual = []
        if len(sources) != 1:
            failures.append("Invalid sources phase: " + target_name)
            continue
        files = re.search(r"files = \(([^)]*)\)", sources[0])
        for build_id in re.findall(r"[A-F0-9]{24}", files[1] if files else ""):
            reference = re.search(r"fileRef = ([A-F0-9]{24});", objects.get(build_id, ""))
            item = objects.get(reference[1], "") if reference else ""
            path = re.search(r'path = "([^"]+\.swift)";', item)
            if not path:
                failures.append("Invalid source build reference: " + build_id)
                continue
            actual.append(path[1])
        referenced.update(actual)
        if set(actual) != expected or len(actual) != len(expected):
            failures.append("Source membership for {}: missing={}, extra={}, duplicates={}".format(
                target_name, sorted(expected - set(actual)), sorted(set(actual) - expected),
                len(actual) - len(set(actual))))
    all_references = re.findall(r'lastKnownFileType = sourcecode.swift; path = "([^"]+)";', project_text)
    if set(all_references) != referenced or len(all_references) != len(referenced):
        failures.append("Orphaned or duplicate Swift source reference")
    return failures


def runtime_membership(project_text):
    """The App owns one embedded framework; hosted tests share that instance."""
    failures = []
    objects = dict(re.findall(r"^\s*([A-F0-9]{24}) = \{ (.*) \};$", project_text, re.M))
    references = [key for key, text in objects.items() if "isa = PBXFileReference;" in text
                  and "name = MeowScriptHost.framework;" in text]
    if len(references) != 1:
        return ["Expected one MeowScriptHost framework reference"]
    reference = references[0]
    expected = "../../.cache/ios-native/$(MEOW_RUNTIME_SDK)-$(MEOW_RUNTIME_ARCH)/MeowScriptHost.framework"
    if f'path = "{expected}";' not in objects[reference]:
        failures.append("Script host framework must use the selected SDK/architecture input")
    builds = [key for key, text in objects.items() if "isa = PBXBuildFile;" in text
              and f"fileRef = {reference};" in text]
    if len(builds) != 1 or "CodeSignOnCopy" not in objects[builds[0]]:
        return failures + ["Script host framework must have one signed embed build file"]
    build = builds[0]
    phases = [key for key, text in objects.items()
              if re.search(r"\b" + build + r"\b", text) and "isa = PBXBuildFile;" not in text]
    if len(phases) != 1 or "isa = PBXCopyFilesBuildPhase;" not in objects[phases[0]] or "dstSubfolderSpec = 10;" not in objects[phases[0]]:
        return failures + ["Script host framework must only join Embed Frameworks"]
    owners = [text for text in objects.values() if "isa = PBXNativeTarget;" in text
              and phases[0] in text]
    if len(owners) != 1 or "name = Meowfilm;" not in owners[0]:
        failures.append("Only the App may embed MeowScriptHost")
    return failures


def evaluate_package(package):
    """Evaluate PackageDescription without resolving dependencies or building targets."""
    result = subprocess.run(
        ["swift", "package", "--package-path", str(package), "dump-package"],
        check=True, capture_output=True, text=True, encoding="utf-8")
    return json.loads(result.stdout)


def check(root):
    # macOS exposes temporary directories through /var while their canonical
    # location is /private/var. Anchor every traversal to one canonical root so
    # that this system alias is not misclassified as a linked source.
    root = Path(root).resolve()
    failures = []
    package = root / "native/MeowKit"
    try:
        manifest = evaluate_package(package)
    except (OSError, subprocess.CalledProcessError, json.JSONDecodeError) as error:
        return ["Cannot evaluate Swift package manifest: " + str(error)]
    local_packages = {}
    for dependency in manifest["dependencies"]:
        for item in dependency.get("fileSystem", []):
            local_packages[item["identity"]] = Path(item["path"]).resolve()
        if set(dependency) != {"fileSystem"}:
            failures.append("Unapproved package source")
    allowed_paths = {(package / path).resolve() for _, path in LOCAL_PRODUCTS.values()}
    if set(local_packages.values()) - allowed_paths:
        failures.append("Unapproved package path")
    external = set()
    targets = {}
    kinds = {}
    for declaration in manifest["targets"]:
        name, kind = declaration["name"], declaration["type"]
        if name in targets:
            failures.append("Duplicate target: " + name)
        dependencies = set()
        for dependency in declaration["dependencies"]:
            if "product" in dependency:
                product, identity = dependency["product"][:2]
                expected = LOCAL_PRODUCTS.get(product)
                if (expected is None or identity != expected[0]
                        or local_packages.get(identity) != (package / expected[1]).resolve()):
                    failures.append("Unapproved local product: " + product)
                else:
                    external.add(product)
                dependencies.add(product)
            else:
                dependencies.add((dependency.get("target") or dependency["byName"])[0])
        targets[name] = dependencies
        kinds[name] = kind
    sources = {name for name, kind in kinds.items() if kind == "regular"}
    tests = {name for name, kind in kinds.items() if kind == "test"}
    if not sources:
        failures.append("No package source targets")
    for folder, expected in (("Sources", sources), ("Tests", tests)):
        parent = package / folder
        actual = {p.name for p in parent.iterdir() if p.is_dir()} if parent.exists() else set()
        if actual != expected:
            failures.append("{} directories: missing={}, extra={}".format(folder, sorted(expected - actual), sorted(actual - expected)))
        for target in sorted(expected):
            if not list((parent / target).rglob("*.swift")):
                failures.append("Empty target: " + target)
    product_targets = [(item["name"], item["targets"]) for item in manifest["products"]]
    products = {name for name, _ in product_targets}
    if len(products) != len(product_targets):
        failures.append("Duplicate package product")
    for name, members in product_targets:
        linked = set(members)
        if not linked or not linked <= sources:
            failures.append("Invalid package product targets: " + name)
    for target, dependencies in targets.items():
        if not dependencies <= sources | external:
            failures.append("Non-source dependency: " + target)
        folder = "Sources" if target in sources else "Tests"
        imported = set()
        for path in (package / folder / target).rglob("*.swift"):
            if path.is_symlink() or not path.resolve().is_relative_to((package / folder / target).resolve()):
                failures.append("Linked source: " + str(path.relative_to(root)))
                continue
            text = path.read_text(encoding="utf-8")
            imported.update(re.findall(r"^(?:@testable )?import (Meow\w+)", text, re.M))
        if not imported <= dependencies:
            failures.append("Undeclared imports for {}: {}".format(target, sorted(imported - dependencies)))
        if target in SHARED_TARGETS:
            if any(name.startswith(("MeowMusic", "MeowFilm", "MeowNovel")) for name in dependencies):
                failures.append("Shared target depends on business: " + target)
    def walk(name, path):
        if name in path:
            failures.append("Dependency cycle: " + " -> ".join(path + [name]))
            return
        for dependency in targets.get(name, ()):
            if dependency in targets:
                walk(dependency, path + [name])
    for name in sources:
        walk(name, [])
    for parent in (root / "apps/ios", package / "Sources", package / "Tests"):
        for path in parent.rglob("*"):
            if parent == root / "apps/ios" and path.relative_to(parent).parts[0] == ".cache":
                continue
            if path.is_symlink() or path.resolve() != path.absolute():
                failures.append("Linked source: " + str(path.relative_to(root)))
    project = root / "apps/ios/Meowfilm.xcodeproj/project.pbxproj"
    if project.exists():
        text = project.read_text(encoding="utf-8")
        app_products = re.findall(r"isa = XCSwiftPackageProductDependency;[^}]*?\bproductName = (Meow\w+);", text)
        if len(app_products) != len(set(app_products)) or not set(app_products) <= products:
            failures.append("Xcode refers to duplicate or undeclared package products")
        failures.extend(application_membership(root / "apps/ios", text))
        failures.extend(runtime_membership(text))
    else:
        failures.append("Missing Xcode project")
    return failures


if __name__ == "__main__":
    failures = check(Path(__file__).resolve().parents[1])
    if failures:
        print("\n".join(failures), file=sys.stderr)
        sys.exit(1)
    print("Native source membership and dependency checks passed.")
