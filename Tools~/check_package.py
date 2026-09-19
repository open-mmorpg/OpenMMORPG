"""Checks a built .unitypackage before it reaches anyone.

Guards the things that would embarrass us in Asset Store review or quietly break
someone's project:

  * every entry lands under Assets/OpenMMORPG, so an import cannot scatter files
  * build tooling and CI config never ship to customers
  * the licence and third-party notices are present, as review requires
  * the project settings and the menu item that imports them travel with the kit,
    since Asset Store users have no installer package
  * the Package Manager manifest is embedded, and it pulls in no render pipeline,
    so the kit works in a URP, HDRP or Built-in project alike

usage: check_package.py <archive.unitypackage>
"""
import json
import os
import sys
import tarfile

PREFIX = "Assets/OpenMMORPG/"
MIN_ENTRIES = 2000

# Importing the kit must never switch or add a render pipeline.
RENDER_PIPELINE_PREFIXES = ("com.unity.render-pipelines.", "com.unity.shadergraph")

REQUIRED = [
    ("THIRD-PARTY-NOTICES.md", "the third-party notices Asset Store review requires"),
    ("Assets/OpenMMORPG/LICENSE", "the licence"),
    ("Tools/Install/OpenMMORPG_Settings.unitypackage", "the project settings archive"),
    ("Tools/Editor/ProjectSettingsInstaller.cs", "the menu item that imports the settings"),
]


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    archive = sys.argv[1]
    paths, manifest = [], None
    with tarfile.open(archive) as tar:
        for member in tar.getmembers():
            if not member.isfile():
                continue
            if member.name == "packagemanagermanifest/asset":
                manifest = json.loads(tar.extractfile(member).read().decode("utf-8"))
            elif member.name.endswith("/pathname"):
                paths.append(tar.extractfile(member).read().decode("utf-8").split("\n")[0])

    size_mb = os.path.getsize(archive) / 1e6
    print(f"{archive}: {len(paths)} entries, {size_mb:.1f} MB")

    failures = []

    stray = [p for p in paths if not p.startswith(PREFIX)]
    if stray:
        failures.append(f"{len(stray)} entry(s) outside {PREFIX}: {stray[:5]}")

    if len(paths) < MIN_ENTRIES:
        failures.append(f"only {len(paths)} entries, fewer than the {MIN_ENTRIES} expected; the kit looks incomplete")

    leaked = [p for p in paths if "/Tools~/" in p or "/.github/" in p]
    if leaked:
        failures.append(f"build tooling or CI config leaked into the package: {leaked[:5]}")

    for suffix, description in REQUIRED:
        if not any(p.endswith(suffix) for p in paths):
            failures.append(f"missing {description} ({suffix})")

    if manifest is None:
        failures.append("no Package Manager manifest embedded, so dependencies would not install")
    else:
        deps = manifest.get("dependencies", {})
        print(f"embedded {len(deps)} package dependencies")
        pipelines = [k for k in deps if k.startswith(RENDER_PIPELINE_PREFIXES)]
        if pipelines:
            failures.append(f"render pipeline package(s) embedded, but the kit must stay pipeline agnostic: {pipelines}")

    if failures:
        print("\nFAILED")
        for f in failures:
            print("  " + f)
        return 1

    print("all package checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
