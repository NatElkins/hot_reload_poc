"""Verify that destructive acceptance edits cannot reach an installed SDK."""
from pathlib import Path
import tempfile
import unittest

from fixture import COMPILER_FILES, create_fixture


class FixtureTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        self.sdk, self.compiler, self.output = root / "sdk", root / "compiler", root / "fixture"
        self.fsharp = self.sdk / "sdk/11.0.100/FSharp"
        self.fsharp.mkdir(parents=True)
        self.compiler.mkdir()
        (self.sdk / "dotnet").write_text("host")
        (self.fsharp / "fsc.dll").write_text("stock compiler")
        (self.fsharp / "Microsoft.FSharp.Core.NetSdk.props").write_text("stock targets")
        for name in COMPILER_FILES:
            (self.compiler / name).write_text("private compiler")

    def test_fixture_edits_do_not_modify_sources(self):
        create_fixture(self.sdk, "11.0.100", self.compiler, self.output)
        target = self.output / "sdk/11.0.100/FSharp"
        (target / "fsc.dll").write_text("replacement")
        self.assertEqual((self.fsharp / "fsc.dll").read_text(), "stock compiler")
        self.assertEqual((self.compiler / "fsc.dll").read_text(), "private compiler")
        self.assertEqual((target / "Microsoft.FSharp.Core.NetSdk.props").read_text(), "stock targets")

    def test_incomplete_compiler_leaves_no_destination(self):
        (self.compiler / "fsc.runtimeconfig.json").unlink()
        with self.assertRaisesRegex(ValueError, "incomplete"):
            create_fixture(self.sdk, "11.0.100", self.compiler, self.output)
        self.assertFalse(self.output.exists())

    def test_existing_destination_is_preserved(self):
        self.output.mkdir()
        marker = self.output / "user-file"
        marker.write_text("preserve")
        with self.assertRaisesRegex(ValueError, "already exists"):
            create_fixture(self.sdk, "11.0.100", self.compiler, self.output)
        self.assertEqual(marker.read_text(), "preserve")


if __name__ == "__main__":
    unittest.main()
