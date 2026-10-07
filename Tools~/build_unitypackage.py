"""Build Unity .unitypackage archives without the editor.

Format (matches what Unity's AssetDatabase.ExportPackage writes):
  <guid>/asset       file contents          (omitted for folders)
  <guid>/asset.meta  the asset's .meta file (omitted for ProjectSettings)
  <guid>/pathname    project-relative path, no trailing newline
stored in a gzip'd tar, directory entry first.

usage:
  build_unitypackage.py kit  <src_dir> <asset_prefix> <out.unitypackage> [--deps <package.json>]
  build_unitypackage.py settings <ProjectSettings_dir> <out.unitypackage>

--deps embeds the "dependencies" of a UPM package.json as a Package Manager
manifest (a packagemanagermanifest/asset entry, the same thing Unity's own
exporter writes). Unity then adds those packages to the project when the
archive is imported, so the kit compiles in a fresh project.
"""
import gzip, io, json, os, re, sys, tarfile, time

# Unity's fixed pseudo-GUIDs for ProjectSettings assets
SETTINGS_IDS = {
    "InputManager.asset":    "00000000000000002000000000000000",
    "TagManager.asset":      "00000000000000003000000000000000",
    "ProjectSettings.asset": "00000000000000004000000000000000",
    "TimeManager.asset":     "00000000000000007000000000000000",
    "DynamicsManager.asset": "00000000000000008000000000000000",
    "QualitySettings.asset": "00000000000000009000000000000000",
}

def guid_of(meta_path):
    with open(meta_path, "rb") as f:
        m = re.search(rb"^guid:\s*([0-9a-fA-F]{32})", f.read(4096), re.M)
    if not m:
        raise SystemExit(f"no guid in {meta_path}")
    return m.group(1).decode().lower()


class _Archive:
    """Tar written in memory, then gzip'd with the plain gzip module.

    Unity's package reader rejects the gzip stream that tarfile's "w:gz" mode
    produces (it embeds the archive filename in the gzip header); the tar
    bytes are fine. Compressing the finished tar with gzip.compress yields a
    stream Unity accepts.
    """
    def __init__(self, out):
        self.out = out; self.buf = io.BytesIO()
        self.tar = tarfile.open(fileobj=self.buf, mode="w", format=tarfile.GNU_FORMAT)
    def __enter__(self): return self.tar
    def __exit__(self, *exc):
        self.tar.close()
        if exc[0] is None:
            with open(self.out, "wb") as f:
                f.write(gzip.compress(self.buf.getvalue(), compresslevel=6, mtime=0))

def add_bytes(tar, name, data, mtime):
    ti = tarfile.TarInfo(name)
    ti.size = len(data); ti.mtime = mtime; ti.mode = 0o777
    tar.addfile(ti, io.BytesIO(data))

def add_dir(tar, name, mtime):
    ti = tarfile.TarInfo(name + "/"); ti.type = tarfile.DIRTYPE
    ti.mtime = mtime; ti.mode = 0o777
    tar.addfile(ti)

def add_entry(tar, guid, pathname, asset_bytes, meta_bytes, mtime):
    add_dir(tar, guid, mtime)
    if asset_bytes is not None:
        add_bytes(tar, f"{guid}/asset", asset_bytes, mtime)
    if meta_bytes is not None:
        add_bytes(tar, f"{guid}/asset.meta", meta_bytes, mtime)
    add_bytes(tar, f"{guid}/pathname", pathname.encode("utf-8"), mtime)

def build_kit(src, prefix, out, deps_json=None):
    mtime = int(time.time()); n_files = n_dirs = 0; skipped = []; seen = set()
    with _Archive(out) as tar:
        if deps_json:
            with open(deps_json, encoding="utf-8") as f:
                deps = json.load(f)["dependencies"]
            manifest = json.dumps({"dependencies": deps}, separators=(",", ":")).encode("utf-8")
            add_dir(tar, "packagemanagermanifest", mtime)
            add_bytes(tar, "packagemanagermanifest/asset", manifest, mtime)
            print(f"embedded Package Manager manifest with {len(deps)} dependencies")
        for root, dirs, files in os.walk(src):
            # Unity ignores dot-folders and anything ending in "~", so neither
            # belongs in an exported package.
            dirs[:] = sorted(d for d in dirs if not d.startswith(".") and not d.endswith("~"))
            rel_root = os.path.relpath(root, src).replace("\\", "/")
            # folder entry (not for the root itself; Unity creates it on import)
            if rel_root != ".":
                meta = root + ".meta"
                if not os.path.isfile(meta):
                    skipped.append(rel_root + "/"); continue
                g = guid_of(meta)
                if g in seen: raise SystemExit(f"duplicate guid {g} at {rel_root}")
                seen.add(g)
                with open(meta, "rb") as f: mb = f.read()
                add_entry(tar, g, f"{prefix}/{rel_root}", None, mb, mtime); n_dirs += 1
            for fn in sorted(files):
                if fn.endswith(".meta") or fn.startswith("."):
                    continue
                p = os.path.join(root, fn); meta = p + ".meta"
                relp = fn if rel_root == "." else f"{rel_root}/{fn}"
                if not os.path.isfile(meta):
                    skipped.append(relp); continue
                g = guid_of(meta)
                if g in seen: raise SystemExit(f"duplicate guid {g} at {relp}")
                seen.add(g)
                with open(p, "rb") as f: ab = f.read()
                with open(meta, "rb") as f: mb = f.read()
                add_entry(tar, g, f"{prefix}/{relp}", ab, mb, mtime); n_files += 1
    print(f"wrote {out}: {n_files} files, {n_dirs} folders, {len(skipped)} skipped (no .meta)")
    for s in skipped: print("  skipped:", s)

def build_settings(src, out):
    mtime = int(time.time())
    with _Archive(out) as tar:
        for fn, g in SETTINGS_IDS.items():
            with open(os.path.join(src, fn), "rb") as f: ab = f.read()
            add_entry(tar, g, f"ProjectSettings/{fn}", ab, None, mtime)
    print(f"wrote {out}: {len(SETTINGS_IDS)} settings files")

if __name__ == "__main__":
    mode = sys.argv[1]
    if mode == "kit":
        deps = sys.argv[sys.argv.index("--deps") + 1] if "--deps" in sys.argv else None
        build_kit(sys.argv[2], sys.argv[3], sys.argv[4], deps)
    elif mode == "settings": build_settings(sys.argv[2], sys.argv[3])
    else: raise SystemExit(__doc__)
