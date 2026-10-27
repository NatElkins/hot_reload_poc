#!/usr/bin/env python3
"""Advance source pins only through verified descendant commits."""
import argparse
import copy
import json
from pathlib import Path
import re
import subprocess
from urllib.parse import quote


def github(path):
    """Read GitHub metadata through the workflow's repository token."""
    return json.loads(subprocess.check_output(["gh", "api", path], text=True))


def validate_source(source):
    """Reject unexpected source identities before any remote lookup or file write."""
    expected = {
        "repository": "NatElkins/sdk",
        "branch": "feature/fsharp-watch-global-tool",
        "upstreamRepository": "dotnet/sdk",
    }
    for key, value in expected.items():
        if source.get(key) != value:
            raise ValueError(f"Unexpected {key}: expected {value}.")
    for key in ("commit", "upstreamCommit"):
        validate_commit(source.get(key))
    fixture = source.get("compilerFixture")
    if fixture is not None:
        if fixture.get("repository") != "NatElkins/fsharp" or fixture.get("branch") != "hot-reload-v2":
            raise ValueError("The compiler fixture must track NatElkins/fsharp:hot-reload-v2.")
        validate_commit(fixture.get("commit"))


def validate_commit(value):
    """Require a full commit identity in both manifests and API responses."""
    if not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{40}", value) is None:
        raise ValueError("A source revision must contain exactly 40 lowercase hexadecimal characters.")
    return value


def descendant(repository, before, after, query):
    """Reject branch rewrites so scheduled updates cannot discard reviewed history."""
    validate_commit(after)
    if before == after:
        return
    comparison = query(f"repos/{repository}/compare/{before}...{after}")
    if comparison["status"] != "ahead":
        raise ValueError(f"Source history changed in {repository}. Resolve the pin manually before another update.")


def update_source(source, query=github):
    """Resolve engine and optional compiler pins without mutation of the input."""
    validate_source(source)
    updated = copy.deepcopy(source)
    for dependency in (updated, updated.get("compilerFixture")):
        if dependency is None:
            continue
        endpoint = f"repos/{dependency['repository']}/commits/{quote(dependency['branch'], safe='')}"
        revision = query(endpoint)["sha"]
        descendant(dependency["repository"], dependency["commit"], revision, query)
        dependency["commit"] = revision

    # The merge base records incorporated upstream source, not an unmerged main tip.
    comparison = query(f"repos/{updated['repository']}/compare/{updated['commit']}...dotnet:main")
    upstream = validate_commit(comparison["merge_base_commit"]["sha"])
    descendant(updated["upstreamRepository"], updated["upstreamCommit"], upstream, query)
    updated["upstreamCommit"] = upstream
    return updated


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=Path(__file__).with_name("sdk-source.json"))
    parser.add_argument("--check", action="store_true", help="Report candidate revisions without a file write.")
    parser.add_argument("--github-output", type=Path)
    args = parser.parse_args()
    original = json.loads(args.manifest.read_text())
    candidate = update_source(original)
    changed = candidate != original
    if changed and not args.check:
        args.manifest.write_text(json.dumps(candidate, indent=2) + "\n")
    if args.github_output:
        with args.github_output.open("a") as output:
            output.write(f"changed={str(changed).lower()}\n")
    print(json.dumps({"changed": changed, "source": candidate}, indent=2))


if __name__ == "__main__":
    main()
