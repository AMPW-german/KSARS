"""Updates the KSA DLL snapshot repository with core DLLs, ksa-versions.csv, and KsaNuGet.props."""

from __future__ import annotations

import argparse
import csv
import ctypes
from ctypes import wintypes
import glob
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import urllib.request
import xml.etree.ElementTree as ET

DEFAULT_KSA_DIR = r"C:\Program Files\Kitten Space Agency"
STARMAP_PACKAGES_PROPS_URL = (
    "https://raw.githubusercontent.com/StarMapLoader/StarMap/main/Directory.Packages.props"
)
CORE_DLLS = ("KSA.dll", "Planet.Core.dll", "Planet.Render.Core.dll")


def load_env_file(env_path: pathlib.Path) -> dict[str, str]:
    values: dict[str, str] = {}
    if not env_path.is_file():
        return values
    for raw_line in env_path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, val = line.split("=", 1)
        values[key.strip()] = val.strip().strip('"').strip("'")
    return values


def get_version_info_buffer(file_path: str) -> ctypes.Array[ctypes.c_char]:
    version_dll = ctypes.windll.version
    size = version_dll.GetFileVersionInfoSizeW(file_path, None)
    if size == 0:
        raise ctypes.WinError()
    buffer = ctypes.create_string_buffer(size)
    if not version_dll.GetFileVersionInfoW(file_path, 0, size, buffer):
        raise ctypes.WinError()
    return buffer


def get_file_version(file_path: str) -> str:
    version_dll = ctypes.windll.version
    buffer = get_version_info_buffer(file_path)

    value = ctypes.c_void_p()
    value_size = wintypes.UINT()
    if not version_dll.VerQueryValueW(
        buffer,
        r"\\",
        ctypes.byref(value),
        ctypes.byref(value_size),
    ):
        raise ctypes.WinError()

    class VS_FIXEDFILEINFO(ctypes.Structure):
        _fields_ = [
            ("dwSignature", wintypes.DWORD),
            ("dwStrucVersion", wintypes.DWORD),
            ("dwFileVersionMS", wintypes.DWORD),
            ("dwFileVersionLS", wintypes.DWORD),
            ("dwProductVersionMS", wintypes.DWORD),
            ("dwProductVersionLS", wintypes.DWORD),
            ("dwFileFlagsMask", wintypes.DWORD),
            ("dwFileFlags", wintypes.DWORD),
            ("dwFileOS", wintypes.DWORD),
            ("dwFileType", wintypes.DWORD),
            ("dwFileSubtype", wintypes.DWORD),
            ("dwFileDateMS", wintypes.DWORD),
            ("dwFileDateLS", wintypes.DWORD),
        ]

    info = ctypes.cast(value, ctypes.POINTER(VS_FIXEDFILEINFO)).contents
    major = info.dwFileVersionMS >> 16
    minor = info.dwFileVersionMS & 0xFFFF
    build = info.dwFileVersionLS >> 16
    revision = info.dwFileVersionLS & 0xFFFF
    return f"{major}.{minor}.{build}.{revision}"


def get_product_semver(file_path: str) -> str:
    version_dll = ctypes.windll.version
    buffer = get_version_info_buffer(file_path)

    candidate_subblocks: list[str] = []
    trans_value = ctypes.c_void_p()
    trans_size = wintypes.UINT()
    if (
        version_dll.VerQueryValueW(
            buffer,
            r"\VarFileInfo\Translation",
            ctypes.byref(trans_value),
            ctypes.byref(trans_size),
        )
        and trans_size.value >= 4
    ):
        lang_cp = ctypes.cast(trans_value, ctypes.POINTER(wintypes.WORD))
        lang = lang_cp[0]
        codepage = lang_cp[1]
        candidate_subblocks.append(f"\\StringFileInfo\\{lang:04x}{codepage:04x}\\ProductVersion")
        candidate_subblocks.append(f"\\StringFileInfo\\{lang:04X}{codepage:04X}\\ProductVersion")

    candidate_subblocks.extend(
        [
            r"\StringFileInfo\000004b0\ProductVersion",
            r"\StringFileInfo\040904b0\ProductVersion",
            r"\StringFileInfo\040904B0\ProductVersion",
        ]
    )

    for subblock in candidate_subblocks:
        str_value = ctypes.c_wchar_p()
        str_size = wintypes.UINT()
        if (
            version_dll.VerQueryValueW(
                buffer,
                subblock,
                ctypes.byref(str_value),
                ctypes.byref(str_size),
            )
            and str_value.value
        ):
            raw = str_value.value.strip()
            if raw:
                return raw.split("+", 1)[0].strip()

    return get_file_version(file_path)


