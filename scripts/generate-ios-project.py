"""Generate the meowfilm iPhone app Xcode project."""
from pathlib import Path
import argparse
import hashlib
import plistlib

ROOT = Path(__file__).resolve().parents[1]

APPLICATION_DIRECTORIES = ("Application", "Composition", "Debug", "Navigation", "Presentation")
# Prepared by scripts/prepare-ios-runtime.py; regenerable inputs, not sources.
NODE_MOBILE_FRAMEWORK = "../../.cache/nodejs-mobile/ios/NodeMobile.xcframework"
RUNTIME_PAYLOAD = "../../.cache/ios-runtime/$(MEOW_RUNTIME_SDK)-$(MEOW_RUNTIME_ARCH)/Runtime"
SCRIPT_HOST_FRAMEWORK = "../../.cache/ios-native/$(MEOW_RUNTIME_SDK)-$(MEOW_RUNTIME_ARCH)/MeowScriptHost.framework"
NODE_MOBILE_LICENSE = "../../LICENSES/nodejs-mobile-LICENSE.txt"
NATIVE_JAVASCRIPT_LICENSE = "../../LICENSES/quickjs-ng-LICENSE.txt"
NATIVE_UNICODE_LICENSE = "../../LICENSES/Unicode-3.0.txt"
PROJECT_NOTICES = "../../LICENSES/NOTICE.md"
PROJECT_LICENSE = "../../LICENSES/GPL-3.0-only.txt"
VIDEO_LICENSES = ("LGPL-2.1-only.txt", "LGPL-3.0-only.txt", "FFmpeg-LICENSE.md", "FFmpeg-IJG-NOTICE.txt")
AUDIO_LICENSES = ("FFmpeg-audio-NOTICES.txt",)
AETHER_LICENSES = ("AetherEngine-EXCEPTION.txt", "Aether-THIRD-PARTY-NOTICES.txt",
                   "WTFPL-2.0.txt", "Aether-libzvbi-ure-MIT.txt",
                   "libxml2-MIT.txt", "dav1d-BSD-2-Clause.txt")


def source_paths(root, directories):
    """Only owned source directories; resources/archives never join the target."""
    base = root / "apps/ios"
    result = []
    for directory in directories:
        folder = base / directory
        if folder.is_symlink() or folder.resolve() != folder.absolute():
            raise ValueError("Linked source directory: " + directory)
        for path in sorted(folder.rglob("*")):
            if path.is_symlink() or path.resolve() != path.absolute() or not path.resolve().is_relative_to(base.resolve()):
                raise ValueError("Linked or escaped source: " + str(path))
            if path.is_file() and path.suffix == ".swift":
                result.append(path.relative_to(base).as_posix())
    if not result:
        raise ValueError("Empty source group: " + ", ".join(directories))
    return sorted(result)

def identifier(value):
    return hashlib.sha256(value.encode()).hexdigest()[:24].upper()

def quoted(value):
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'

def write_lf(path, text):
    # Xcode's bundled Python 3.9 supports newline on open(), not write_text().
    with path.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(text)

