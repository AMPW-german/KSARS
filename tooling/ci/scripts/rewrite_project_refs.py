#!/usr/bin/env python3
"""Rewrites local <ProjectReference> elements in a .csproj to CI <Reference>/<HintPath> entries."""

from __future__ import annotations

import json
import os
import pathlib
import re


def main() -> None:
    csproj_raw = os.environ.get("CSPROJ_PATH", "").strip()
    replacements_raw = os.environ.get("PROJECT_REF_REPLACEMENTS", "").strip()
    if not csproj_raw or not replacements_raw:
        return

    project_path = pathlib.Path(csproj_raw)
    content = project_path.read_text(encoding="utf-8")

    replacements = json.loads(replacements_raw)
    if not isinstance(replacements, list):
        raise ValueError("PROJECT_REF_REPLACEMENTS must be a JSON array.")

    for item in replacements:
        project_include = item["project"]
        ref_name = item["reference"]
        hint_path = item["hint_path"]
        is_private = "True" if item.get("private", True) else "False"

        normalized = project_include.replace("\\", "/")
        parts = [re.escape(p) for p in normalized.split("/")]
        path_regex = r"[\\/]".join(parts)
        pattern = re.compile(
            rf'([ \t]*)<ProjectReference\s+Include="{path_regex}"\s*/>'
        )

        replacement = (
            f'\\1<Reference Include="{ref_name}">\n'
            f"\\1\t<HintPath>{hint_path}</HintPath>\n"
            f"\\1\t<Private>{is_private}</Private>\n"
            f"\\1</Reference>"
        )

        content, count = pattern.subn(replacement, content, count=1)
        if count != 1:
            raise RuntimeError(
                f"Expected to replace 1 ProjectReference matching '{project_include}' in {project_path}, replaced {count}."
            )
        print(f"Replaced ProjectReference '{project_include}' -> HintPath '{hint_path}'")

    project_path.write_text(content, encoding="utf-8")


if __name__ == "__main__":
    main()
