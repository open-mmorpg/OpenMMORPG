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
adds them to the project. The installer declares the same list in its own
`package.json`; the workflow warns when the two drift apart.

## Cutting a release

1. Tag the kit, for example `git tag v1.1.0 && git push origin v1.1.0`.
2. The workflow builds the archive, checks it, and publishes a GitHub release with
   `OpenMMORPG.unitypackage` attached.
3. Upload that archive to the Asset Store through Unity's Asset Store Tools.

To rehearse without publishing, run the workflow by hand from the Actions tab. It
builds and verifies the same archive and leaves it on the run as an artifact.

## Project settings

`ProjectSettings/` holds the sanitised settings the kit expects. Rebuild the archive
the kit ships after changing them:

```sh
python "Tools~/build_unitypackage.py" settings "Tools~/ProjectSettings" "Tools/Install/OpenMMORPG_Settings.unitypackage"
```

That archive travels inside the kit, and `Open MMORPG > Install > Import Project
Settings` imports it, so people who install from the Asset Store get the option
without the installer package. Keep project specific values out of
`ProjectSettings.asset`, namely `productName`, `cloudProjectId`, `organizationId`,
`projectName`, `metroPackageName` and `metroApplicationDescription`.

## Checks

[Checks](../.github/workflows/checks.yml) runs on every push and pull request, needs no
Unity install, and finishes in seconds. Run the same checks locally before pushing:

```sh
python "Tools~/check_repo.py" .
python "Tools~/build_unitypackage.py" kit . Assets/OpenMMORPG OpenMMORPG.unitypackage --deps "Tools~/dependencies.json"
python "Tools~/check_package.py" OpenMMORPG.unitypackage
```

`check_repo.py` catches an asset with no `.meta`, which the exporter skips so the file
silently never ships, plus orphan `.meta` files, duplicate GUIDs, names differing only
in case, and a `ThirdParty` component missing from `THIRD-PARTY-NOTICES.md`.

`check_package.py` inspects a built archive: everything under `Assets/OpenMMORPG`, no
build tooling or CI config inside, the licence, notices, project settings archive and
settings menu item all present, and no render pipeline among the embedded
dependencies, so the kit stays usable under URP, HDRP and Built-in. The release
workflow runs the same script, so a tag build cannot pass looser rules than a push.
