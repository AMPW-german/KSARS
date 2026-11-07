#!/usr/bin/env python3
"""Downloads StarMap.API and configured mod dependency DLLs from GitHub releases."""

from __future__ import annotations

import json
import os
import pathlib
import urllib.request
import zipfile


def api_get(url: str, token: str) -> dict:
    headers = {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2026-03-10",
        "User-Agent": "KSAModTools-CI/1.0",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req) as response:
        return json.loads(response.read().decode("utf-8"))


def download(url: str, target: pathlib.Path, token: str) -> None:
    headers = {
        "Accept": "application/octet-stream",
        "X-GitHub-Api-Version": "2026-03-10",
        "User-Agent": "KSAModTools-CI/1.0",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(url, headers=headers)
    target.parent.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(req) as response:
        target.write_bytes(response.read())


def latest_release(owner: str, repo: str, token: str) -> dict:
    return api_get(f"https://api.github.com/repos/{owner}/{repo}/releases/latest", token)


def find_asset(assets: list, predicate, error: str) -> dict:
    for asset in assets:
        if predicate(asset):
            return asset
    raise RuntimeError(error)


def extract_release_zip(
    owner: str, repo: str, deps_dir: pathlib.Path, extracted_dir: pathlib.Path, token: str
) -> pathlib.Path:
    extract_path = extracted_dir / f"{owner}_{repo}"
    if extract_path.exists():
        return extract_path

    release = latest_release(owner, repo, token)
    assets = release.get("assets", [])
    zip_asset = find_asset(
        assets,
        lambda a: a["name"].lower().endswith(".zip"),
        f"No zip asset found in latest release for {owner}/{repo}.",
    )

    zip_path = deps_dir / f"{repo}-latest.zip"
    extract_path.mkdir(parents=True, exist_ok=True)
    download(zip_asset["browser_download_url"], zip_path, token)

    with zipfile.ZipFile(zip_path, "r") as archive:
        archive.extractall(extract_path)

    return extract_path


def download_zip_dll(
    owner: str,
    repo: str,
    dll_name: str,
    mod_name: str,
    deps_dir: pathlib.Path,
    extracted_dir: pathlib.Path,
    token: str,
    zip_subpath: str = "",
    pdb: bool = False,
) -> None:
    extract_path = extract_release_zip(owner, repo, deps_dir, extracted_dir, token)
    target_dir = deps_dir / mod_name if mod_name else deps_dir
    target_dir.mkdir(parents=True, exist_ok=True)

    if zip_subpath:
        source_dll = extract_path / zip_subpath / dll_name
        if not source_dll.exists():
            raise RuntimeError(
                f"{dll_name} not found at {zip_subpath} in latest {owner}/{repo} zip release asset."
            )
    else:
        dll_matches = list(extract_path.rglob(dll_name))
        if not dll_matches:
            raise RuntimeError(f"{dll_name} not found in latest {owner}/{repo} zip release asset.")
        source_dll = dll_matches[0]

    (target_dir / dll_name).write_bytes(source_dll.read_bytes())
    print(f"Downloaded {owner}/{repo} -> {target_dir / dll_name}")

    if pdb:
        pdb_name = dll_name[:-4] + ".pdb" if dll_name.lower().endswith(".dll") else f"{dll_name}.pdb"
        if zip_subpath:
            source_pdb = extract_path / zip_subpath / pdb_name
            if not source_pdb.exists():
                raise RuntimeError(
                    f"{pdb_name} not found at {zip_subpath} in latest {owner}/{repo} zip release asset."
                )
        else:
            pdb_matches = list(extract_path.rglob(pdb_name))
            if not pdb_matches:
                raise RuntimeError(
                    f"{pdb_name} not found in latest {owner}/{repo} zip release asset."
                )
            source_pdb = pdb_matches[0]

        (target_dir / pdb_name).write_bytes(source_pdb.read_bytes())
        print(f"Downloaded {owner}/{repo} -> {target_dir / pdb_name}")


def download_direct_dll(
    owner: str,
    repo: str,
    dll_name: str,
    mod_name: str,
    deps_dir: pathlib.Path,
    token: str,
) -> None:
    release = latest_release(owner, repo, token)
    assets = release.get("assets", [])
    dll_asset = find_asset(
        assets,
        lambda a: a["name"].lower() == dll_name.lower(),
        f"No direct DLL asset named {dll_name} found in latest release for {owner}/{repo}.",
    )
    target_dir = deps_dir / mod_name if mod_name else deps_dir
    target = target_dir / dll_name
    download(dll_asset["browser_download_url"], target, token)
    print(f"Downloaded {owner}/{repo} -> {target}")


def main() -> None:
    token = os.environ.get("GH_TOKEN", "")
    default_owner = os.environ.get("GH_OWNER", "AMPW-german")
    configuration = os.environ.get("CONFIGURATION", "Release")
    include_starmap = os.environ.get("INCLUDE_STARMAP", "true").lower() != "false"
    mod_deps_raw = os.environ.get("MOD_DEPS", "").strip()

    deps_dir = pathlib.Path(os.environ.get("DEPS_DIR", "deps"))
    extracted_dir = deps_dir / "extracted"
    extracted_dir.mkdir(parents=True, exist_ok=True)

    if include_starmap:
        download_zip_dll(
            owner="StarMapLoader",
            repo="StarMap",
            dll_name="StarMap.API.dll",
            mod_name="StarMap",
            deps_dir=deps_dir,
            extracted_dir=extracted_dir,
            token=token,
        )

    if not mod_deps_raw:
        return

    mod_deps = json.loads(mod_deps_raw)
    if not isinstance(mod_deps, list):
        raise ValueError("MOD_DEPS must be a JSON array of dependency objects.")

    for entry in mod_deps:
        repo = entry["repo"]
        owner = entry.get("owner") or default_owner
        dll_name = entry.get("dll") or f"{repo}.dll"
        mod_name = entry.get("mod_name", repo)
        direct = bool(entry.get("direct", False))
        pdb = bool(entry.get("pdb", False))
        zip_subpath = (entry.get("zip_subpath") or "").replace(
            "{config}", configuration.lower()
        ).replace("{Configuration}", configuration)

        if direct:
            download_direct_dll(
                owner=owner,
                repo=repo,
                dll_name=dll_name,
                mod_name=mod_name,
                deps_dir=deps_dir,
                token=token,
            )
        else:
            download_zip_dll(
                owner=owner,
                repo=repo,
                dll_name=dll_name,
                mod_name=mod_name,
                deps_dir=deps_dir,
                extracted_dir=extracted_dir,
                token=token,
                zip_subpath=zip_subpath,
                pdb=pdb,
            )


if __name__ == "__main__":
    main()
