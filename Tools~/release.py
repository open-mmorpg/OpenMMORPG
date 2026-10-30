"""Cut a release: work out the next version, check the kit, tag master and push the tag.

Pushing a v* tag is the whole release: the Asset Store package workflow builds the archive,
checks it and publishes the GitHub release. This script makes sure the tag lands on the right
commit and that the workflow will not fail on something a local run would have caught.

  1. refuses unless HEAD is master, the working tree is clean and master matches origin/master,
     so the tag names a commit everyone has and nothing uncommitted slipped into the checks
  2. bumps the newest vX.Y.Z tag: major (2.0.0), minor (1.3.0) or patch (1.2.2)
  3. runs the release checks: check_repo, pipeline_packs check --strict, then builds the archive
     into Tools~/build and runs check_package on it, the same steps the tag build runs
  4. asks before tagging, then opens your editor on an annotated tag message headed
     "Open MMORPG X.Y.Z" with the commits since the last release listed as comments; that
     message is the changelog, and leaving it empty cancels the release
  5. pushes the tag to origin

usage: release.py major|minor|patch [--notes FILE] [--yes] [--dry-run]

--notes takes the tag message from a file instead of the editor. --yes skips the question
(for scripting; the editor still opens unless --notes is given). --dry-run runs the checks and
stops before tagging.
"""
import argparse
import os
import re
import subprocess
import sys

TOOLS = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(TOOLS)
BUILD = os.path.join(TOOLS, "build")
BRANCH = "master"
REMOTE = "origin"
TAG_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")


def git(*args, check=True):
    result = subprocess.run(["git", "-C", KIT, *args], capture_output=True, text=True)
    if check and result.returncode != 0:
        raise SystemExit(f"git {' '.join(args)} failed:\n{result.stderr.strip()}")
    return result.stdout.strip()


def fail(message):
    raise SystemExit(f"release: {message}")


def check_checkout():
    branch = git("branch", "--show-current")
    if branch != BRANCH:
        fail(f"on '{branch or 'a detached HEAD'}', releases are tagged on {BRANCH}")
    dirty = git("status", "--porcelain")
    if dirty:
        fail("the working tree has changes; commit or stash them first:\n" + dirty)
    print(f"fetching {REMOTE}...")
    git("fetch", "--tags", REMOTE)
    behind, ahead = git("rev-list", "--left-right", "--count", f"{REMOTE}/{BRANCH}...HEAD").split()
    if behind != "0":
        fail(f"{BRANCH} is {behind} commit(s) behind {REMOTE}/{BRANCH}; pull first")
    if ahead != "0":
        fail(f"{BRANCH} is {ahead} commit(s) ahead of {REMOTE}/{BRANCH}; "
             "changes reach master through a pull request, so the tag must name a pushed commit")


def latest_version():
    versions = [tuple(map(int, m.groups())) for m in map(TAG_RE.match, git("tag", "-l", "v*").splitlines()) if m]
    if not versions:
        fail("no vX.Y.Z tag to bump from")
    return max(versions)


def bump(version, part):
    major, minor, patch = version
    if part == "major":
        return (major + 1, 0, 0)
    if part == "minor":
        return (major, minor + 1, 0)
    return (major, minor, patch + 1)


def run_checks(tag):
    package = os.path.join(BUILD, "OpenMMORPG.unitypackage")
    os.makedirs(BUILD, exist_ok=True)
    steps = [
        ["check_repo.py", KIT],
        ["pipeline_packs.py", "check", "--repo", KIT, "--strict"],
        ["build_unitypackage.py", "kit", KIT, "Assets/OpenMMORPG", package,
         "--deps", os.path.join(TOOLS, "dependencies.json"),
         "--version", tag, "--manifest-out", os.path.join(BUILD, "kit-manifest.json")],
        ["check_package.py", package],
    ]
    for script, *args in steps:
        print(f"\n== {script}")
        if subprocess.run([sys.executable, os.path.join(TOOLS, script), *args]).returncode != 0:
            fail(f"{script} failed; nothing was tagged")


def confirm(question):
    try:
        return input(f"{question} [y/N] ").strip().lower() in ("y", "yes")
    except EOFError:
        return False


def main():
    # The checks' output goes straight to the terminal; keep ours in order with it when piped.
    sys.stdout.reconfigure(line_buffering=True)
    parser =argparse.ArgumentParser(description="Tag and push the next kit release.")
    parser.add_argument("part", choices=["major", "minor", "patch"])
    parser.add_argument("--notes", help="file holding the tag message (default: write it in your editor)")
    parser.add_argument("--yes", action="store_true", help="do not ask before tagging")
    parser.add_argument("--dry-run", action="store_true", help="run the checks, then stop before tagging")
    args = parser.parse_args()
    if args.notes and not os.path.isfile(args.notes):
        fail(f"no notes file at {args.notes}")

    check_checkout()
    previous = latest_version()
    version = bump(previous, args.part)
    name, tag, last = ".".join(map(str, version)), "v" + ".".join(map(str, version)), "v" + ".".join(map(str, previous))
    if git("tag", "-l", tag) or git("ls-remote", "--tags", REMOTE, f"refs/tags/{tag}"):
        fail(f"{tag} already exists")
    log = git("log", "--oneline", "--no-decorate", f"{last}..HEAD")
    if not log:
        fail(f"nothing has been committed since {last}")

    print(f"\n{last} -> {tag} ({args.part}), tagging {git('rev-parse', '--short', 'HEAD')} on {BRANCH}")
    print(f"{len(log.splitlines())} commit(s) since {last}:\n" + "\n".join("  " + line for line in log.splitlines()))
    run_checks(tag)

    if args.dry_run:
        print(f"\ndry run: checks passed, {tag} not created")
        return
    if not args.yes and not confirm(f"\nTag {tag} and push it to {REMOTE}? This publishes the release."):
        fail("cancelled; nothing was tagged")

    if args.notes:
        tagged = subprocess.run(["git", "-C", KIT, "tag", "-a", tag, "-F", args.notes])
    else:
        # The editor strips the # lines, so the commit list is a prompt, not part of the message.
        template = f"Open MMORPG {name}\n\n" + "\n".join(
            [f"# Write the changelog for {tag} above: what changed, in paragraphs, for kit users.",
             "# Leave the message empty to cancel the release.",
             f"# Commits since {last}:"] + ["#   " + line for line in log.splitlines()])
        tagged = subprocess.run(["git", "-C", KIT, "tag", "-a", tag, "-e", "-m", template])
    if tagged.returncode != 0:
        fail("the tag was not created; nothing was pushed")
    # git keeps a tag whose message was emptied in the editor when -m prefilled it, so check here.
    if not git("tag", "-l", "--format=%(contents)", tag):
        git("tag", "-d", tag)
        fail("the tag message was empty; cancelled, nothing was pushed")

    if subprocess.run(["git", "-C", KIT, "push", REMOTE, tag]).returncode != 0:
        fail(f"the push failed; {tag} exists only here. Retry with: git push {REMOTE} {tag}\n"
             f"or remove it with: git tag -d {tag}")
    url = re.sub(r"\.git$", "", git("remote", "get-url", REMOTE)).replace("git@github.com:", "https://github.com/")
    print(f"\npushed {tag}. The Asset Store package workflow is building the release:\n  {url}/actions\n  {url}/releases/tag/{tag}")


if __name__ == "__main__":
    main()
