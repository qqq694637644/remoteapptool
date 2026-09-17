#!/usr/bin/env python3
"""GitHub Actions helper for RemoteApp Tool build and release workflows."""

from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import re
import subprocess
import sys
import zipfile

SEMVER_RE = re.compile(
    r"^\d+\.\d+\.\d+"
    r"(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?"
    r"(?:\+[0-9A-Za-z][0-9A-Za-z.-]*)?$"
)

REQUIRED_BUILD_FILES = (
    "RemoteApp Tool.exe",
    "RemoteAppSessionHost.exe",
    "RemoteAppLib.dll",
    "RDPFileLib.dll",
)


def append_github_file(env_name: str, values: dict[str, str]) -> None:
    path_text = os.environ.get(env_name)
    if not path_text:
        raise RuntimeError(f"{env_name} is not set")

    with Path(path_text).open("a", encoding="utf-8", newline="\n") as stream:
        for key, value in values.items():
            if "\n" in value or "\r" in value:
                raise ValueError(f"Multiline value is not supported for {key}")
            stream.write(f"{key}={value}\n")


def metadata(args: argparse.Namespace) -> None:
    if args.event_name == "workflow_dispatch":
        version = args.version.strip()
        if version.lower().startswith("v"):
            version = version[1:]
        if not SEMVER_RE.fullmatch(version):
            raise ValueError(
                f"Version '{version}' is invalid. "
                "Use a semantic version such as 6.2.0 or 6.2.0-beta.1."
            )
        tag = f"v{version}"
        package_name = f"remoteapptool-{tag}.zip"
    elif args.event_name == "pull_request":
        if not args.pr_number:
            raise ValueError("--pr-number is required for pull_request builds")
        version = f"pr-{args.pr_number}"
        tag = ""
        package_name = f"remoteapptool-{version}.zip"
    else:
        raise ValueError(f"Unsupported workflow event: {args.event_name}")

    checksum_name = f"{package_name}.sha256"
    values = {
        "version": version,
        "tag": tag,
        "package_name": package_name,
        "checksum_name": checksum_name,
    }
    append_github_file("GITHUB_OUTPUT", values)
    for key, value in values.items():
        print(f"{key}: {value}")


def framework_env(args: argparse.Namespace) -> None:
    root = Path(args.root).resolve()
    framework_path = (
        root
        / ".packages"
        / "Microsoft.NETFramework.ReferenceAssemblies.net40"
        / "build"
        / ".NETFramework"
        / "v4.0"
    )
    if not framework_path.is_dir():
        raise FileNotFoundError(
            "The .NET Framework 4.0 reference assemblies were not found at "
            f"{framework_path}"
        )

    append_github_file(
        "GITHUB_ENV",
        {"FRAMEWORK_PATH_OVERRIDE": str(framework_path)},
    )
    print(f"FRAMEWORK_PATH_OVERRIDE: {framework_path}")


def zip_directory(source_directory: Path, destination: Path) -> None:
    if destination.exists():
        destination.unlink()

    with zipfile.ZipFile(destination, mode="w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(source_directory.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(source_directory))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def package(args: argparse.Namespace) -> None:
    root = Path(args.root).resolve()
    output_directory = root / "remoteapp-tool" / "bin" / "Release"
    if not output_directory.is_dir():
        raise FileNotFoundError(f"Build output directory is missing: {output_directory}")

    missing = [name for name in REQUIRED_BUILD_FILES if not (output_directory / name).is_file()]
    if missing:
        raise FileNotFoundError(
            "Required build outputs are missing: " + ", ".join(missing)
        )

    dist_directory = root / "dist"
    dist_directory.mkdir(parents=True, exist_ok=True)

    package_path = dist_directory / args.package_name
    checksum_path = dist_directory / args.checksum_name

    zip_directory(output_directory, package_path)
    digest = sha256_file(package_path)
    checksum_path.write_text(
        f"{digest}  {package_path.name}\n",
        encoding="ascii",
    )

    print(f"Package: {package_path}")
    print(f"SHA256:  {digest}")


def tag_exists(repository: str, tag: str) -> bool:
    result = subprocess.run(
        ["gh", "api", f"repos/{repository}/git/ref/tags/{tag}"],
        text=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode == 0:
        return True
    stderr = (result.stderr or "").lower()
    if "404" in stderr or "not found" in stderr:
        return False
    raise RuntimeError(
        "Failed to query release tag. "
        f"gh exited with {result.returncode}: {(result.stderr or '').strip()}"
    )


def release(args: argparse.Namespace) -> None:
    repository = args.repository.strip()
    version = args.version.strip()
    tag = args.tag.strip()
    target = args.target.strip()
    dist_directory = Path(args.dist).resolve()

    if not repository or not version or not tag or not target:
        raise ValueError("repository, version, tag and target are required")

    if tag_exists(repository, tag):
        raise RuntimeError(f"Release tag {tag} already exists")

    package_path = dist_directory / args.package_name
    checksum_path = dist_directory / args.checksum_name
    for path in (package_path, checksum_path):
        if not path.is_file():
            raise FileNotFoundError(f"Release asset is missing: {path}")

    command = [
        "gh",
        "release",
        "create",
        tag,
        str(package_path),
        str(checksum_path),
        "--repo",
        repository,
        "--target",
        target,
        "--title",
        f"RemoteApp Tool {version}",
        "--generate-notes",
    ]
    if "-" in version:
        command.append("--prerelease")

    print("+ " + subprocess.list2cmdline(command))
    subprocess.run(command, check=True)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    metadata_parser = subparsers.add_parser("metadata")
    metadata_parser.add_argument("--event-name", required=True)
    metadata_parser.add_argument("--version", default="")
    metadata_parser.add_argument("--pr-number", default="")
    metadata_parser.set_defaults(func=metadata)

    framework_parser = subparsers.add_parser("framework-env")
    framework_parser.add_argument("--root", default=".")
    framework_parser.set_defaults(func=framework_env)

    package_parser = subparsers.add_parser("package")
    package_parser.add_argument("--root", default=".")
    package_parser.add_argument("--package-name", required=True)
    package_parser.add_argument("--checksum-name", required=True)
    package_parser.set_defaults(func=package)

    release_parser = subparsers.add_parser("release")
    release_parser.add_argument("--repository", required=True)
    release_parser.add_argument("--version", required=True)
    release_parser.add_argument("--tag", required=True)
    release_parser.add_argument("--target", required=True)
    release_parser.add_argument("--dist", default="dist")
    release_parser.add_argument("--package-name", required=True)
    release_parser.add_argument("--checksum-name", required=True)
    release_parser.set_defaults(func=release)

    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    try:
        args.func(args)
        return 0
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