def generate():
    product, name = "meowfilm", "Meowfilm"
    app_sources = source_paths(ROOT, APPLICATION_DIRECTORIES)
    all_sources = {p.relative_to(ROOT / "apps/ios").as_posix()
                   for p in (ROOT / "apps/ios").rglob("*.swift")
                   if p.relative_to(ROOT / "apps/ios").parts[0] != ".cache"}
    if all_sources != set(app_sources):
        raise ValueError("App Swift sources must belong to a declared application directory")
    bundle_identifier = "dev.meowfilm.aggregate"
    base_info = plistlib.loads((ROOT / "apps/ios/Info.plist").read_bytes())
    project = ROOT / f"apps/ios/{name}.xcodeproj"
    project.mkdir(parents=True, exist_ok=True)
    ids = {key: identifier(f"{product}:{key}") for key in [
        "project", "mainGroup", "productsGroup", "app",
        "target", "sources", "frameworks", "resources", "assets", "assetsBuild", "package",
        "projectConfigs", "targetConfigs", "projectDebug", "projectRelease", "targetDebug", "targetRelease",
    ]}
    ids.update({key: identifier(f"{product}:{key}") for key in [
        "nodeFramework", "nodeFrameworkLink", "nodeFrameworkEmbed", "embedFrameworks",
        "scriptHostFramework", "scriptHostFrameworkEmbed",
        "runtimePayload", "runtimePayloadBuild", "nodeLicense", "nodeLicenseBuild",
        "javascriptLicense", "javascriptLicenseBuild",
        "unicodeLicense", "unicodeLicenseBuild",
        "projectNotices", "projectNoticesBuild", "projectLicense", "projectLicenseBuild",
    ]})
    libraries = ["MeowContracts", "MeowDebug", "MeowStorage", "MeowPlayback", "MeowPlaybackVideo", "MeowAccounts", "MeowUI", "MeowRuntime",
                 "MeowMusicFeature", "MeowMusicPlatform", "MeowMusicNativeService", "MeowFilmFeature", "MeowNovelFeature"]
    objects = []
    def obj(key, content):
        objects.append(f"\t\t{ids[key]} = {{ {content} }};")
    def seq(values):
        return "(" + ", ".join(values) + ("," if values else "") + ")"
    def source_objects(paths):
        references, builds = [], []
        for path in paths:
            reference, build = identifier(f"{product}:source:{path}"), identifier(f"{product}:build:{path}")
            references.append(reference)
            builds.append(build)
            objects.append(f'\t\t{reference} = {{ isa = PBXFileReference; lastKnownFileType = sourcecode.swift; path = {quoted(path)}; sourceTree = "<group>"; }};')
            objects.append(f"\t\t{build} = {{ isa = PBXBuildFile; fileRef = {reference}; }};")
        return references, builds
    app_references, app_builds = source_objects(app_sources)
    obj("assets", 'isa = PBXFileReference; lastKnownFileType = folder.assetcatalog; path = Assets.xcassets; sourceTree = "<group>";')
    obj("assetsBuild", f"isa = PBXBuildFile; fileRef = {ids['assets']};")
    # The embedded runtime: the pinned Node Mobile framework (linked, embedded and
    # re-signed), the complete JS payload copied verbatim, and its license text.
    obj("nodeFramework", f'isa = PBXFileReference; lastKnownFileType = wrapper.xcframework; name = NodeMobile.xcframework; path = {quoted(NODE_MOBILE_FRAMEWORK)}; sourceTree = "<group>";')
    obj("nodeFrameworkLink", f"isa = PBXBuildFile; fileRef = {ids['nodeFramework']};")
    obj("nodeFrameworkEmbed", f"isa = PBXBuildFile; fileRef = {ids['nodeFramework']}; settings = {{ATTRIBUTES = (CodeSignOnCopy, RemoveHeadersOnCopy, ); }};")
    obj("scriptHostFramework", f'isa = PBXFileReference; lastKnownFileType = wrapper.framework; name = MeowScriptHost.framework; path = {quoted(SCRIPT_HOST_FRAMEWORK)}; sourceTree = "<group>";')
    obj("scriptHostFrameworkEmbed", f"isa = PBXBuildFile; fileRef = {ids['scriptHostFramework']}; settings = {{ATTRIBUTES = (CodeSignOnCopy, RemoveHeadersOnCopy, ); }};")
    obj("embedFrameworks", f'isa = PBXCopyFilesBuildPhase; buildActionMask = 2147483647; dstPath = ""; dstSubfolderSpec = 10; files = ({ids["nodeFrameworkEmbed"]}, {ids["scriptHostFrameworkEmbed"]},); name = "Embed Frameworks"; runOnlyForDeploymentPostprocessing = 0;')
    obj("runtimePayload", f'isa = PBXFileReference; lastKnownFileType = folder; name = Runtime; path = {quoted(RUNTIME_PAYLOAD)}; sourceTree = "<group>";')
    obj("runtimePayloadBuild", f"isa = PBXBuildFile; fileRef = {ids['runtimePayload']};")
    obj("nodeLicense", f'isa = PBXFileReference; lastKnownFileType = text; name = nodejs-mobile-LICENSE.txt; path = {quoted(NODE_MOBILE_LICENSE)}; sourceTree = "<group>";')
    obj("javascriptLicense", f'isa = PBXFileReference; lastKnownFileType = text; name = quickjs-ng-LICENSE.txt; path = {quoted(NATIVE_JAVASCRIPT_LICENSE)}; sourceTree = "<group>";')
    obj("javascriptLicenseBuild", f"isa = PBXBuildFile; fileRef = {ids['javascriptLicense']};")
    obj("unicodeLicense", f'isa = PBXFileReference; lastKnownFileType = text; name = Unicode-3.0.txt; path = {quoted(NATIVE_UNICODE_LICENSE)}; sourceTree = "<group>";')
    obj("unicodeLicenseBuild", f"isa = PBXBuildFile; fileRef = {ids['unicodeLicense']};")
    obj("projectNotices", f'isa = PBXFileReference; lastKnownFileType = text; name = NOTICE.md; path = {quoted(PROJECT_NOTICES)}; sourceTree = "<group>";')
    obj("projectNoticesBuild", f"isa = PBXBuildFile; fileRef = {ids['projectNotices']};")
    obj("projectLicense", f'isa = PBXFileReference; lastKnownFileType = text; name = GPL-3.0-only.txt; path = {quoted(PROJECT_LICENSE)}; sourceTree = "<group>";')
    obj("projectLicenseBuild", f"isa = PBXBuildFile; fileRef = {ids['projectLicense']};")
    obj("nodeLicenseBuild", f"isa = PBXBuildFile; fileRef = {ids['nodeLicense']};")
    media_license_references, media_license_builds = [], []
    for filename in VIDEO_LICENSES + AUDIO_LICENSES + AETHER_LICENSES + ("sqlite-LICENSE.md",):
        reference, build = identifier(f"{product}:license:{filename}"), identifier(f"{product}:license-build:{filename}")
        media_license_references.append(reference)
        media_license_builds.append(build)
        objects.append(f'\t\t{reference} = {{ isa = PBXFileReference; lastKnownFileType = text; name = {quoted(filename)}; path = {quoted("../../LICENSES/" + filename)}; sourceTree = "<group>"; }};')
        objects.append(f"\t\t{build} = {{ isa = PBXBuildFile; fileRef = {reference}; }};")
    obj("app", f'isa = PBXFileReference; explicitFileType = wrapper.application; path = {name}.app; sourceTree = BUILT_PRODUCTS_DIR;')
    obj("productsGroup", f'isa = PBXGroup; children = ({ids["app"]},); name = Products; sourceTree = "<group>";')
    obj("mainGroup", f'isa = PBXGroup; children = {seq(app_references + media_license_references + [ids["assets"], ids["nodeFramework"], ids["scriptHostFramework"], ids["runtimePayload"], ids["nodeLicense"], ids["javascriptLicense"], ids["unicodeLicense"], ids["projectNotices"], ids["projectLicense"], ids["productsGroup"]])}; sourceTree = "<group>";')
    obj("sources", f'isa = PBXSourcesBuildPhase; buildActionMask = 2147483647; files = {seq(app_builds)}; runOnlyForDeploymentPostprocessing = 0;')
    package_ids, framework_ids = [], []
    for library in libraries:
        dependency_id = identifier(f"{product}:dependency:{library}")
        build_id = identifier(f"{product}:framework:{library}")
        package_ids.append(dependency_id)
        framework_ids.append(build_id)
        objects.append(f"\t\t{dependency_id} = {{ isa = XCSwiftPackageProductDependency; package = {ids['package']}; productName = {library}; }};")
        objects.append(f"\t\t{build_id} = {{ isa = PBXBuildFile; productRef = {dependency_id}; }};")
    obj("frameworks", f"isa = PBXFrameworksBuildPhase; buildActionMask = 2147483647; files = {seq(framework_ids + [ids['nodeFrameworkLink']])}; runOnlyForDeploymentPostprocessing = 0;")
    obj("resources", f'isa = PBXResourcesBuildPhase; buildActionMask = 2147483647; files = {seq([ids["assetsBuild"], ids["runtimePayloadBuild"], ids["nodeLicenseBuild"], ids["javascriptLicenseBuild"], ids["unicodeLicenseBuild"], ids["projectNoticesBuild"], ids["projectLicenseBuild"]] + media_license_builds)}; runOnlyForDeploymentPostprocessing = 0;')
    obj("package", 'isa = XCLocalSwiftPackageReference; relativePath = ../../native/MeowKit;')
    common_project = "CLANG_ENABLE_MODULES = YES; IPHONEOS_DEPLOYMENT_TARGET = 18.0; SDKROOT = iphoneos; SWIFT_VERSION = 5.0;"
    conditions = "FEATURE_MUSIC"
    app_info = plistlib.loads(plistlib.dumps(base_info))
    app_info["NSAppTransportSecurity"] = {"NSAllowsArbitraryLoads": True}
    app_info["UIApplicationSceneManifest"] = {
        "UIApplicationSupportsMultipleScenes": False,
        "UISceneConfigurations": {
            "UIWindowSceneSessionRoleApplication": [{
                "UISceneConfigurationName": "Meowfilm",
                "UISceneDelegateClassName": "$(PRODUCT_MODULE_NAME).MeowfilmSceneDelegate",
            }],
        },
    }
    info_file = "Info-Meowfilm.plist"
    (ROOT / "apps/ios" / info_file).write_bytes(plistlib.dumps(app_info, sort_keys=False))
    # Local Simulator ad-hoc signing needs an application identity for Keychain.
    # This file is selected ONLY by the Simulator SDK, never the unsigned
    # iphoneos beta or an AltStore/device signing configuration.
    simulator_entitlements = "Meowfilm-Simulator.entitlements"
    (ROOT / "apps/ios" / simulator_entitlements).write_bytes(plistlib.dumps({
        "application-identifier": "$(PRODUCT_BUNDLE_IDENTIFIER)",
        "get-task-allow": True,
    }, sort_keys=False))
    # Use Xcode's shared products directory, including SwiftPM resource bundles.
    # A target-only output override makes Xcode copy those from a nonexistent path.
    target_settings = (
        f'PRODUCT_BUNDLE_IDENTIFIER = {bundle_identifier}; PRODUCT_NAME = {name}; INFOPLIST_FILE = {info_file}; '
        'GENERATE_INFOPLIST_FILE = NO; TARGETED_DEVICE_FAMILY = 1; '
        'SUPPORTED_PLATFORMS = "iphoneos iphonesimulator"; SUPPORTS_MACCATALYST = NO; '
        'CODE_SIGN_STYLE = Automatic; SWIFT_EMIT_LOC_STRINGS = YES; '
        f'"CODE_SIGN_ENTITLEMENTS[sdk=iphonesimulator*]" = {simulator_entitlements}; '
        'ASSETCATALOG_COMPILER_APPICON_NAME = AppIcon; '
        'LD_RUNPATH_SEARCH_PATHS = "$(inherited) @executable_path/Frameworks"; '
        'MEOW_RUNTIME_SDK = iphoneos; "MEOW_RUNTIME_SDK[sdk=iphonesimulator*]" = iphonesimulator; '
        'MEOW_RUNTIME_ARCH = "$(NATIVE_ARCH_ACTUAL)"; "MEOW_RUNTIME_ARCH[sdk=iphoneos*]" = arm64; '
    )
    # Swift Package modules remain in Xcode's shared products directory.
    target_settings += (
        'SWIFT_INCLUDE_PATHS = ("$(inherited)", "$(BUILD_DIR)/$(CONFIGURATION)$(EFFECTIVE_PLATFORM_NAME)",); '
    )
    for config in ["Debug", "Release"]:
        obj("project" + config, f'isa = XCBuildConfiguration; name = {config}; buildSettings = {{ {common_project} }};')
        debug = 'SWIFT_OPTIMIZATION_LEVEL = "-Onone"; DEBUG_INFORMATION_FORMAT = dwarf; ENABLE_TESTABILITY = YES;' if config == "Debug" else 'SWIFT_OPTIMIZATION_LEVEL = "-O";'
        active = ("DEBUG " if config == "Debug" else "") + conditions
        obj("target" + config, f'isa = XCBuildConfiguration; name = {config}; buildSettings = {{ {target_settings} {debug} SWIFT_ACTIVE_COMPILATION_CONDITIONS = {quoted(active)}; }};')
    obj("projectConfigs", f'isa = XCConfigurationList; buildConfigurations = ({ids["projectDebug"]}, {ids["projectRelease"]},); defaultConfigurationIsVisible = 0; defaultConfigurationName = Release;')
    obj("targetConfigs", f'isa = XCConfigurationList; buildConfigurations = ({ids["targetDebug"]}, {ids["targetRelease"]},); defaultConfigurationIsVisible = 0; defaultConfigurationName = Release;')
    obj("target", f'isa = PBXNativeTarget; buildConfigurationList = {ids["targetConfigs"]}; buildPhases = ({ids["sources"]}, {ids["frameworks"]}, {ids["resources"]}, {ids["embedFrameworks"]},); buildRules = (); dependencies = (); name = {name}; productName = {name}; productReference = {ids["app"]}; productType = "com.apple.product-type.application"; packageProductDependencies = {seq(package_ids)};')
    obj("project", f'isa = PBXProject; attributes = {{ BuildIndependentTargetsInParallel = YES; LastUpgradeCheck = 2610; }}; buildConfigurationList = {ids["projectConfigs"]}; compatibilityVersion = "Xcode 14.0"; developmentRegion = zh_CN; knownRegions = (en, zh_CN, Base,); mainGroup = {ids["mainGroup"]}; productRefGroup = {ids["productsGroup"]}; projectDirPath = ""; projectRoot = ""; targets = ({ids["target"]},); packageReferences = ({ids["package"]},);')
    text = "// !$*UTF8*$!\n{\n\tarchiveVersion = 1;\n\tclasses = {};\n\tobjectVersion = 56;\n\tobjects = {\n"
    text += "\n".join(objects) + f"\n\t}};\n\trootObject = {ids['project']};\n}}\n"
    write_lf(project / "project.pbxproj", text)
    scheme_dir = project / "xcshareddata/xcschemes"
    scheme_dir.mkdir(parents=True, exist_ok=True)
    reference = f'<BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="{ids["target"]}" BuildableName="{name}.app" BlueprintName="{name}" ReferencedContainer="container:{name}.xcodeproj"/>'
    scheme = f'''<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion="2610" version="1.3">
  <BuildAction parallelizeBuildables="YES" buildImplicitDependencies="YES"><BuildActionEntries><BuildActionEntry buildForTesting="YES" buildForRunning="YES" buildForProfiling="YES" buildForArchiving="YES" buildForAnalyzing="YES">{reference}</BuildActionEntry></BuildActionEntries></BuildAction>
  <LaunchAction buildConfiguration="Debug" selectedDebuggerIdentifier="Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier="Xcode.IDEFoundation.Launcher.LLDB" launchStyle="0" useCustomWorkingDirectory="NO" ignoresPersistentStateOnLaunch="NO" debugDocumentVersioning="YES" allowLocationSimulation="YES"><BuildableProductRunnable runnableDebuggingMode="0">{reference}</BuildableProductRunnable></LaunchAction>
  <ProfileAction buildConfiguration="Release" shouldUseLaunchSchemeArgsEnv="YES" savedToolIdentifier="" useCustomWorkingDirectory="NO" debugDocumentVersioning="YES"><BuildableProductRunnable runnableDebuggingMode="0">{reference}</BuildableProductRunnable></ProfileAction>
  <AnalyzeAction buildConfiguration="Debug"/>
  <ArchiveAction buildConfiguration="Release" revealArchiveInOrganizer="YES"/>
</Scheme>
'''
    write_lf(scheme_dir / f"{name}.xcscheme", scheme)
    print(f"Generated {name}: meowfilm, enabled capabilities music, film, novel")


if __name__ == "__main__":
    argparse.ArgumentParser(description=__doc__).parse_args()
    generate()
