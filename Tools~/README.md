# Release tooling

Ignored by Unity because of the trailing `~`, and excluded from the exported
package, so nothing here ships to users.

`build_unitypackage.py` writes a `.unitypackage` without opening the editor. The
[Asset Store package workflow](../.github/workflows/asset-store-package.yml) runs it
on every `v*` tag:

```sh
python "Tools~/build_unitypackage.py" kit . Assets/OpenMMORPG OpenMMORPG.unitypackage --deps "Tools~/dependencies.json"
```

`dependencies.json` lists the Unity packages the kit compiles against. The builder
embeds them in the archive as a Package Manager manifest, so importing the package
adds them to the project.

## Kit manifest

Every kit archive carries `Tools/Install/KitManifest.json`: each file and folder it
ships, keyed by GUID, with its path, a SHA-1 of its contents and of its `.meta`, and
its size. Text files are hashed with CRLF read as LF, the same rule as `pipelines.json`,
so a Windows checkout matches the archive. An installed kit keeps the manifest of the
release it came from, which is what lets an updater tell the files that changed upstream
from the files the user has edited. The manifest is generated, never committed; the
builder skips a copy left in the tree by an imported release.

`--version` names the release in the manifest (`v1.1.5` becomes version `1.1.5`, tag
`v1.1.5`; anything else is a test build with no tag), and `--manifest-out` also writes
it beside the archive. The release workflow does both:

```sh
python "Tools~/build_unitypackage.py" kit . Assets/OpenMMORPG OpenMMORPG.unitypackage --deps "Tools~/dependencies.json" --version v1.1.5 --manifest-out kit-manifest.json
```

## Cutting a release

1. Tag the kit, for example `git tag v1.1.0 && git push origin v1.1.0`.
2. The workflow builds the archive, checks it, and publishes a GitHub release with
   `OpenMMORPG-v1.1.0.unitypackage` and `kit-manifest.json` attached.
3. Upload that archive to the Asset Store through Unity's Asset Store Tools.

To rehearse without publishing, run the workflow by hand from the Actions tab. It
builds and verifies the same archive and leaves it on the run as an artifact.

## Project settings

`ProjectSettings/` holds the sanitised settings the kit expects. Rebuild the archive
the kit ships after changing them:

```sh
python "Tools~/build_unitypackage.py" settings "Tools~/ProjectSettings" "Tools/Install/OpenMMORPG_Settings.unitypackage"
```

That archive travels inside the kit, and `Tools > Open MMORPG > Install > Import
Project Settings` imports it. Keep project specific values out of
`ProjectSettings.asset`, namely `productName`, `cloudProjectId`, `organizationId`,
`projectName`, `metroPackageName` and `metroApplicationDescription`.

## Render pipeline packs

The kit is URP; its HDRP version travels inside it as an archive, with a URP one to switch back, and
an editor window installs the one that matches the project. `pipeline_packs.py` builds those archives
from a converted copy of the project and checks them. How it works, and what to do when a
pipeline-specific file changes, is in [`pipelines/README.md`](pipelines/README.md).

```sh
python "Tools~/pipeline_packs.py" build --repo . --hdrp "<HDRP project>/Assets/OpenMMORPG"
python "Tools~/pipeline_packs.py" check --repo .
```

## Checks

[Checks](../.github/workflows/checks.yml) runs on every push and pull request, needs no
Unity install, and finishes in seconds. Run the same checks locally before pushing:

```sh
python "Tools~/check_repo.py" .
python "Tools~/pipeline_packs.py" check --repo .
python "Tools~/build_unitypackage.py" kit . Assets/OpenMMORPG OpenMMORPG.unitypackage --deps "Tools~/dependencies.json"
python "Tools~/check_package.py" OpenMMORPG.unitypackage
```

`check_repo.py` catches an asset with no `.meta`, which the exporter skips so the file
silently never ships, plus orphan `.meta` files, duplicate GUIDs, names differing only
in case, and a `ThirdParty` component missing from `THIRD-PARTY-NOTICES.md`.

`check_package.py` inspects a built archive: everything under `Assets/OpenMMORPG`, no
build tooling or CI config inside, the licence, notices, project settings archive and
settings menu item all present, the render pipeline installer with its manifest and both
pipelines' archives, and URP among the embedded dependencies (the kit's own files are the URP
version, so it needs URP to compile before the installer has run), and a kit manifest
that lists every entry with the hashes recomputed from the archive. The release
workflow runs the same script, so a tag build cannot pass looser rules than a push.

## Tests

`run_tests.py` runs the kit's tests in Unity batch mode, in a clean project that holds only
the kit:

```sh
python "Tools~/run_tests.py"
python "Tools~/run_tests.py" --category "Logic;Harnessed"
python "Tools~/run_tests.py" --filter SQLiteCharacterTests
```

The project starts as Unity's default new project, with the packages in `dependencies.json`
(plus the test framework) merged in and the settings from `ProjectSettings/`, as an Asset
Store user's would. That makes every run a
clean-project compile check as well: a dependency missing from `dependencies.json` fails
here first. The kit is mirrored into the project rather than linked, so the run can never
write into your checkout, and it works while your own editor is open.

The project is kept between runs, in `%LOCALAPPDATA%\OpenMMORPG\TestProject` on Windows and
`~/.cache/OpenMMORPG/TestProject` elsewhere (`--project` picks another folder). The first run
imports the whole kit and takes a while; later runs import only what changed. Unity is
`--unity`, else `UNITY_EXE`, else the newest Unity Hub install. The exit status is 0 when
every test passed, 1 when one failed and 2 when the run did not finish (compile errors are
printed from the log). Where tests go and the helpers they use are in
[`Tests/README.md`](../Tests/README.md).

The [Tests](../.github/workflows/tests.yml) workflow runs the same suite on GitHub for every pull
request and every push to `master` or `develop`. It builds the project with
`run_tests.py --prepare-only`, which also adds the built-in modules of Unity's new-project
template, and runs Unity through GameCI's `unity-test-runner`. It needs a Unity licence in the
repository secrets: `UNITY_LICENSE` (the contents of `Unity_lic.ulf`), `UNITY_EMAIL` and
`UNITY_PASSWORD`. Without them the job is skipped with a notice rather than failing.