def parse_semver_key(version: str) -> tuple[tuple[int, ...], int, str]:
    core, _, prerelease = version.partition("-")
    core_parts = []
    for part in core.split("."):
        try:
            core_parts.append(int(part))
        except ValueError:
            core_parts.append(0)
    while len(core_parts) > 3 and core_parts[-1] == 0:
        core_parts.pop()
    is_release = 1 if not prerelease else 0
    return (tuple(core_parts), is_release, prerelease)


def resolve_nuget_version(package_id: str, dll_version: str) -> str:
    url = f"https://api.nuget.org/v3-flatcontainer/{package_id.lower()}/index.json"
    req = urllib.request.Request(url, headers={"User-Agent": "KSAModTools/1.0"})
    with urllib.request.urlopen(req, timeout=30) as response:
        data = json.loads(response.read().decode("utf-8"))

    versions: list[str] = data.get("versions", [])
    if not versions:
        raise RuntimeError(f"No versions found on NuGet.org for package '{package_id}'.")

    lower_target = dll_version.lower()
    for v in versions:
        if v.lower() == lower_target:
            return v

    target_key = parse_semver_key(dll_version)
    target_is_prerelease = "-" in dll_version

    candidates = [
        v
        for v in versions
        if ("-" in v) == target_is_prerelease
    ] or versions

    greater_or_equal = [
        v for v in candidates if parse_semver_key(v) >= target_key
    ]
    if greater_or_equal:
        greater_or_equal.sort(key=parse_semver_key)
        resolved = greater_or_equal[0]
        print(
            f"  [NuGet] {package_id}: DLL version {dll_version} not published; resolved to >= {resolved}"
        )
        return resolved

    candidates.sort(key=parse_semver_key)
    resolved = candidates[-1]
    print(
        f"  [NuGet] {package_id}: DLL version {dll_version} not published; resolved to latest {resolved}"
    )
    return resolved


def fetch_starmap_harmony_version() -> str:
    req = urllib.request.Request(
        STARMAP_PACKAGES_PROPS_URL, headers={"User-Agent": "KSAModTools/1.0"}
    )
    with urllib.request.urlopen(req, timeout=30) as response:
        xml_text = response.read().decode("utf-8")

    root = ET.fromstring(xml_text)
    for elem in root.iter():
        if elem.tag.endswith("PackageVersion") and elem.attrib.get("Include") == "Lib.Harmony":
            version = elem.attrib.get("Version", "").strip()
            if version:
                return version

    raise RuntimeError(
        f"Could not find Lib.Harmony PackageVersion in {STARMAP_PACKAGES_PROPS_URL}"
    )


def read_existing_ksa_version(dll_repo_dir: pathlib.Path) -> str | None:
    csv_path = dll_repo_dir / "ksa-versions.csv"
    if csv_path.is_file():
        with csv_path.open("r", encoding="utf-8", newline="") as f:
            reader = csv.DictReader(f)
            for row in reader:
                if row.get("name") == "KSA":
                    return row.get("version", "").strip() or None

    legacy_txt = dll_repo_dir / "KSA_VERSION.txt"
    if legacy_txt.is_file():
        return legacy_txt.read_text(encoding="utf-8").strip() or None

    return None


def is_newer_ksa_version(installed: str, existing: str) -> bool:
    try:
        inst_parts = [int(p) for p in installed.split(".")]
        exist_parts = [int(p) for p in existing.split(".")]
        return inst_parts > exist_parts
    except ValueError:
        return installed != existing


