"""Build Unity .unitypackage archives without the editor.

Format (matches what Unity's AssetDatabase.ExportPackage writes):
  <guid>/asset       file contents          (omitted for folders)
  <guid>/asset.meta  the asset's .meta file (omitted for ProjectSettings)
  <guid>/pathname    project-relative path, no trailing newline
stored in a gzip'd tar, directory entry first.

usage:
  build_unitypackage.py kit  <src_dir> <asset_prefix> <out.unitypackage> [--deps <package.json>]
                             [--version <tag or ref>] [--manifest-out <kit-manifest.json>]
  build_unitypackage.py settings <ProjectSettings_dir> <out.unitypackage>

--deps embeds the "dependencies" of a UPM package.json as a Package Manager
manifest (a packagemanagermanifest/asset entry, the same thing Unity's own
exporter writes). Unity then adds those packages to the project when the
archive is imported, so the kit compiles in a fresh project.

A kit archive always carries the kit manifest (KIT_MANIFEST below): every file
and folder it ships, by GUID, with a hash of its contents and of its .meta. An
installed kit keeps it, so an updater can tell which files changed between two
releases and which ones the user has edited. --version names the release (a
v* tag, or the branch for a test build); --manifest-out also writes the
manifest beside the archive, for the GitHub release.
"""
import gzip, hashlib, io, json, os, re, sys, tarfile, time

KIT_MANIFEST = "Tools/Install/KitManifest.json"
KIT_MANIFEST_GUID = "b0dc8875fe2440b1b1620858e0de0436"
KIT_MANIFEST_FORMAT = 1
KIT_MANIFEST_META = (
    "fileFormatVersion: 2\n"
    f"guid: {KIT_MANIFEST_GUID}\n"
    "TextScriptImporter:\n"
    "  externalObjects: {}\n"
    "  userData:\n"
    "  assetBundleName:\n"
    "  assetBundleVariant:\n"
).encode("utf-8")

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

def normalised(data):
    """CRLF read as LF when the file is text (no NUL in its first 8000 bytes):
    a Windows working tree is CRLF and an exported package is not."""
    if b"\x00" not in data[:8000]:
        return data.replace(b"\r\n", b"\n")
    return data

def content_hash(data):
    # SHA-1, as pipelines.json: this spots changes, it is not a security check, and the editor hashes SHA-1 natively
    # but SHA-256 only in managed code, about 13 times slower over the whole kit.
    return "sha1:" + hashlib.sha1(normalised(data)).hexdigest()

def kit_manifest(entries, version, deps_file):
    """The manifest's bytes: a header, then one line per file or folder, in path order."""
    tag = version if version and re.fullmatch(r"v\d+(\.\d+)*", version) else None
    head = {"format": KIT_MANIFEST_FORMAT,
            "version": tag[1:] if tag else (version or "dev"),
            "tag": tag}
    if deps_file:
        if "unity" in deps_file: head["unity"] = deps_file["unity"]
        head["dependencies"] = deps_file["dependencies"]
    lines = ",\n".join(f"    {json.dumps(g)}: {json.dumps(e)}" for g, e in entries.items())
    text = json.dumps(head, indent=2)[:-2] + ',\n  "files": {\n' + lines + "\n  }\n}\n"
    return text.encode("utf-8")


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

def build_kit(src, prefix, out, deps_json=None, version=None, manifest_out=None):
    mtime = int(time.time()); n_files = n_dirs = 0; skipped = []; seen = set(); entries = {}
    deps_file = None
    with _Archive(out) as tar:
        if deps_json:
            with open(deps_json, encoding="utf-8") as f:
                deps_file = json.load(f)
            deps = deps_file["dependencies"]
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
                entries[g] = {"path": rel_root, "folder": True, "meta": content_hash(mb)}
            for fn in sorted(files):
                if fn.endswith(".meta") or fn.startswith("."):
                    continue
                p = os.path.join(root, fn); meta = p + ".meta"
                relp = fn if rel_root == "." else f"{rel_root}/{fn}"
                if relp == KIT_MANIFEST:
                    continue  # an imported release's copy; this build writes its own
                if not os.path.isfile(meta):
                    skipped.append(relp); continue
                g = guid_of(meta)
                if g in seen: raise SystemExit(f"duplicate guid {g} at {relp}")
                seen.add(g)
                with open(p, "rb") as f: ab = f.read()
                with open(meta, "rb") as f: mb = f.read()
                add_entry(tar, g, f"{prefix}/{relp}", ab, mb, mtime); n_files += 1
                entries[g] = {"path": relp, "hash": content_hash(ab), "meta": content_hash(mb),
                              "size": len(normalised(ab))}
        if KIT_MANIFEST_GUID in seen: raise SystemExit(f"duplicate guid {KIT_MANIFEST_GUID} at {KIT_MANIFEST}")
        kit_bytes = kit_manifest(entries, version, deps_file)
        add_entry(tar, KIT_MANIFEST_GUID, f"{prefix}/{KIT_MANIFEST}", kit_bytes, KIT_MANIFEST_META, mtime)
    if manifest_out:
        with open(manifest_out, "wb") as f: f.write(kit_bytes)
    print(f"wrote {out}: {n_files} files, {n_dirs} folders, {len(skipped)} skipped (no .meta)")
    print(f"kit manifest: {len(entries)} entries, {len(kit_bytes) / 1e6:.1f} MB"
          + (f", also written to {manifest_out}" if manifest_out else ""))
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
    opt = lambda name: sys.argv[sys.argv.index(name) + 1] if name in sys.argv else None
    if mode == "kit":
        build_kit(sys.argv[2], sys.argv[3], sys.argv[4], opt("--deps"), opt("--version"), opt("--manifest-out"))
    elif mode == "settings": build_settings(sys.argv[2], sys.argv[3])
    else: raise SystemExit(__doc__)
