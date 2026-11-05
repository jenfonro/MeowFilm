"""Deterministic command doubles: no Apple build, app or real network here."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "test-native.sh"
WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/native-code-gate.yml"


class NativeGateWorkflowTests(unittest.TestCase):
    def test_ci_runs_the_complete_gate_without_publishing_or_device_operations(self):
        text = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("bash scripts/test-native.sh all", text)
        self.assertIn("persist-credentials: false", text)
        self.assertNotIn("test-ui-platform.py", text)
        self.assertNotIn("publish-beta-ios.py", text)
        self.assertNotIn("upload-artifact", text)
        self.assertNotIn("secrets.", text)


class NativeGateOrderTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "scripts").mkdir()
        shutil.copyfile(SCRIPT, self.root / "scripts/test-native.sh")
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.events = self.root / "events"
        self.tmp = self.root / "tmp"
        self.env = dict(os.environ, GATE_EVENTS=str(self.events), GATE_TMP=str(self.tmp),
                        GATE_FAIL="", GATE_SKIP="", GATE_COUNT="12")
        self.command("uname", 'if [ "$1" = -s ]; then echo Darwin; else echo x86_64; fi')
        self.command("git", 'exit 99')
        self.command("python3", """
case "$*" in
 *repository-paths.py*) echo "$GATE_TMP" ;;
 *check-source-scope.py*) echo scope >> "$GATE_EVENTS" ;;
 *prepare-ios-runtime.py*) echo runtime >> "$GATE_EVENTS"; [ "$GATE_FAIL" != runtime ] ;;
 *check-simulator-signing.py*) echo signing >> "$GATE_EVENTS"; [ "$GATE_FAIL" != signing ] ;;
 *unittest*) echo tools >> "$GATE_EVENTS"; [ "$GATE_FAIL" != tools ] ;;
 *) exit 9 ;;
esac
""")
        self.command("swift", """
if [ "$1" = build ]; then
 echo package >> "$GATE_EVENTS"; [ "$GATE_FAIL" != package ]
elif [ "$1" = test ]; then
 echo "swift $*" >> "$GATE_EVENTS"
 echo "Executed $GATE_COUNT tests, with 0 failures"
 [ -z "$GATE_SKIP" ] || echo "1 test skipped"
 [ "$GATE_FAIL" != tests ]
else exit 9; fi
""")
        self.command("xcodebuild", """
case " $* " in
 *" -showBuildSettings "*)
  echo "    TARGET_BUILD_DIR = /Synthetic/Derived"
  echo "    FULL_PRODUCT_NAME = Meowfilm.app"; exit 0 ;;
esac
case "$*" in *MeowfilmAcceptance*) kind=components ;; *) kind=app ;; esac
echo "$kind" >> "$GATE_EVENTS"; [ "$GATE_FAIL" != "$kind" ]
""")

    def command(self, name, body):
        path = self.bin / name
        path.write_text("#!/bin/bash\n" + body + "\n", encoding="utf-8")
        path.chmod(0o755)

    def run_gate(self, *arguments, **env):
        if os.name == "nt":
            command = ["bash", "-c",
                       'PATH="$(cygpath -u "$1"):$PATH"; export PATH; shift; exec bash scripts/test-native.sh "$@"',
                       "native-gate-fixture", str(self.bin), *arguments]
        else:
            command = ["bash", "scripts/test-native.sh", *arguments]
        fixture_env = dict(self.env, **env)
        if os.name != "nt":
            fixture_env["PATH"] = str(self.bin) + os.pathsep + fixture_env["PATH"]
        return subprocess.run(command, cwd=self.root, env=fixture_env, capture_output=True, text=True)

    def recorded(self):
        return self.events.read_text().splitlines() if self.events.exists() else []

    def test_scope_is_required_before_invoking_any_tool(self):
        for arguments in [(), ("test",), ("test", "--filter", ""), ("unknown",)]:
            with self.subTest(arguments=arguments):
                self.assertNotEqual(self.run_gate(*arguments).returncode, 0)
                self.assertEqual(self.recorded(), [])

    def test_filtered_tests_build_only_the_package_and_do_not_require_git_or_prior_receipts(self):
        result = self.run_gate("test", "--filter", "MeowUITests.SomeCase")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.recorded(), ["swift test --package-path native/MeowKit -j 2 --filter MeowUITests.SomeCase"])
        self.assertEqual(self.run_gate("test", "--filter", "MeowUITests.SomeCase").returncode, 0)

    def test_explicit_all_compiles_then_runs_both_test_layers(self):
        result = self.run_gate("all")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.recorded()[:7], ["scope", "package", "runtime", "app", "components", "signing", "tools"])
        self.assertIn("--skip-build", self.recorded()[-1])

    def test_failed_compile_collects_independent_compile_errors_without_running_tests(self):
        result = self.run_gate("all", GATE_FAIL="package")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.recorded(), ["scope", "package", "runtime", "app", "components", "signing"])

    def test_failed_runtime_preparation_never_builds_with_stale_app_inputs(self):
        result = self.run_gate("compile", GATE_FAIL="runtime")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.recorded(), ["scope", "package", "runtime"])

    def test_tool_failure_does_not_hide_swift_test_results(self):
        result = self.run_gate("all", GATE_FAIL="tools")
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue(self.recorded()[-1].startswith("swift test"))

    def test_empty_skipped_and_failed_selection_return_failure(self):
        for env in ({"GATE_COUNT": "0"}, {"GATE_SKIP": "1"}, {"GATE_FAIL": "tests"}):
            with self.subTest(env=env):
                self.assertNotEqual(self.run_gate("test", "--filter", "SomeCase", **env).returncode, 0)


if __name__ == "__main__":
    unittest.main()