def write_ksa_versions_csv(
    csv_path: pathlib.Path, rows: list[dict[str, str]]
) -> None:
    with csv_path.open("w", encoding="utf-8", newline="\n") as f:
        writer = csv.DictWriter(
            f, fieldnames=["name", "version", "is_nuget", "package_id"], lineterminator="\n"
        )
        writer.writeheader()
        writer.writerows(rows)


def write_ksa_nuget_props(
    props_path: pathlib.Path,
    harmony_version: str,
    nuget_rows: list[dict[str, str]],
) -> None:
    lines = [
        "<Project>",
        "  <PropertyGroup>",
        f"    <HarmonyVersion Condition=\"'$(HarmonyVersion)' == ''\">{harmony_version}</HarmonyVersion>",
        "  </PropertyGroup>",
        "  <ItemGroup Condition=\"'$(UseKsaNuGetPackages)' == 'true'\">",
    ]
    for row in nuget_rows:
        pkg_id = row["package_id"]
        if pkg_id == "Lib.Harmony":
            continue
        ver = row["version"]
        lines.append(
            f'    <PackageReference Include="{pkg_id}" Version="[{ver}]" ExcludeAssets="native;contentFiles;build;buildMultitargeting;buildTransitive" />'
        )
    lines.extend(
        [
            "  </ItemGroup>",
            "</Project>",
            "",
        ]
    )
    props_path.write_text("\n".join(lines), encoding="utf-8")


