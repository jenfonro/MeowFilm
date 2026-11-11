"""Generate the meowfilm iPhone app and component-test project."""
from pathlib import Path
import argparse
import hashlib
import plistlib

ROOT = Path(__file__).resolve().parents[1]

APPLICATION_DIRECTORIES = ("Application", "Composition", "Navigation", "Presentation")
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


def source_paths(root, directories):
    """Only owned source directories; resources/archives never join the target."""
    base = root / "apps/ios"
    result = []
    for directory in directories:
        folder = base / directory
        if folder.is_symlink() or folder.resolve() != folder.absolute():
            raise ValueError("Linked source directory: " + directory)
        if not folder.is_dir():
            raise ValueError("Missing source directory: " + directory)
        count = len(result)
        for path in sorted(folder.rglob("*")):
            if path.is_symlink() or path.resolve() != path.absolute() or not path.resolve().is_relative_to(base.resolve()):
                raise ValueError("Linked or escaped source: " + str(path))
            if path.is_file() and path.suffix == ".swift":
                result.append(path.relative_to(base).as_posix())
        if len(result) == count:
            raise ValueError("Empty source directory: " + directory)
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
    test_sources = source_paths(ROOT, ("UITests",))
    runtime_test_sources = source_paths(ROOT, ("RuntimeTests",))
    all_sources = {p.relative_to(ROOT / "apps/ios").as_posix()
                   for p in (ROOT / "apps/ios").rglob("*.swift")}
    if all_sources != set(app_sources + test_sources + runtime_test_sources):
        raise ValueError("App Swift sources must belong to an application responsibility or a test directory")
    bundle_identifier = "dev.meowfilm.aggregate"
    base_info = plistlib.loads((ROOT / "apps/ios/Info.plist").read_bytes())
    project = ROOT / f"apps/ios/{name}.xcodeproj"
    project.mkdir(parents=True, exist_ok=True)
    ids = {key: identifier(f"{product}:{key}") for key in [
        "project", "mainGroup", "productsGroup", "app",
        "target", "sources", "frameworks", "resources", "assets", "assetsBuild", "package",
        "projectConfigs", "targetConfigs", "projectDebug", "projectRelease", "targetDebug", "targetRelease",
    ]}
    test_name = "MeowfilmAcceptanceUITests"
    ids.update({key: identifier(f"{product}:{key}") for key in [
        "uiTests", "uiTestGroup", "uiTestTarget",
        "uiTestSources", "uiTestFrameworks", "uiTestConfigs", "uiTestDebug", "uiTestRelease",
        "uiTestDependency", "uiTestProxy", "uiPlatformFramework",
    ]})
    runtime_test_name = "MeowfilmRuntimeTests"
    ids.update({key: identifier(f"{product}:{key}") for key in [
        "nodeFramework", "nodeFrameworkLink", "nodeFrameworkEmbed", "embedFrameworks",
        "scriptHostFramework", "scriptHostFrameworkEmbed",
        "runtimePayload", "runtimePayloadBuild", "nodeLicense", "nodeLicenseBuild",
        "javascriptLicense", "javascriptLicenseBuild",
        "unicodeLicense", "unicodeLicenseBuild",
        "projectNotices", "projectNoticesBuild", "projectLicense", "projectLicenseBuild",
        "runtimeTests", "runtimeTestGroup", "runtimeTestTarget", "runtimeTestSources",
        "runtimeTestMovie", "runtimeTestMovieBuild", "runtimeTestResources",
        "runtimeTestConfigs", "runtimeTestDebug", "runtimeTestRelease", "runtimeTestDependency", "runtimeTestProxy",
    ]})
    libraries = ["MeowContracts", "MeowStorage", "MeowPlayback", "MeowAccounts", "MeowUI", "MeowRuntime",
                 "MeowMusicFeature", "MeowMusicPlatform", "MeowMusicNativeService", "MeowFilmFeature"]
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
    test_references, test_builds = source_objects(test_sources)
    runtime_test_references, runtime_test_builds = source_objects(runtime_test_sources)
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
    video_license_references, video_license_builds = [], []
    for filename in VIDEO_LICENSES:
        reference, build = identifier(f"{product}:license:{filename}"), identifier(f"{product}:license-build:{filename}")
        video_license_references.append(reference)
        video_license_builds.append(build)
        objects.append(f'\t\t{reference} = {{ isa = PBXFileReference; lastKnownFileType = text; name = {quoted(filename)}; path = {quoted("../../LICENSES/" + filename)}; sourceTree = "<group>"; }};')
        objects.append(f"\t\t{build} = {{ isa = PBXBuildFile; fileRef = {reference}; }};")
    obj("app", f'isa = PBXFileReference; explicitFileType = wrapper.application; path = {name}.app; sourceTree = BUILT_PRODUCTS_DIR;')
    obj("productsGroup", f'isa = PBXGroup; children = ({ids["app"]}, {ids["uiTests"]}, {ids["runtimeTests"]},); name = Products; sourceTree = "<group>";')
    obj("mainGroup", f'isa = PBXGroup; children = {seq(app_references + video_license_references + [ids["assets"], ids["nodeFramework"], ids["scriptHostFramework"], ids["runtimePayload"], ids["nodeLicense"], ids["javascriptLicense"], ids["unicodeLicense"], ids["projectNotices"], ids["projectLicense"], ids["uiTestGroup"], ids["runtimeTestGroup"], ids["productsGroup"]])}; sourceTree = "<group>";')
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
    obj("resources", f'isa = PBXResourcesBuildPhase; buildActionMask = 2147483647; files = {seq([ids["assetsBuild"], ids["runtimePayloadBuild"], ids["nodeLicenseBuild"], ids["javascriptLicenseBuild"], ids["unicodeLicenseBuild"], ids["projectNoticesBuild"], ids["projectLicenseBuild"]] + video_license_builds)}; runOnlyForDeploymentPostprocessing = 0;')
    obj("package", 'isa = XCLocalSwiftPackageReference; relativePath = ../../native/MeowKit;')
    common_project = "CLANG_ENABLE_MODULES = YES; IPHONEOS_DEPLOYMENT_TARGET = 17.0; SDKROOT = iphoneos; SWIFT_VERSION = 5.0;"
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
    # The component runner links the host package products for App-owned symbols.
    ui_packages = [identifier(f"{product}:dependency:{library}") for library in libraries]
    ui_build_files = []
    for index, package in enumerate(ui_packages):
        key = "uiPlatformFramework" if index == 0 else "uiTestFeatureFramework{}".format(index)
        ids[key] = identifier(f"{product}:uiTestFramework:{index}")
        obj(key, "isa = PBXBuildFile; productRef = {};".format(package))
        ui_build_files.append(ids[key])
    obj("uiTestGroup", f'isa = PBXGroup; children = {seq(test_references)}; name = UITests; sourceTree = "<group>";')
    obj("uiTests", f'isa = PBXFileReference; explicitFileType = wrapper.cfbundle; path = {test_name}.xctest; sourceTree = BUILT_PRODUCTS_DIR;')
    obj("uiTestSources", f'isa = PBXSourcesBuildPhase; buildActionMask = 2147483647; files = {seq(test_builds)}; runOnlyForDeploymentPostprocessing = 0;')
    obj("uiTestFrameworks", f'isa = PBXFrameworksBuildPhase; buildActionMask = 2147483647; files = {seq(ui_build_files)}; runOnlyForDeploymentPostprocessing = 0;')
    obj("uiTestProxy", f'isa = PBXContainerItemProxy; containerPortal = {ids["project"]}; proxyType = 1; remoteGlobalIDString = {ids["target"]}; remoteInfo = {name};')
    obj("uiTestDependency", f'isa = PBXTargetDependency; target = {ids["target"]}; targetProxy = {ids["uiTestProxy"]};')
    test_settings = (
        f'PRODUCT_BUNDLE_IDENTIFIER = {bundle_identifier}.acceptance-tests; PRODUCT_NAME = {test_name}; '
        'GENERATE_INFOPLIST_FILE = YES; IPHONEOS_DEPLOYMENT_TARGET = 17.0; TARGETED_DEVICE_FAMILY = 1; '
        'SUPPORTED_PLATFORMS = "iphoneos iphonesimulator"; SUPPORTS_MACCATALYST = NO; '
        'CODE_SIGN_STYLE = Automatic; '
        'SWIFT_INCLUDE_PATHS = ("$(inherited)", "$(BUILD_DIR)/$(CONFIGURATION)$(EFFECTIVE_PLATFORM_NAME)",); '
        'LD_RUNPATH_SEARCH_PATHS = "$(inherited) @executable_path/Frameworks @loader_path/Frameworks"; '
        f'TEST_TARGET_NAME = {name}; '
    )
    for config in ["Debug", "Release"]:
        optimization = "-Onone" if config == "Debug" else "-O"
        active = ("DEBUG " if config == "Debug" else "") + conditions
        obj("uiTest" + config, f'isa = XCBuildConfiguration; name = {config}; buildSettings = {{ {test_settings} SWIFT_OPTIMIZATION_LEVEL = "{optimization}"; SWIFT_ACTIVE_COMPILATION_CONDITIONS = {quoted(active)}; }};')
    obj("uiTestConfigs", f'isa = XCConfigurationList; buildConfigurations = ({ids["uiTestDebug"]}, {ids["uiTestRelease"]},); defaultConfigurationIsVisible = 0; defaultConfigurationName = Release;')
    obj("uiTestTarget", f'isa = PBXNativeTarget; buildConfigurationList = {ids["uiTestConfigs"]}; buildPhases = ({ids["uiTestSources"]}, {ids["uiTestFrameworks"]},); buildRules = (); dependencies = ({ids["uiTestDependency"]},); name = {test_name}; productName = {test_name}; productReference = {ids["uiTests"]}; productType = "com.apple.product-type.bundle.ui-testing"; packageProductDependencies = {seq(ui_packages)};')
    # Hosted in the real App process: no package products are linked again, so
    # the App's single copy of each module (and of Node Mobile) is exercised.
    obj("runtimeTestMovie", 'isa = PBXFileReference; lastKnownFileType = video.mp4; name = ordinary.mp4; path = "../../native/VideoKernel/Tests/VideoKernelTests/Fixtures/ordinary.mp4"; sourceTree = "<group>";')
    obj("runtimeTestMovieBuild", f"isa = PBXBuildFile; fileRef = {ids['runtimeTestMovie']};")
    obj("runtimeTestResources", f'isa = PBXResourcesBuildPhase; buildActionMask = 2147483647; files = ({ids["runtimeTestMovieBuild"]},); runOnlyForDeploymentPostprocessing = 0;')
    obj("runtimeTestGroup", f'isa = PBXGroup; children = {seq(runtime_test_references + [ids["runtimeTestMovie"]])}; name = RuntimeTests; sourceTree = "<group>";')
    obj("runtimeTests", f'isa = PBXFileReference; explicitFileType = wrapper.cfbundle; path = {runtime_test_name}.xctest; sourceTree = BUILT_PRODUCTS_DIR;')
    obj("runtimeTestSources", f'isa = PBXSourcesBuildPhase; buildActionMask = 2147483647; files = {seq(runtime_test_builds)}; runOnlyForDeploymentPostprocessing = 0;')
    obj("runtimeTestProxy", f'isa = PBXContainerItemProxy; containerPortal = {ids["project"]}; proxyType = 1; remoteGlobalIDString = {ids["target"]}; remoteInfo = {name};')
    obj("runtimeTestDependency", f'isa = PBXTargetDependency; target = {ids["target"]}; targetProxy = {ids["runtimeTestProxy"]};')
    runtime_test_settings = (
        f'PRODUCT_BUNDLE_IDENTIFIER = {bundle_identifier}.runtime-tests; PRODUCT_NAME = {runtime_test_name}; '
        'GENERATE_INFOPLIST_FILE = YES; IPHONEOS_DEPLOYMENT_TARGET = 17.0; TARGETED_DEVICE_FAMILY = 1; '
        'INFOPLIST_KEY_NSPrincipalClass = MeowfilmRuntimeTestBundle; '
        'SUPPORTED_PLATFORMS = "iphoneos iphonesimulator"; SUPPORTS_MACCATALYST = NO; CODE_SIGN_STYLE = Automatic; '
        # The test shares the App's products directory, so the host and its
        # processed NodeMobile.framework are both found under BUILT_PRODUCTS_DIR.
        f'TEST_HOST = "$(BUILT_PRODUCTS_DIR)/{name}.app/{name}"; BUNDLE_LOADER = "$(TEST_HOST)"; '
        'SWIFT_INCLUDE_PATHS = ("$(inherited)", "$(BUILD_DIR)/$(CONFIGURATION)$(EFFECTIVE_PLATFORM_NAME)",); '
        'FRAMEWORK_SEARCH_PATHS = ("$(inherited)", "$(BUILT_PRODUCTS_DIR)",); '
        'LD_RUNPATH_SEARCH_PATHS = "$(inherited) @executable_path/Frameworks @loader_path/Frameworks"; '
    )
    for config in ["Debug", "Release"]:
        optimization = "-Onone" if config == "Debug" else "-O"
        active = ("DEBUG " if config == "Debug" else "") + conditions
        obj("runtimeTest" + config, f'isa = XCBuildConfiguration; name = {config}; buildSettings = {{ {runtime_test_settings} SWIFT_OPTIMIZATION_LEVEL = "{optimization}"; SWIFT_ACTIVE_COMPILATION_CONDITIONS = {quoted(active)}; }};')
    obj("runtimeTestConfigs", f'isa = XCConfigurationList; buildConfigurations = ({ids["runtimeTestDebug"]}, {ids["runtimeTestRelease"]},); defaultConfigurationIsVisible = 0; defaultConfigurationName = Release;')
    obj("runtimeTestTarget", f'isa = PBXNativeTarget; buildConfigurationList = {ids["runtimeTestConfigs"]}; buildPhases = ({ids["runtimeTestSources"]}, {ids["runtimeTestResources"]},); buildRules = (); dependencies = ({ids["runtimeTestDependency"]},); name = {runtime_test_name}; productName = {runtime_test_name}; productReference = {ids["runtimeTests"]}; productType = "com.apple.product-type.bundle.unit-test";')
    test_targets = f' {ids["uiTestTarget"]}, {ids["runtimeTestTarget"]},'
    test_attributes = (
        f' TargetAttributes = {{ '
        f'{ids["uiTestTarget"]} = {{ TestTargetID = {ids["target"]}; }}; '
        f'{ids["runtimeTestTarget"]} = {{ TestTargetID = {ids["target"]}; }}; '
        '};'
    )
    obj("project", f'isa = PBXProject; attributes = {{ BuildIndependentTargetsInParallel = YES; LastUpgradeCheck = 2610;{test_attributes} }}; buildConfigurationList = {ids["projectConfigs"]}; compatibilityVersion = "Xcode 14.0"; developmentRegion = zh_CN; knownRegions = (en, zh_CN, Base,); mainGroup = {ids["mainGroup"]}; productRefGroup = {ids["productsGroup"]}; projectDirPath = ""; projectRoot = ""; targets = ({ids["target"]},{test_targets}); packageReferences = ({ids["package"]},);')
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
    test_reference = f'<BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="{ids["uiTestTarget"]}" BuildableName="{test_name}.xctest" BlueprintName="{test_name}" ReferencedContainer="container:{name}.xcodeproj"/>'
    runtime_test_reference = f'<BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="{ids["runtimeTestTarget"]}" BuildableName="{runtime_test_name}.xctest" BlueprintName="{runtime_test_name}" ReferencedContainer="container:{name}.xcodeproj"/>'
    test_entry = "".join(
        f'<BuildActionEntry buildForTesting="YES" buildForRunning="NO" buildForProfiling="NO" buildForArchiving="NO" buildForAnalyzing="YES">{value}</BuildActionEntry>'
        for value in (test_reference, runtime_test_reference))
    test_action = f'''  <TestAction buildConfiguration="Debug" selectedDebuggerIdentifier="Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier="Xcode.IDEFoundation.Launcher.LLDB" shouldUseLaunchSchemeArgsEnv="YES">
    <Testables><TestableReference skipped="NO" parallelizable="NO">{test_reference}</TestableReference><TestableReference skipped="NO" parallelizable="NO">{runtime_test_reference}</TestableReference></Testables>
    <MacroExpansion>{reference}</MacroExpansion>
  </TestAction>
'''
    acceptance_scheme = scheme.replace(
        "</BuildActionEntries>", test_entry + "</BuildActionEntries>"
    )
    acceptance_scheme = acceptance_scheme.replace("  <LaunchAction", test_action + "  <LaunchAction")
    write_lf(scheme_dir / "MeowfilmAcceptance.xcscheme", acceptance_scheme)
    print(f"Generated {name}: meowfilm, enabled capabilities music, film")


if __name__ == "__main__":
    argparse.ArgumentParser(description=__doc__).parse_args()
    generate()
