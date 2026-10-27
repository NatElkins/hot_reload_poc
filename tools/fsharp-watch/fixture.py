#!/usr/bin/env python3
"""Create a private SDK copy for acceptance. Never modify an installed SDK."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess

COMPILER_FILES = {"fsc.dll", "fsc.deps.json", "fsc.runtimeconfig.json",
                  "FSharp.Compiler.Service.dll", "FSharp.Core.dll", "FSharp.Build.dll",
                  "Microsoft.FSharp.Targets", "Microsoft.FSharp.Core.NetSdk.props"}


def create_fixture(sdk_root, version, compiler, destination):
    """Copy the SDK and compiler files into a new, isolated installation."""
    sdk_root, compiler, destination = sdk_root.resolve(), compiler.resolve(), destination.resolve()
    if destination.exists():
        raise ValueError(f"The fixture destination already exists: {destination}")
    if not (sdk_root / "sdk" / version / "FSharp/fsc.dll").is_file():
        raise ValueError(f"The source SDK {version} is absent from {sdk_root}.")
    if not all((compiler / name).is_file() for name in COMPILER_FILES):
        raise ValueError(f"The compiler output is incomplete: {compiler}")
    # Copy file contents, never hard links. The acceptance tests replace compiler files.
    destination.mkdir(parents=True)
    for name in ["host", "shared", "packs", "sdk-manifests"]:
        source = sdk_root / name
        if source.is_dir():
            shutil.copytree(source, destination / name)
    for name in ["dotnet", "dotnet.exe"]:
        if (sdk_root / name).is_file():
            shutil.copy2(sdk_root / name, destination / name)
    target_sdk = destination / "sdk" / version
    shutil.copytree(sdk_root / "sdk" / version, target_sdk)
    # The build task and compiler must use the same FSharp.Core assembly identity.
    # Retain stock library packs but replace the full compiler distribution together.
    shutil.copytree(compiler, target_sdk / "FSharp", dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("*.runtimeconfig.dev.json", "*.pdb", "*.xml"))
    (destination / ".fsharp-watch-private-sdk").write_text(json.dumps({
        "purpose": "Private future-SDK fixture. This is not an official SDK distribution.",
        "compilerOutput": str(compiler), "sdkVersion": version,
    }, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk-root", type=Path, required=True)
    parser.add_argument("--sdk-version", required=True)
    parser.add_argument("--compiler-output", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    create_fixture(args.sdk_root, args.sdk_version, args.compiler_output, args.output)
    host = args.output / ("dotnet.exe" if (args.output / "dotnet.exe").exists() else "dotnet")
    subprocess.run([str(host.resolve()), "--list-sdks"], check=True)


if __name__ == "__main__":
    main()