def main() -> None:
    repo_root = pathlib.Path(__file__).resolve().parent.parent
    env_values = load_env_file(repo_root / ".ksa-tools.env")

    parser = argparse.ArgumentParser(
        description="Update the KSA DLL snapshot repository with core DLLs and NuGet manifest."
    )
    parser.add_argument(
        "--dll-repo-dir",
        default=os.environ.get("KSA_DLL_REPO_DIR") or env_values.get("KSA_DLL_REPO_DIR"),
        help="Path to local KSA DLL snapshot repository (or set KSA_DLL_REPO_DIR / .ksa-tools.env).",
    )
    parser.add_argument(
        "--ksa-dir",
        default=os.environ.get("KSA_DIR") or env_values.get("KSA_DIR") or DEFAULT_KSA_DIR,
        help="Path to local Kitten Space Agency installation directory.",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Update even if installed KSA.dll version is not newer than the recorded version.",
    )
    parser.add_argument(
        "--no-git",
        action="store_true",
        help="Do not stage, commit, or push changes in the DLL repository.",
    )
    parser.add_argument(
        "--no-wait",
        action="store_true",
        help="Do not wait for user input before exiting.",
    )
    args = parser.parse_args()

    if not args.dll_repo_dir:
        parser.error(
            "Missing KSA DLL repository path. Provide --dll-repo-dir, set KSA_DLL_REPO_DIR, "
            "or configure KSA_DLL_REPO_DIR in .ksa-tools.env."
        )

    dll_repo_dir = pathlib.Path(args.dll_repo_dir).resolve()
    ksa_dir = pathlib.Path(args.ksa_dir).resolve()

    if not dll_repo_dir.is_dir():
        raise FileNotFoundError(f"DLL repository directory not found: {dll_repo_dir}")

    ksa_dll_path = ksa_dir / "KSA.dll"
    if not ksa_dll_path.is_file():
        raise FileNotFoundError(f"KSA.dll not found at: {ksa_dll_path}")

    current_ksa_version = get_file_version(str(ksa_dll_path))
    existing_ksa_version = read_existing_ksa_version(dll_repo_dir)

    if (
        not args.force
        and existing_ksa_version
        and not is_newer_ksa_version(current_ksa_version, existing_ksa_version)
    ):
        print(
            f"Installed version {current_ksa_version} is not newer than repo version "
            f"{existing_ksa_version}, aborting (pass --force to override)..."
        )
        return

    print(
        f"Updating {dll_repo_dir} from v{existing_ksa_version or 'none'} to v{current_ksa_version}..."
    )

    rows: list[dict[str, str]] = []
    for core_dll in CORE_DLLS:
        dll_path = ksa_dir / core_dll
        if not dll_path.is_file():
            raise FileNotFoundError(f"Required core assembly not found: {dll_path}")
        stem = core_dll[:-4]
        rows.append(
            {
                "name": stem,
                "version": get_file_version(str(dll_path)),
                "is_nuget": "false",
                "package_id": "",
            }
        )

    harmony_version = fetch_starmap_harmony_version()
    print(f"  [StarMap] Lib.Harmony version: {harmony_version}")
    rows.append(
        {
            "name": "Lib.Harmony",
            "version": harmony_version,
            "is_nuget": "true",
            "package_id": "Lib.Harmony",
        }
    )

    brutal_dlls = sorted(ksa_dir.glob("Brutal.*.dll"), key=lambda p: p.name.lower())
    for dll_path in brutal_dlls:
        stem = dll_path.stem
        suffix = stem[len("Brutal.") :]
        package_id = f"Ahwoo.BRUTAL.{suffix}"
        dll_ver = get_product_semver(str(dll_path))
        resolved_ver = resolve_nuget_version(package_id, dll_ver)
        rows.append(
            {
                "name": stem,
                "version": resolved_ver,
                "is_nuget": "true",
                "package_id": package_id,
            }
        )

    for bepu_stem in ("BepuPhysics", "BepuUtilities"):
        dll_path = ksa_dir / f"{bepu_stem}.dll"
        if not dll_path.is_file():
            raise FileNotFoundError(f"Required assembly not found: {dll_path}")
        dll_ver = get_product_semver(str(dll_path))
        resolved_ver = resolve_nuget_version(bepu_stem, dll_ver)
        rows.append(
            {
                "name": bepu_stem,
                "version": resolved_ver,
                "is_nuget": "true",
                "package_id": bepu_stem,
            }
        )

    tomlet_path = ksa_dir / "Tomlet.dll"
    if not tomlet_path.is_file():
        raise FileNotFoundError(f"Required assembly not found: {tomlet_path}")
    tomlet_ver = resolve_nuget_version("Samboy063.Tomlet", get_product_semver(str(tomlet_path)))
    rows.append(
        {
            "name": "Tomlet",
            "version": tomlet_ver,
            "is_nuget": "true",
            "package_id": "Samboy063.Tomlet",
        }
    )

    for existing_dll in dll_repo_dir.glob("*.dll"):
        print(f"Removing {existing_dll.name}...")
        existing_dll.unlink()

    for legacy_file in ("KSA_VERSION.txt", "updateRepo.py"):
        legacy_path = dll_repo_dir / legacy_file
        if legacy_path.is_file():
            print(f"Removing legacy {legacy_file}...")
            legacy_path.unlink()

    for core_dll in CORE_DLLS:
        print(f"Copying {core_dll}...")
        shutil.copyfile(ksa_dir / core_dll, dll_repo_dir / core_dll)

    csv_path = dll_repo_dir / "ksa-versions.csv"
    props_path = dll_repo_dir / "KsaNuGet.props"
    nuget_rows = [r for r in rows if r["is_nuget"] == "true"]

    print(f"Writing {csv_path.name} and {props_path.name}...")
    write_ksa_versions_csv(csv_path, rows)
    write_ksa_nuget_props(props_path, harmony_version, nuget_rows)

    if not args.no_git:
        commit_message = f"Update to KSA v{current_ksa_version}"
        subprocess.run(["git", "add", "-A"], cwd=dll_repo_dir, check=True)
        subprocess.run(["git", "commit", "-m", commit_message], cwd=dll_repo_dir, check=True)
        subprocess.run(["git", "push"], cwd=dll_repo_dir, check=True)


if __name__ == "__main__":
    exit_code = 0
    try:
        main()
    except Exception as exc:
        print(f"\nError: {exc}", file=sys.stderr)
        exit_code = 1
    finally:
        if "--no-wait" not in sys.argv and sys.stdin and sys.stdin.isatty():
            try:
                input("\nPress Enter to exit...")
            except EOFError:
                pass
    sys.exit(exit_code)
