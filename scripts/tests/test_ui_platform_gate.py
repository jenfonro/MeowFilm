"""Pure contracts for the UIKit platform runner."""
from pathlib import Path
import importlib.util
import unittest
from tempfile import TemporaryDirectory
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location(
    "platform_gate", ROOT / "scripts/test-ui-platform.py"
)
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class UIPlatformGateTests(unittest.TestCase):
    def test_source_declares_unique_platform_tests(self):
        names = gate.expected_tests()
        self.assertTrue(names)
        self.assertEqual(len(set(names)), len(names))

    def test_selected_component_is_forwarded_and_only_its_results_are_required(self):
        name = gate.expected_tests()[0]
        output = "\n".join(
            f"Test Case '-[Runner.MeowUIPlatformTests {name}]' {state}"
            for state in ("started", "passed")
        )
        with TemporaryDirectory() as directory, patch.object(gate.sys, "platform", "darwin"), \
                patch.object(gate, "run", return_value=SimpleNamespace(returncode=0, stdout=output)) as run, \
                patch.object(gate.sys, "argv", [
                    "gate", "--simulator", "12345678-1234-1234-1234-123456789abc",
                    "--result", str(Path(directory) / "selected.xcresult"), "--test", name,
                ]):
            self.assertEqual(gate.main(), 0)
            self.assertEqual(run.call_count, 2)
            self.assertEqual(run.call_args_list[0].args[0], [
                gate.sys.executable, "-I", "-B", "scripts/prepare-ios-video-kernel.py",
                "--target", "ios-simulator-" + gate.platform.machine(),
            ])
            selected = [arg for arg in run.call_args.args[0] if arg.startswith("-only-testing:")]
            self.assertEqual(selected, [f"-only-testing:MeowfilmAcceptanceUITests/MeowUIPlatformTests/{name}"])

    def test_failed_video_kernel_preparation_preserves_exit_and_never_starts_xcode(self):
        with TemporaryDirectory() as directory, patch.object(gate.sys, "platform", "darwin"), \
                patch.object(gate, "run", return_value=SimpleNamespace(
                    returncode=27, stdout="video kernel fixture failed\n")) as run, \
                patch.object(gate.sys, "argv", [
                    "gate", "--simulator", "12345678-1234-1234-1234-123456789abc",
                    "--result", str(Path(directory) / "selected.xcresult"),
                ]):
            self.assertEqual(gate.main(), 27)
            run.assert_called_once()
            self.assertIn("scripts/prepare-ios-video-kernel.py", run.call_args.args[0])
            self.assertEqual((Path(directory) / "selected.log").read_text(),
                             "video kernel fixture failed\n")
            self.assertFalse((Path(directory) / "selected.xcresult").exists())

    def test_unknown_selection_does_not_launch_xcode(self):
        with patch.object(gate.sys, "platform", "darwin"), patch.object(gate, "run") as run, \
                patch.object(gate.sys, "argv", [
                    "gate", "--simulator", "12345678-1234-1234-1234-123456789abc",
                    "--result", "unused.xcresult", "--test", "testMissing",
                ]):
            with self.assertRaisesRegex(ValueError, "Unknown component"):
                gate.main()
            run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
