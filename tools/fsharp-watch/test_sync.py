"""Verify source updates against branch rewrites and unrelated upstream changes."""
import copy
import unittest

import sync


class SourceUpdateTests(unittest.TestCase):
    def setUp(self):
        self.source = {
            "repository": "NatElkins/sdk",
            "branch": "feature/fsharp-watch-global-tool",
            "commit": "a" * 40,
            "upstreamRepository": "dotnet/sdk",
            "upstreamCommit": "b" * 40,
            "compilerFixture": {
                "repository": "NatElkins/fsharp",
                "branch": "hot-reload-v2",
                "commit": "c" * 40,
            },
        }

    def query(self, path):
        if "/commits/feature%2Ffsharp-watch-global-tool" in path:
            return {"sha": "d" * 40}
        if "/commits/hot-reload-v2" in path:
            return {"sha": "e" * 40}
        if path.endswith("...dotnet:main"):
            return {"merge_base_commit": {"sha": "f" * 40}}
        return {"status": "ahead"}

    def test_updates_both_sources_without_mutating_original(self):
        original = copy.deepcopy(self.source)
        self.source["futureSetting"] = {"enabled": True}
        updated = sync.update_source(self.source, self.query)
        self.assertEqual("d" * 40, updated["commit"])
        self.assertEqual("e" * 40, updated["compilerFixture"]["commit"])
        self.assertEqual("f" * 40, updated["upstreamCommit"])
        self.assertEqual({"enabled": True}, updated["futureSetting"])
        self.assertEqual(original["commit"], self.source["commit"])
        self.assertEqual(original["compilerFixture"], self.source["compilerFixture"])

    def test_rejects_rewritten_engine_or_compiler_history(self):
        for repository in ("NatElkins/sdk", "NatElkins/fsharp"):
            for status in ("behind", "diverged"):
                with self.subTest(repository=repository, status=status):
                    def query(path):
                        if path.startswith(f"repos/{repository}/compare/") and not path.endswith("...dotnet:main"):
                            return {"status": status}
                        return self.query(path)
                    with self.assertRaisesRegex(ValueError, "Source history changed"):
                        sync.update_source(self.source, query)

    def test_records_incorporated_upstream_commit(self):
        def query(path):
            if path.endswith("...dotnet:main"):
                return {"head_commit": {"sha": "1" * 40}, "merge_base_commit": {"sha": "b" * 40}}
            return self.query(path)
        updated = sync.update_source(self.source, query)
        self.assertEqual("b" * 40, updated["upstreamCommit"])

    def test_rejects_upstream_history_regression(self):
        def query(path):
            if path.startswith("repos/dotnet/sdk/compare/"):
                return {"status": "behind"}
            return self.query(path)
        with self.assertRaisesRegex(ValueError, "Source history changed"):
            sync.update_source(self.source, query)

    def test_no_update_for_identical_sources(self):
        def query(path):
            if "/commits/feature%2F" in path:
                return {"sha": "a" * 40}
            if "/commits/hot-reload-v2" in path:
                return {"sha": "c" * 40}
            return {"merge_base_commit": {"sha": "b" * 40}}
        self.assertEqual(self.source, sync.update_source(self.source, query))

    def test_preserves_older_manifest_without_fixture(self):
        del self.source["compilerFixture"]
        updated = sync.update_source(self.source, self.query)
        self.assertNotIn("compilerFixture", updated)

    def test_rejects_invalid_identity_before_remote_lookup(self):
        for key, value in (("repository", "other/sdk"), ("branch", "main"),
                           ("upstreamRepository", "other/sdk"), ("commit", "abc")):
            with self.subTest(key=key):
                invalid = dict(self.source, **{key: value})
                with self.assertRaises(ValueError):
                    sync.update_source(invalid, lambda path: self.fail("Unexpected API call"))

    def test_rejects_invalid_remote_commit(self):
        with self.assertRaisesRegex(ValueError, "40 lowercase"):
            sync.update_source(self.source, lambda path: {"sha": "bad"})


if __name__ == "__main__":
    unittest.main()
