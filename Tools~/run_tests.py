"""Run the kit's tests in a clean Unity project, from the command line.

Builds (or refreshes) a throwaway project that holds nothing but the kit: Unity's default new
project with the packages in dependencies.json merged in and the settings from
Tools~/ProjectSettings, as an Asset Store user's would be. That makes every run a clean-project compile check too - a missing dependency fails
here, not in someone else's project. Then runs the tests in Unity batch mode and prints the
result.

The kit is mirrored into the project, not linked, so the test project can never write into
your checkout, and it is a different project, so it works while your own editor is open.
The project and its Library are kept between runs: the first run imports everything (slow),
later ones only what changed.

--prepare-only writes the project and stops, for CI, which runs Unity itself (see
.github/workflows/tests.yml). The project may sit inside the kit checkout, as in CI; it is left
out of the mirror. When the kit already is the project's Assets/OpenMMORPG it is not mirrored.

usage: run_tests.py [--unity PATH] [--project DIR] [--platform EditMode|PlayMode]
                    [--category Logic;Harnessed] [--filter REGEX] [--nographics]
                    [--prepare-only]

Exit status: 0 all passed, 1 a test failed, 2 the run itself failed (compile errors,
no results).
"""
import argparse
import glob
import json
import os
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

KIT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(KIT, "Tools~")
# Always present in the test project, whatever dependencies.json says.
TEST_FRAMEWORK = ("com.unity.test-framework", "1.6.0")
# The built-in modules of Unity's new-project template. Every project an Asset Store user imports
# into has them, and the kit needs some (Audio, Video) that dependencies.json does not list.
# Unity adds them itself when it first opens a new project, but not always (CI), so add them here.
DEFAULT_MODULES = [
    "accessibility", "adaptiveperformance", "ai", "androidjni", "animation", "assetbundle", "audio",
    "cloth", "director", "imageconversion", "imgui", "jsonserialize", "particlesystem", "physics",
    "physics2d", "screencapture", "subsystems", "terrain", "terrainphysics", "tilemap", "ui",
    "uielements", "umbra", "unityanalytics", "unitywebrequest", "unitywebrequestassetbundle",
    "unitywebrequestaudio", "unitywebrequesttexture", "unitywebrequestwww", "vectorgraphics",
    "vehicles", "video", "vr", "wind", "xr",
]
# Not part of what Unity sees; skipped when mirroring.
SKIP = {".git"}


def default_project_dir():
    base = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), ".cache")
    return os.path.join(base, "OpenMMORPG", "TestProject")


def hub_editors():
    """Unity executables installed through Unity Hub, newest version first."""
    patterns = [
        r"C:\Program Files\Unity\Hub\Editor\*\Editor\Unity.exe",
        "/Applications/Unity/Hub/Editor/*/Unity.app/Contents/MacOS/Unity",
        os.path.expanduser("~/Unity/Hub/Editor/*/Editor/Unity"),
    ]
    found = [path for pattern in patterns for path in glob.glob(pattern)]

    def version(path):
        parts = path.replace("\\", "/").split("/")
        return parts[parts.index("Editor") + 1] if "Editor" in parts else ""

    return sorted(found, key=version, reverse=True)


def find_unity(explicit):
    if explicit:
        return explicit
    if os.environ.get("UNITY_EXE"):
        return os.environ["UNITY_EXE"]
    editors = hub_editors()
    if not editors:
        sys.exit("No Unity found. Pass --unity or set UNITY_EXE.")
    return editors[0]


def write_manifest(project):
    """Merge the kit's packages into the project's manifest, as importing the kit does.

    Never replace it: on the first run Unity may add its own template packages, just like the
    project an Asset Store user imports into. The default built-in modules are added if missing.
    """
    with open(os.path.join(TOOLS, "dependencies.json"), encoding="utf-8") as f:
        kit = json.load(f)["dependencies"]
    kit.setdefault(*TEST_FRAMEWORK)
    path = os.path.join(project, "Packages", "manifest.json")
    manifest = {"dependencies": {}}
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            manifest = json.load(f)
    dependencies = manifest.setdefault("dependencies", {})
    modules = {"com.unity.modules." + name: "1.0.0" for name in DEFAULT_MODULES}
    if all(dependencies.get(name) == version for name, version in kit.items()) and all(name in dependencies for name in modules):
        return
    for name, version in modules.items():
        dependencies.setdefault(name, version)
    dependencies.update(kit)
    manifest["dependencies"] = dict(sorted(dependencies.items()))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(manifest, indent=2) + "\n")


