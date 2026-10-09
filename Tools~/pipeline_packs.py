"""Builds and checks the render pipeline packs.

The kit in this repository is the URP version. Its HDRP version is a handful of files that
differ from it - materials, shaders, scenes, prefabs and the scripts that call pipeline APIs -
made in a converted copy of the project. Instead of shipping two kits, the kit ships both
versions of just those files as two small archives, plus a manifest, in Tools/Install/Pipelines.
The editor window in Tools/Editor/PipelineInstaller detects the project's pipeline and imports
the matching archive, removing what the other pipeline left behind.

    build   compares the converted HDRP project with this repository and writes
            OpenMMORPG_URP.unitypackage, OpenMMORPG_HDRP.unitypackage and pipelines.json
    sync    copies the repository's changes to everything that is not pipeline-specific into the HDRP
            project, so that only real HDRP differences are left to find
    check   needs no Unity and no HDRP project: confirms the archives hold what the manifest says,
            and that the repository still matches the URP versions the manifest was built from

usage:
  pipeline_packs.py build --repo <Assets/OpenMMORPG> --hdrp <converted Assets/OpenMMORPG>
                          [--out <dir>] [--obsolete <file>] [--kit-version <v>]
  pipeline_packs.py sync  --repo <Assets/OpenMMORPG> --hdrp <converted Assets/OpenMMORPG> [--dry-run]
  pipeline_packs.py check --repo <Assets/OpenMMORPG> [--strict]

What goes where, for a path in the kit (relative to Assets/OpenMMORPG):

  differs between the two trees   both archives carry it: the HDRP archive the converted file,
                                  the URP archive the repository's own
  only in the HDRP tree           in the HDRP archive; installing URP removes it
  only in this repository         in the URP archive; installing HDRP removes it
  listed in the obsolete file     identical in both trees but meaningless under HDRP (URP pipeline
                                  assets and the like): in the URP archive, removed by an HDRP install

Everything the installer needs to know is in pipelines.json: for each pipeline the files its
content is made of with a hash of each, and the files that must not exist beside it. Hashes are
SHA-1 of the file with CRLF turned into LF when it is text (no NUL in its first 8000 bytes),
because the working tree is CRLF on Windows and the exported package is not.
"""
import argparse
import hashlib
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_unitypackage as bu  # noqa: E402  (the archive writer the kit's own build uses)

PREFIX = "Assets/OpenMMORPG"
OUT_DIR = "Tools/Install/Pipelines"
MANIFEST = "pipelines.json"
PACKS = {"urp": "OpenMMORPG_URP.unitypackage", "hdrp": "OpenMMORPG_HDRP.unitypackage"}

# Neutral files that ship loose in the kit and are never part of a pipeline's content.
NEVER_PACKED = ("Tools/Editor/PipelineInstaller/", "Tools/Install/Pipelines/")

# A fixed timestamp, so the same inputs give the same archive.
MTIME = 1704067200

PIPELINES = {
    "urp": {
        "name": "Universal Render Pipeline (URP)",
        "assetType": "UniversalRenderPipelineAsset",
        "unityPackage": "com.unity.render-pipelines.universal",
        "testedWith": "17.3.0",
    },
    "hdrp": {
        "name": "High Definition Render Pipeline (HDRP)",
        "assetType": "HDRenderPipelineAsset",
        "unityPackage": "com.unity.render-pipelines.high-definition",
        "testedWith": "17.3.0",
        # What the URP content needed and HDRP does not; the installer offers to remove them afterwards.
        "packagesNoLongerNeeded": [
            "com.unity.render-pipelines.universal",
            "com.unity.render-pipelines.universal-config",
        ],
    },
}


def skip(rel):
    """Unity ignores dot-folders and anything ending in '~'."""
    return any(p.startswith(".") or p.endswith("~") for p in rel.split("/"))


def never_packed(rel):
    return any(rel.startswith(p) for p in NEVER_PACKED)


