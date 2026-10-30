# Shortcuts for the scripts in this folder. It lives in Tools~ so Unity ignores it and it never
# ships; run it from the kit root with either of:
#
#   make -C Tools~ check
#   make -f Tools~/Makefile check
#
# Every target is a thin wrapper around a command in README.md, so CI and a bare shell stay the
# reference. Builds land in Tools~/build, not the kit root: an archive there would be imported by
# the Unity project around the checkout.
#
# Recipes are single commands with no shell-specific syntax, so they work under sh and cmd.exe.
# Paths are relative because the checkout path may contain spaces, which make's path functions
# split on.

PYTHON ?= python

# Works from the kit root (-f) or from Tools~ (-C).
ifneq ($(wildcard check_repo.py),)
TOOLS := .
KIT := ..
else
TOOLS := Tools~
KIT := .
endif

BUILD := $(TOOLS)/build
PACKAGE := $(BUILD)/OpenMMORPG.unitypackage
KIT_MANIFEST := $(BUILD)/kit-manifest.json

# make package VERSION=v1.1.5 stamps the kit manifest and writes it beside the archive, as a release
# does; make packs VERSION=v1.1.5 records it in pipelines.json.
VERSION ?=
# make packs HDRP=<converted project>/Assets/OpenMMORPG; the default is the sibling HDRP project.
HDRP ?= $(KIT)/../../../OpenMMORPG-HDRP/Assets/OpenMMORPG
# make test CATEGORY="Logic;Harnessed" FILTER=SQLiteCharacterTests PLATFORM=PlayMode UNITY=<Unity.exe> PROJECT=<dir>
CATEGORY ?=
FILTER ?=
PLATFORM ?= EditMode
UNITY ?=
PROJECT ?=

TEST_ARGS = --platform $(PLATFORM) \
	$(if $(CATEGORY),--category "$(CATEGORY)") \
	$(if $(FILTER),--filter "$(FILTER)") \
	$(if $(UNITY),--unity "$(UNITY)") \
	$(if $(PROJECT),--project "$(PROJECT)")

# make release patch|minor|major [NOTES=<file>] [DRY_RUN=1] [YES=1]: the bump is read from the goals,
# and the three words are no-op targets so make does not try to build them.
BUMP = $(filter major minor patch,$(MAKECMDGOALS))
NOTES ?=
DRY_RUN ?=
YES ?=

empty :=
I := $(empty)  $(empty)

.DEFAULT_GOAL := help
.PHONY: help check release-check check-repo check-packs check-packs-strict package check-package \
	settings packs packs-sync packs-sync-dry test test-play clean release major minor patch

# $(info) rather than echo: no shell, so the columns survive sh, which collapses spaces in echo.
help:
	$(info Checks - no Unity needed)
	$(info $(I)check               what CI runs on every push: check-repo, check-packs, package, check-package)
	$(info $(I)release-check       the same with the pipeline packs held to release rules - run before tagging)
	$(info $(I)check-repo          missing or orphan .meta, duplicate GUIDs, case clashes, third-party notices)
	$(info $(I)check-packs         the URP and HDRP archives match pipelines.json)
	$(info $(I)check-packs-strict  ...and no pipeline-specific file changed since the packs were built)
	$(info Builds - output in Tools~/build)
	$(info $(I)package             build OpenMMORPG.unitypackage [VERSION=v1.x.y also writes kit-manifest.json])
	$(info $(I)check-package       inspect the built archive)
	$(info $(I)settings            rebuild Tools/Install/OpenMMORPG_Settings.unitypackage from Tools~/ProjectSettings)
	$(info $(I)packs               rebuild the pipeline packs from the HDRP project [HDRP=... VERSION=...])
	$(info $(I)packs-sync          copy non-pipeline changes into the HDRP project [HDRP=...])
	$(info $(I)packs-sync-dry      list what packs-sync would copy)
	$(info $(I)clean               remove Tools~/build)
	$(info Tests - Unity batch mode)
	$(info $(I)test                run the tests [CATEGORY=... FILTER=... PLATFORM=... UNITY=... PROJECT=...])
	$(info $(I)test-play           run the PlayMode tests)
	$(info Release - tags master and pushes the tag, which publishes it)
	$(info $(I)release patch       check, tag and push the next version: release major / minor / patch)
	$(info $(I)                    [NOTES=file for the changelog, DRY_RUN=1 to stop before tagging, YES=1])
	@cd .

check: check-repo check-packs check-package

release-check: check-repo check-packs-strict check-package

check-repo:
	$(PYTHON) "$(TOOLS)/check_repo.py" "$(KIT)"

check-packs:
	$(PYTHON) "$(TOOLS)/pipeline_packs.py" check --repo "$(KIT)"

check-packs-strict:
	$(PYTHON) "$(TOOLS)/pipeline_packs.py" check --repo "$(KIT)" --strict

# Always rebuilt: the inputs are the whole kit.
package:
	$(PYTHON) -c "import os; os.makedirs(r'$(BUILD)', exist_ok=True)"
	$(PYTHON) "$(TOOLS)/build_unitypackage.py" kit "$(KIT)" Assets/OpenMMORPG "$(PACKAGE)" --deps "$(TOOLS)/dependencies.json" \
		$(if $(VERSION),--version $(VERSION) --manifest-out "$(KIT_MANIFEST)")

check-package: package
	$(PYTHON) "$(TOOLS)/check_package.py" "$(PACKAGE)"

settings:
	$(PYTHON) "$(TOOLS)/build_unitypackage.py" settings "$(TOOLS)/ProjectSettings" "$(KIT)/Tools/Install/OpenMMORPG_Settings.unitypackage"

packs:
	$(PYTHON) "$(TOOLS)/pipeline_packs.py" build --repo "$(KIT)" --hdrp "$(HDRP)" \
		$(if $(VERSION),--kit-version $(VERSION:v%=%))

packs-sync:
	$(PYTHON) "$(TOOLS)/pipeline_packs.py" sync --repo "$(KIT)" --hdrp "$(HDRP)"

packs-sync-dry:
	$(PYTHON) "$(TOOLS)/pipeline_packs.py" sync --repo "$(KIT)" --hdrp "$(HDRP)" --dry-run

test:
	$(PYTHON) "$(TOOLS)/run_tests.py" $(TEST_ARGS)

test-play: PLATFORM = PlayMode
test-play: test

clean:
	$(PYTHON) -c "import shutil; shutil.rmtree(r'$(BUILD)', ignore_errors=True)"

release:
	$(if $(BUMP),,$(error say which part to bump: make release major, minor or patch))
	$(if $(word 2,$(BUMP)),$(error bump one part at a time, not $(BUMP)))
	$(PYTHON) "$(TOOLS)/release.py" $(BUMP) $(if $(NOTES),--notes "$(NOTES)") $(if $(DRY_RUN),--dry-run) $(if $(YES),--yes)

major minor patch:
	@$(if $(filter release,$(MAKECMDGOALS)),,$(error $@ only means something after release: make release $@))
	@cd .