def copy_settings(project):
    target = os.path.join(project, "ProjectSettings")
    os.makedirs(target, exist_ok=True)
    source = os.path.join(TOOLS, "ProjectSettings")
    for name in os.listdir(source):
        shutil.copy2(os.path.join(source, name), os.path.join(target, name))


def mirror(source, target, project):
    """Make target an exact copy of source, copying only what changed (size or mtime).

    A project folder inside the kit checkout (as in CI, where the workspace root must be the
    git checkout) is left out, or the kit would be copied into itself.
    """
    copied = removed = 0
    os.makedirs(target, exist_ok=True)
    project = os.path.normcase(os.path.abspath(project))
    for dirpath, dirnames, filenames in os.walk(source):
        dirnames[:] = [d for d in dirnames if d not in SKIP
                       and os.path.normcase(os.path.abspath(os.path.join(dirpath, d))) != project]
        rel = os.path.relpath(dirpath, source)
        out_dir = os.path.normpath(os.path.join(target, rel))
        os.makedirs(out_dir, exist_ok=True)
        wanted = set(dirnames) | set(filenames)
        for name in os.listdir(out_dir):
            if name not in wanted:
                stale = os.path.join(out_dir, name)
                if os.path.isdir(stale) and not os.path.islink(stale):
                    shutil.rmtree(stale)
                else:
                    os.remove(stale)
                removed += 1
        for name in filenames:
            src, dst = os.path.join(dirpath, name), os.path.join(out_dir, name)
            s = os.stat(src)
            if os.path.exists(dst):
                d = os.stat(dst)
                if d.st_size == s.st_size and int(d.st_mtime) == int(s.st_mtime):
                    continue
            shutil.copy2(src, dst)
            copied += 1
    return copied, removed


def report(results_path, log_path):
    if not os.path.exists(results_path):
        print("No test results were written. Compile errors from the log:")
        with open(log_path, encoding="utf-8", errors="replace") as f:
            errors = sorted({line.strip() for line in f if "error CS" in line})
        for line in errors[:50] or ["(none found; read the log)"]:
            print("  " + line)
        print("Log: " + log_path)
        return 2

    run = ET.parse(results_path).getroot()
    for case in run.iter("test-case"):
        if case.get("result") == "Failed":
            message = case.findtext("failure/message", default="").strip()
            print("FAILED " + case.get("fullname"))
            for line in message.splitlines():
                print("    " + line)
    total, passed, failed = (int(run.get(k, 0)) for k in ("total", "passed", "failed"))
    skipped = int(run.get("skipped", 0))
    print(f"{passed} passed, {failed} failed, {skipped} skipped, {total} total "
          f"({float(run.get('duration', 0)):.1f}s)")
    print("Results: " + results_path)
    return 1 if failed else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--unity", help="Unity executable (default: UNITY_EXE, else the newest Hub install)")
    parser.add_argument("--project", default=default_project_dir(), help="test project folder, kept between runs")
    parser.add_argument("--platform", default="EditMode", choices=["EditMode", "PlayMode"])
    parser.add_argument("--category", help="NUnit categories, ';'-separated, e.g. Logic;Harnessed")
    parser.add_argument("--filter", help="test name filter (regex or ';'-separated full names)")
    parser.add_argument("--nographics", action="store_true", help="run without a graphics device (CI)")
    parser.add_argument("--prepare-only", action="store_true", help="write the project and stop, without running Unity")
    args = parser.parse_args()

    project = os.path.abspath(args.project)
    print(f"Project: {project}")
    write_manifest(project)
    copy_settings(project)
    target = os.path.join(project, "Assets", "OpenMMORPG")
    if os.path.exists(target) and os.path.samefile(KIT, target):
        print("Kit is already the project's Assets/OpenMMORPG; not mirrored")
    else:
        copied, removed = mirror(KIT, target, project)
        print(f"Kit mirrored: {copied} copied, {removed} removed")
    if args.prepare_only:
        return 0

    unity = find_unity(args.unity)
    print(f"Unity:   {unity}")

    results = os.path.join(project, "TestResults.xml")
    log = os.path.join(project, "TestRun.log")
    if os.path.exists(results):
        os.remove(results)
    command = [unity, "-batchmode", "-projectPath", project, "-runTests",
               "-testPlatform", args.platform, "-testResults", results, "-logFile", log]
    if args.category:
        command += ["-testCategory", args.category]
    if args.filter:
        command += ["-testFilter", args.filter]
    if args.nographics:
        command.append("-nographics")
    print("Running tests (the first run imports the whole kit and takes a while)...")
    subprocess.run(command)
    return report(results, log)


if __name__ == "__main__":
    sys.exit(main())