def scan(root):
    """(files, folders): every asset's path relative to root, mapped to its absolute path."""
    files, folders = {}, {}
    for dirpath, dirnames, filenames in os.walk(root):
        rel_dir = os.path.relpath(dirpath, root).replace("\\", "/")
        rel_dir = "" if rel_dir == "." else rel_dir
        dirnames[:] = sorted(d for d in dirnames if not skip((rel_dir + "/" + d).lstrip("/")))
        for d in dirnames:
            rel = (rel_dir + "/" + d).lstrip("/")
            if not never_packed(rel + "/"):
                folders[rel] = os.path.join(dirpath, d)
        for fn in filenames:
            rel = (rel_dir + "/" + fn).lstrip("/")
            if skip(rel) or fn.endswith(".meta") or never_packed(rel):
                continue
            files[rel] = os.path.join(dirpath, fn)
    return files, folders


def normalised(data):
    if b"\x00" not in data[:8000]:
        return data.replace(b"\r\n", b"\n")
    return data


def read(path):
    with open(path, "rb") as f:
        return normalised(f.read())


def digest(path):
    return hashlib.sha1(read(path)).hexdigest()


def read_obsolete(path):
    out = []
    if not os.path.isfile(path):
        return out
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.split("#", 1)[0].strip()
            if line:
                out.append(line)
    return out


def entries_for(paths, files, folders, known_folders, label):
    """Archive entries (guid, pathname, asset bytes, meta bytes) for paths, plus any new folders above them."""
    entries, seen = [], {}

    def add(guid, pathname, asset, meta):
        if guid in seen:
            raise SystemExit(f"{label}: {pathname} shares GUID {guid} with {seen[guid]}")
        seen[guid] = pathname
        entries.append((guid, pathname, asset, meta))

    wanted_folders = set()
    for rel in paths:
        parent = rel.rsplit("/", 1)[0] if "/" in rel else ""
        while parent and parent not in known_folders and parent not in wanted_folders:
            wanted_folders.add(parent)
            parent = parent.rsplit("/", 1)[0] if "/" in parent else ""
    for rel in sorted(wanted_folders):
        meta = folders[rel] + ".meta"
        if not os.path.isfile(meta):
            raise SystemExit(f"{label}: new folder {rel} has no .meta")
        add(bu.guid_of(meta), f"{PREFIX}/{rel}", None, read(meta))
    for rel in sorted(paths):
        meta = files[rel] + ".meta"
        if not os.path.isfile(meta):
            raise SystemExit(f"{label}: {rel} has no .meta, so it would not ship")
        add(bu.guid_of(meta), f"{PREFIX}/{rel}", read(files[rel]), read(meta))
    return entries


def write_pack(out, entries):
    with bu._Archive(out) as tar:
        for guid, pathname, asset, meta in entries:
            bu.add_entry(tar, guid, pathname, asset, meta, MTIME)


