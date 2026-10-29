"""Checks a built .unitypackage before it reaches anyone.

Guards the things that would embarrass us in Asset Store review or quietly break
someone's project:

  * every entry lands under Assets/OpenMMORPG, so an import cannot scatter files
  * build tooling and CI config never ship to customers
  * the licence and third-party notices are present, as review requires
  * the project settings and the menu item that imports them travel with the kit
  * the render pipeline installer travels with the kit, with the manifest and both pipelines' archives
  * the Package Manager manifest is embedded, including URP, which the kit needs
  * the kit manifest lists every entry in the archive, with hashes that match what is in it

usage: check_package.py <archive.unitypackage>
"""
import json
import os
import sys
import tarfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_unitypackage as bu  # noqa: E402  (the same hashing the build uses)

PREFIX = "Assets/OpenMMORPG/"
MIN_ENTRIES = 2000

REQUIRED = [
    ("THIRD-PARTY-NOTICES.md", "the third-party notices Asset Store review requires"),
    ("Assets/OpenMMORPG/LICENSE", "the licence"),
    ("Tools/Install/OpenMMORPG_Settings.unitypackage", "the project settings archive"),
    ("Tools/Editor/ProjectSettingsInstaller.cs", "the menu item that imports the settings"),
    ("Tools/Editor/PipelineInstaller/PipelineInstaller.cs", "the render pipeline installer"),
    ("Tools/Editor/PipelineInstaller/PipelineInstallerWindow.cs", "the render pipeline installer's window"),
    ("Tools/Install/Pipelines/pipelines.json", "the pipeline manifest"),
    ("Tools/Install/Pipelines/OpenMMORPG_URP.unitypackage", "the URP version of the demo content"),
    ("Tools/Install/Pipelines/OpenMMORPG_HDRP.unitypackage", "the HDRP version of the demo content"),
    (bu.KIT_MANIFEST, "the kit manifest the updater reads"),
]


def check_kit_manifest(kit, archived):
    """kit: the parsed manifest; archived: guid -> {path, hash, meta, size}, hashed from the archive."""
    failures = []
    if kit.get("format") != bu.KIT_MANIFEST_FORMAT:
        failures.append(f"kit manifest format {kit.get('format')!r}, expected {bu.KIT_MANIFEST_FORMAT}")
    files = kit.get("files", {})
    expected = {g: e for g, e in archived.items() if g != bu.KIT_MANIFEST_GUID}
    missing = [e["path"] for g, e in expected.items() if g not in files]
    if missing:
        failures.append(f"{len(missing)} archive entry(s) missing from the kit manifest: {missing[:5]}")
    extra = [e.get("path") for g, e in files.items() if g not in expected]
    if extra:
        failures.append(f"{len(extra)} kit manifest entry(s) not in the archive: {extra[:5]}")
    wrong = [e["path"] for g, e in expected.items() if g in files and files[g] != e]
    if wrong:
        failures.append(f"{len(wrong)} kit manifest entry(s) disagree with the archive: {wrong[:5]}")
    print(f"kit manifest: version {kit.get('version')}, {len(files)} entries")
    return failures


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    archive = sys.argv[1]
    paths, manifest, kit = [], None, None
    archived = {}  # guid -> what the kit manifest should say about it
    with tarfile.open(archive) as tar:
        for member in tar:
            if not member.isfile():
                continue
            guid, _, part = member.name.partition("/")
            data = tar.extractfile(member).read()
            if member.name == "packagemanagermanifest/asset":
                manifest = json.loads(data.decode("utf-8"))
                continue
            entry = archived.setdefault(guid, {})
            if part == "pathname":
                path = data.decode("utf-8").split("\n")[0]
                paths.append(path)
                entry["path"] = path[len(PREFIX):] if path.startswith(PREFIX) else path
            elif part == "asset":
                entry["hash"] = bu.content_hash(data)
                entry["size"] = len(bu.normalised(data))
                if guid == bu.KIT_MANIFEST_GUID:
                    kit = json.loads(data.decode("utf-8"))
            elif part == "asset.meta":
                entry["meta"] = bu.content_hash(data)
    # A folder has no contents to hash; the builder marks it instead.
    for entry in archived.values():
        if "hash" not in entry:
            entry["folder"] = True

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
        # The kit's own files are the URP version, so it needs URP to compile; that is also what lets an HDRP
        # project import it and reach the installer, which then offers to remove URP again.
        if "com.unity.render-pipelines.universal" not in deps:
            failures.append("the kit needs URP to compile before the pipeline installer has run, "
                            "but it is not in the embedded dependencies")

    if kit is None:
        failures.append(f"no readable kit manifest at {bu.KIT_MANIFEST}, so an installed kit cannot be updated")
    else:
        failures += check_kit_manifest(kit, archived)

    if failures:
        print("\nFAILED")
        for f in failures:
            print("  " + f)
        return 1

    print("all package checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
