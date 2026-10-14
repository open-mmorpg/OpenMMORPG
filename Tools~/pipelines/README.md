# Render pipeline packs

The kit in this repository is the **URP** version. The **HDRP** version of the demo is a set of files
that differ from it, made in a converted copy of the project (the HDRP project). Both versions of
just those files ship inside the kit, in `Tools/Install/Pipelines`, and an editor window
(`Tools/Editor/PipelineInstaller`) imports the one that matches the project it is in.

```
Tools/Install/Pipelines/
  pipelines.json               what each pipeline's version is made of, with a hash of every file
  OpenMMORPG_URP.unitypackage  the repository's own versions of those files (to switch back to URP)
  OpenMMORPG_HDRP.unitypackage the converted versions, plus the files only HDRP has
```

Nothing pipeline-specific is loose in the kit except the URP version itself. The installer reads
`pipelines.json`, hashes the files in the project to see which version is there, and on request
imports the other archive and removes what the new version does not want. Files are compared with
CRLF read as LF, because a Windows working tree is CRLF and an exported package is not.

## What counts as pipeline-specific

Anything that differs between the two trees: materials, shaders, scenes, prefabs with lights or
cameras, the scripts that call pipeline APIs, and the graphics settings scripts. Anything that is
the same in both is simply the kit. Files that are the same in both but meaningless under HDRP
(URP pipeline assets and their renderers, URP volume profiles, a `Skybox/Cubemap` material) are
listed in `hdrp-obsolete.txt` here, so an HDRP install removes them and a URP install puts them
back. Files that exist only in the URP tree (something deleted in the HDRP project) are found by
comparing and need no entry.

A change that is **not** pipeline-specific, such as the pipeline-aware welcome window, is made in
this repository like any other change and copied into the HDRP project; once the two trees agree
on it, it is part of the kit and not of a pack.

## Rebuilding the packs

```sh
python "Tools~/pipeline_packs.py" sync  --repo . --hdrp "<HDRP project>/Assets/OpenMMORPG"
python "Tools~/pipeline_packs.py" build --repo . --hdrp "<HDRP project>/Assets/OpenMMORPG" --kit-version 1.1.0
```

Run from the repository root (`Assets/OpenMMORPG`).

`sync` first copies every change in the repository that is *not* pipeline-specific into the HDRP
project (a new script, a fixed prefab, this README), so that what is left different between the two
trees is only real HDRP work. It never touches a file the manifest says is pipeline-specific, and
`--dry-run` lists what it would copy.

`build` then prints how many files differ, are HDRP-only and are removed for HDRP, and writes the two
archives and `pipelines.json`. Commit all three, with their `.meta` files if they are new. The
archives are plain binary files in git (about 8 MB together), so rebuild when pipeline-specific
files have changed and before a release, not after every edit.

`check` needs neither Unity nor the HDRP project and is part of CI:

```sh
python "Tools~/pipeline_packs.py" check --repo .            # archives match the manifest; lists stale files
python "Tools~/pipeline_packs.py" check --repo . --strict   # the same, and stale files fail (releases)
```

## When a pipeline-specific file changes in the repository

`check` lists the files whose URP version has changed since the packs were built; each one's HDRP
version is now out of date. `pipelines.json` records the commit it was built from (`builtFrom`), so
`git diff <builtFrom> -- <file>` shows what changed. For each stale file:

- **A material, scene, prefab or terrain asset:** copy the new file into the HDRP project over its
  converted version and run the matching converter again (the HDRP project's
  `Tools > HDRP Conversion` menu; converters can be run again safely, and each is described in its
  source). Scenes and prefabs go through *Convert Lights and Cameras*, which must run on scenes
  first, then prefabs.
- **A script or shader:** the HDRP version is hand-written. Apply the same change to it by hand.
- **Neither pipeline has it changed in kind (a comment, a tooltip):** apply the same edit to the
  HDRP version.

Then rebuild. A release workflow run on a `v*` tag fails if any pipeline-specific file is stale, so
the HDRP content in a release is never behind the kit.

## The HDRP project

It is a copy of the project with the kit imported and converted in place (GUIDs kept), plus its own
`Assets/_HDRPConversion` folder of editor tools that does the converting and is never shipped.
Keep it on the same Unity version as the kit.

> **Never copy the repository over the HDRP project, and never import the kit into it.** The
> repository's files are the URP version. Either one puts the URP originals back over the converted
> files, drops the files that exist only under HDRP (the HDRP scripts, volume profiles and shader
> includes, so the project stops compiling), brings back the URP-only files, and an import also adds
> the URP packages to the project again. A scene left open through it picks up stray light overrides.
> Bring repository changes in with `sync`, which skips every pipeline-specific file.

**The pack is the only backup.** The HDRP project is not under version control, and the installer's
own backup does not cover this: it copies aside only files you have edited past the shipped version,
and in an accident like this the converted files are the shipped version. So the HDRP archive in
`Tools/Install/Pipelines` is the one copy of the converted content that outlives it, and HDRP work is
only as safe as the last `build`. Rebuild after any change worth keeping, and commit.

**If it happens anyway**, nothing is lost that is in the pack:

1. Do not save the scene that is open. Open an empty one first if the editor asks about it.
2. Import `Tools/Install/Pipelines/OpenMMORPG_HDRP.unitypackage` with **Assets → Import Package →
   Custom Package** (this works even while the project does not compile).
3. Once it compiles, open **Tools → Open MMORPG → Install → Render Pipeline Content**. It reports the HDRP
   content with a few files "changed or removed" (the URP-only ones that came back); press
   **Reinstall the shipped High Definition Render Pipeline (HDRP) content**. It removes them and
   checks every file against `pipelines.json`.
4. Check the result against the repository: `sync --dry-run` should list nothing.

Work done in the project after the last `build` is not in the pack and cannot be restored this way.