def git_head(repo):
    """The commit the repository is at, with '+' if the tree has uncommitted changes; None without git."""
    try:
        sha = subprocess.run(["git", "-C", repo, "rev-parse", "--short=12", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
        dirty = subprocess.run(["git", "-C", repo, "status", "--porcelain"], capture_output=True, text=True, check=True).stdout.strip()
        return sha + ("+" if dirty else "")
    except (OSError, subprocess.CalledProcessError):
        return None


def build(args):
    repo, hdrp = os.path.abspath(args.repo), os.path.abspath(args.hdrp)
    out = os.path.abspath(args.out or os.path.join(repo, OUT_DIR))
    obsolete_file = args.obsolete or os.path.join(repo, "Tools~", "pipelines", "hdrp-obsolete.txt")

    r_files, r_folders = scan(repo)
    h_files, h_folders = scan(hdrp)
    print(f"repository: {len(r_files)} files   converted project: {len(h_files)} files")

    def unit_hash(files, rel):
        # an asset and its .meta travel together: a changed importer setting is a change
        return digest(files[rel]), digest(files[rel] + ".meta")

    changed = sorted(p for p in h_files if p in r_files and unit_hash(h_files, p) != unit_hash(r_files, p))
    hdrp_only = sorted(p for p in h_files if p not in r_files)
    repo_only = sorted(p for p in r_files if p not in h_files)
    if len(repo_only) > args.max_missing:
        raise SystemExit(f"{len(repo_only)} repository files are missing from the converted project: it looks "
                         f"incomplete (raise --max-missing if that is right)")

    obsolete = read_obsolete(obsolete_file)
    for p in obsolete:
        if p not in r_files:
            raise SystemExit(f"{obsolete_file}: {p} is not in the repository")
    remove_for_hdrp = sorted(set(repo_only) | set(obsolete))
    remove_for_urp = list(hdrp_only)

    print(f"differ: {len(changed)}   HDRP only: {len(hdrp_only)}   removed for HDRP: {len(remove_for_hdrp)}"
          f" ({len(repo_only)} gone from the converted project, {len(obsolete)} listed obsolete)")

    # GUIDs must agree wherever a path exists in both: a different GUID is a different asset
    for p in changed:
        if bu.guid_of(h_files[p] + ".meta") != bu.guid_of(r_files[p] + ".meta"):
            raise SystemExit(f"{p} has a different GUID in the converted project")

    hdrp_paths = changed + hdrp_only
    urp_paths = sorted(set(changed) | set(remove_for_hdrp))
    packs = {
        "hdrp": (entries_for(hdrp_paths, h_files, h_folders, r_folders, "HDRP"), hdrp_paths, h_files, remove_for_hdrp),
        "urp": (entries_for(urp_paths, r_files, r_folders, r_folders, "URP"), urp_paths, r_files, remove_for_urp),
    }

    os.makedirs(out, exist_ok=True)
    manifest = {"schema": 1, "kitVersion": args.kit_version, "builtFrom": git_head(repo) or "", "prefix": PREFIX, "pipelines": []}
    for pid in ("urp", "hdrp"):
        entries, paths, files, remove = packs[pid]
        target = os.path.join(out, PACKS[pid])
        write_pack(target, entries)
        print(f"wrote {target}: {len(entries)} entries, {os.path.getsize(target) / 1e6:.1f} MB")
        info = dict(PIPELINES[pid])
        manifest["pipelines"].append({
            "id": pid,
            "package": PACKS[pid],
            **info,
            "files": [{"path": p, "hash": digest(files[p])} for p in sorted(paths)],
            "remove": sorted(remove),
        })
    with open(os.path.join(out, MANIFEST), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"wrote {os.path.join(out, MANIFEST)}")
    return 0


def scan_all(root):
    """Like scan(), but with the installer and the packs included: sync must carry those too."""
    files = {}
    for dirpath, dirnames, filenames in os.walk(root):
        rel_dir = os.path.relpath(dirpath, root).replace("\\", "/")
        rel_dir = "" if rel_dir == "." else rel_dir
        dirnames[:] = sorted(d for d in dirnames if not skip((rel_dir + "/" + d).lstrip("/")))
        for fn in filenames:
            rel = (rel_dir + "/" + fn).lstrip("/")
            if not skip(rel):
                files[rel] = os.path.join(dirpath, fn)
    return files


def pipeline_specific(out):
    """Every path a pipeline's version owns (installs or removes), from the manifest; empty if there is none yet."""
    manifest_path = os.path.join(out, MANIFEST)
    owned = set()
    if os.path.isfile(manifest_path):
        with open(manifest_path, encoding="utf-8") as f:
            for pipe in json.load(f)["pipelines"]:
                owned.update(i["path"] for i in pipe["files"])
                owned.update(pipe["remove"])
    return owned


def sync(args):
    import shutil
    repo, hdrp = os.path.abspath(args.repo), os.path.abspath(args.hdrp)
    owned = pipeline_specific(os.path.join(repo, OUT_DIR))
    if not owned:
        print("no manifest yet, so nothing is known to be pipeline-specific: sync would overwrite converted files; run build first")
        return 1
    r_files, h_files = scan_all(repo), scan_all(hdrp)
    copy, kept = [], 0
    for rel in sorted(r_files):
        asset = rel[:-5] if rel.endswith(".meta") else rel
        if asset in owned:
            kept += 1
            continue
        if rel not in h_files or digest(r_files[rel]) != digest(h_files[rel]):
            copy.append(rel)
    print(f"{len(copy)} file(s) differ from the repository outside the pipeline-specific set ({kept} pipeline-specific files left alone)")
    for rel in copy:
        print(("  would copy " if args.dry_run else "  copy ") + rel)
        if not args.dry_run:
            target = os.path.join(hdrp, rel.replace("/", os.sep))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copyfile(r_files[rel], target)
    only_h = [p for p in h_files if p not in r_files and (p[:-5] if p.endswith(".meta") else p) not in owned]
    if only_h:
        print(f"{len(only_h)} file(s) exist only in the HDRP project and are not pipeline-specific (not deleted): {only_h[:5]}")
    return 0


def tar_paths(archive):
    import tarfile
    paths = set()
    with tarfile.open(archive) as tar:
        for m in tar.getmembers():
            if m.isfile() and m.name.endswith("/pathname"):
                paths.add(tar.extractfile(m).read().decode("utf-8").split("\n")[0])
    return paths


def check(args):
    repo = os.path.abspath(args.repo)
    out = os.path.join(repo, OUT_DIR)
    manifest_path = os.path.join(out, MANIFEST)
    if not os.path.isfile(manifest_path):
        print(f"no {MANIFEST} in {out}: run `build` first")
        return 1
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)

    problems, stale = [], []
    r_files, _ = scan(repo)
    by_id = {p["id"]: p for p in manifest["pipelines"]}
    for pid in ("urp", "hdrp"):
        if pid not in by_id:
            problems.append(f"{pid}: not in the manifest")
            continue
        pipe = by_id[pid]
        archive = os.path.join(out, pipe["package"])
        if not os.path.isfile(archive):
            problems.append(f"{pid}: {pipe['package']} is missing")
            continue
        in_archive = tar_paths(archive)
        for item in pipe["files"]:
            if f"{PREFIX}/{item['path']}" not in in_archive:
                problems.append(f"{pid}: {item['path']} is in the manifest but not the archive")
        listed = {f"{PREFIX}/{i['path']}" for i in pipe["files"]}
        extra = sorted(p for p in in_archive if p not in listed and "." in os.path.basename(p))
        if extra:
            problems.append(f"{pid}: {len(extra)} archive entries are not in the manifest, e.g. {extra[0]}")

    urp, hdrp = by_id.get("urp"), by_id.get("hdrp")
    if urp and hdrp:
        # The repository is the URP version, so it must hold exactly what the URP manifest describes
        for item in urp["files"]:
            p = r_files.get(item["path"])
            if p is None:
                problems.append(f"urp: {item['path']} is in the manifest but not in the repository")
            elif digest(p) != item["hash"]:
                stale.append(item["path"])
        for rel in urp["remove"]:
            if rel in r_files:
                problems.append(f"urp: {rel} is HDRP-only but exists in the repository")
        for rel in hdrp["remove"]:
            if rel not in r_files:
                problems.append(f"hdrp: {rel} is meant to be removed but is not in the repository")

    for pid, pipe in by_id.items():
        print(f"{pid}: {len(pipe['files'])} files, {len(pipe['remove'])} removed")
    if stale:
        print(f"\n{len(stale)} pipeline-specific file(s) changed since the packs were built; the HDRP "
              f"version of each may be out of date:")
        for p in stale[:40]:
            print("  " + p)
        if len(stale) > 40:
            print(f"  ... and {len(stale) - 40} more")
    if problems:
        print("\nFAILED")
        for p in problems:
            print("  " + p)
        return 1
    if stale and args.strict:
        print("\nFAILED (--strict): rebuild the packs from a refreshed HDRP project")
        return 1
    print("pipeline pack checks passed" + (" (with stale files, see above)" if stale else ""))
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("build")
    b.add_argument("--repo", required=True)
    b.add_argument("--hdrp", required=True)
    b.add_argument("--out")
    b.add_argument("--obsolete")
    b.add_argument("--kit-version", default="dev")
    b.add_argument("--max-missing", type=int, default=50)
    s = sub.add_parser("sync")
    s.add_argument("--repo", required=True)
    s.add_argument("--hdrp", required=True)
    s.add_argument("--dry-run", action="store_true")
    c = sub.add_parser("check")
    c.add_argument("--repo", default=".")
    c.add_argument("--strict", action="store_true")
    args = ap.parse_args()
    return {"build": build, "sync": sync, "check": check}[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
