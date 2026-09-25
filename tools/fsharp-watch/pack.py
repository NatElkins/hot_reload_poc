#!/usr/bin/env python3
"""Package the watch fork and a matched compiler without an SDK redistribution."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess


def git_revision(root):
    subprocess.run(["git", "-C", str(root), "diff", "--quiet", "HEAD", "--"], check=True)
    return subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()


def copy_payload(source, destination):
    if destination.exists():
        shutil.rmtree(destination)
    shutil.copytree(source, destination, ignore=shutil.ignore_patterns("*.pdb", "*.xml", "*.orig"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk", type=Path, required=True)
    parser.add_argument("--fsharp", type=Path, required=True)
    parser.add_argument("--configuration", default="Debug", choices=["Debug", "Release"])
    args = parser.parse_args()
    args.sdk = args.sdk.resolve()
    args.fsharp = args.fsharp.resolve()
    revisions = {"sdkCommit": git_revision(args.sdk), "fsharpCommit": git_revision(args.fsharp)}
    root = Path(__file__).resolve().parent
    sdk_arguments = ["build", "src/Dotnet.Watch/dotnet-watch/dotnet-watch.csproj", "-c", "Release",
                     "-p:SdkTargetFramework=net10.0", "-v", "minimal"]
    compiler_arguments = ["build", "src/fsc/fscProject/fsc.fsproj", "-c", args.configuration,
                          "-p:FSharpNetCoreProductTargetFramework=net10.0", "-p:MicrosoftBuildVersion=18.7.1",
                          "-p:MicrosoftBuildFrameworkVersion=18.7.1", "-p:MicrosoftBuildTasksCoreVersion=18.7.1",
                          "-p:MicrosoftBuildUtilitiesCoreVersion=18.7.1", "-v", "minimal", "-m:2", "-nr:false"]
    subprocess.run([str(args.sdk / ".dotnet/dotnet"), *sdk_arguments], cwd=args.sdk, check=True)
    subprocess.run([str(args.fsharp / ".dotnet/dotnet"), *compiler_arguments], cwd=args.fsharp,
                   env=dict(os.environ, BUILDING_USING_DOTNET="true"), check=True)
    watch = args.sdk / "artifacts/bin/dotnet-watch/Release/net10.0"
    compiler = args.fsharp / f"artifacts/bin/fsc/{args.configuration}/net10.0"
    for required in [watch / "dotnet-watch.dll", compiler / "fsc.dll", compiler / "FSharp.Compiler.Service.dll"]:
        if not required.is_file():
            parser.error(f"Required build output is absent: {required}")
    payload = root / "payload"
    copy_payload(watch, payload / "watch")
    copy_payload(compiler, payload / "FSharp")
    licenses = payload / "licenses"
    licenses.mkdir(exist_ok=True)
    for checkout, prefix in [(args.sdk, "sdk"), (args.fsharp, "fsharp")]:
        for name in ["LICENSE.TXT", "LICENSE", "License.txt", "ThirdPartyNotices.txt", "THIRD-PARTY-NOTICES.TXT"]:
            source = checkout / name
            if source.is_file():
                shutil.copy2(source, licenses / f"{prefix}-{name}")
    hashes = {
        str(path.relative_to(payload)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(payload.rglob("*")) if path.is_file() and path.name != "provenance.json"
    }
    (payload / "provenance.json").write_text(json.dumps({
        **revisions,
        "sdkBuildArguments": sdk_arguments,
        "compilerBuildArguments": compiler_arguments,
        "targetFramework": "net10.0",
        "sha256": hashes,
    }, indent=2) + "\n")
    subprocess.run(["dotnet", "pack", str(root / "FSharpWatch.fsproj"), "-c", "Release", "-o", str(root / "artifacts")], check=True)


if __name__ == "__main__":
    main()
