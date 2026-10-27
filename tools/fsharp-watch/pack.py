#!/usr/bin/env python3
"""Package the watch engine. The selected SDK supplies the F# compiler."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile


def git_revision(root):
    subprocess.run(["git", "-C", str(root), "diff", "--quiet", "HEAD", "--"], check=True)
    untracked = subprocess.check_output(
        ["git", "-C", str(root), "ls-files", "--others", "--exclude-standard", "-z"], text=True
    ).split("\0")
    # Worktrees can reuse the bootstrapped SDK through an untracked .dotnet link.
    unknown = [name for name in untracked if name and name != ".dotnet"]
    if unknown:
        raise RuntimeError("Commit or exclude untracked SDK files before package creation: " + ", ".join(unknown))
    return subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()


def copy_payload(source, destination):
    if destination.exists():
        shutil.rmtree(destination)
    shutil.copytree(source, destination, ignore=shutil.ignore_patterns("*.pdb", "*.xml", "*.orig"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk", type=Path, required=True)
    parser.add_argument("--version", help="Override the preview version for a CI release.")
    args = parser.parse_args()
    args.sdk = args.sdk.resolve()
    root = Path(__file__).resolve().parent
    source = json.loads((root / "sdk-source.json").read_text())
    revision = git_revision(args.sdk)
    if revision != source["commit"]:
        parser.error(f"SDK checkout {revision} differs from the pinned source {source['commit']}.")
    subprocess.run(["git", "-C", str(args.sdk), "merge-base", "--is-ancestor",
                    source["upstreamCommit"], revision], check=True)
    if args.version and not re.fullmatch(r"0\.2\.0-preview\.[0-9]+\.[0-9]+", args.version):
        parser.error("The version must use 0.2.0-preview.<date>.<sequence>.")
    revisions = {"sdkCommit": revision, "sdkRepository": source["repository"],
                 "upstreamCommit": source["upstreamCommit"],
                 "toolCommit": subprocess.check_output(
                     ["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()}
    sdk_arguments = ["build", "src/Dotnet.Watch/dotnet-watch/dotnet-watch.csproj", "-c", "Release",
                     "-p:SdkTargetFramework=net10.0", "-v", "minimal"]
    subprocess.run([str(args.sdk / ".dotnet/dotnet"), *sdk_arguments], cwd=args.sdk, check=True)
    watch = args.sdk / "artifacts/bin/dotnet-watch/Release/net10.0"
    if not (watch / "dotnet-watch.dll").is_file():
        parser.error(f"Required build output is absent: {watch / 'dotnet-watch.dll'}")
    payload = root / "payload"
    # A package from the earlier preview must not retain its compiler directory.
    if payload.exists():
        shutil.rmtree(payload)
    copy_payload(watch, payload / "watch")
    licenses = payload / "licenses"
    licenses.mkdir(exist_ok=True)
    for name in ["LICENSE.TXT", "LICENSE", "License.txt", "ThirdPartyNotices.txt", "THIRD-PARTY-NOTICES.TXT"]:
        source = args.sdk / name
        if source.is_file():
            shutil.copy2(source, licenses / f"sdk-{name}")
    hashes = {
        str(path.relative_to(payload)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(payload.rglob("*")) if path.is_file() and path.name != "provenance.json"
    }
    compiler_files = {"fsc.dll", "fsc.exe", "fsi.dll", "fsi.exe", "fsharp.compiler.service.dll", "fsharp.build.dll"}
    if any(Path(name).name.lower() in compiler_files | {"fsharp.core.dll"} for name in hashes):
        raise RuntimeError("The watch payload must not contain an F# compiler or build task.")
    (payload / "provenance.json").write_text(json.dumps({
        **revisions,
        "sdkBuildArguments": sdk_arguments,
        "compilerOwnership": "selected-sdk",
        "targetFramework": "net10.0",
        "sha256": hashes,
    }, indent=2) + "\n")
    project = root / "FSharpWatch.fsproj"
    # Remove obsolete copied output before the tool pack target scans the output directory.
    subprocess.run(["dotnet", "clean", str(project), "-c", "Release", "-v", "quiet"], check=True)
    version_arguments = [f"-p:Version={args.version}"] if args.version else []
    subprocess.run(["dotnet", "pack", str(project), "-c", "Release", "-o", str(root / "artifacts"),
                    *version_arguments], check=True)
    version = args.version or ET.parse(project).findtext("PropertyGroup/Version")
    package = root / "artifacts" / f"fsharp-watch.{version}.nupkg"
    prefix = "tools/net10.0/any/payload/"
    with zipfile.ZipFile(package) as archive:
        if any(Path(name).name.lower() in compiler_files for name in archive.namelist()):
            raise RuntimeError("The package contains an F# compiler or build task.")
        packaged = {name[len(prefix):] for name in archive.namelist()
                    if name.startswith(prefix) and not name.endswith("/") and name != prefix + "provenance.json"}
        if packaged != set(hashes):
            raise RuntimeError("The package payload differs from the provenance file list.")
        for name, expected in hashes.items():
            if hashlib.sha256(archive.read(prefix + name)).hexdigest() != expected:
                raise RuntimeError(f"The package contains a stale payload file: {name}")
    print(f"Verified {len(hashes)} payload hashes in {package}")


if __name__ == "__main__":
    main()
