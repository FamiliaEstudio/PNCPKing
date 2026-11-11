"""Republish the tested Windows build in the existing v1.2.48 release."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

REPO = "FamiliaEstudio/PNCPKing"
TAG = "v1.2.48"
PREVIOUS = "c96c9bd55d0d7f682a8bba5f26bf82aa8784f306"
REVISION = "0719c2dd3c066e6b04592f4572e52ff9209f5e88"
RUN = "43084979588"
NAMES = ("PNCPKing.exe", "app-update.json")


def command(*args):
    return subprocess.check_output(args, text=True).strip()


def api(resource, *args):
    output = command("gh", "api", f"repos/{REPO}/{resource}", *args)
    return json.loads(output) if output else None


def rename(asset, name):
    api(f"releases/assets/{asset['id']}", "--method", "PATCH", "-f", f"name={name}")


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def move_tag(expected, target):
    ref = f"refs/tags/{TAG}"
    command("git", "push", f"--force-with-lease={ref}:{expected}", "origin", f"{target}:{ref}")


def main():
    release = api(f"releases/tags/{TAG}")
    run = api(f"actions/runs/{RUN}")
    if (run["conclusion"] != "success" or run["head_sha"] != REVISION
            or run["path"] != ".github/workflows/publish-windows.yml"):
        raise RuntimeError("O artefato precisa ter passado na validação Windows desta revisão.")
    if (release["id"] != 442063967 or release["draft"] or release["prerelease"] or release["immutable"]
            or api(f"git/ref/tags/{TAG}")["object"]["sha"] != PREVIOUS):
        raise RuntimeError("A release mudou desde a autorização.")
    old = {a["name"]: a for a in release["assets"]}
    if set(old) != set(NAMES):
        raise RuntimeError("Os anexos da publicação mudaram.")
    # Only the verified build artifacts enter the release. The operations branch is never tagged.
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        command("gh", "run", "download", RUN, "-R", REPO, "-n", f"PNCPKing-win-x64-{REVISION}", "-D", str(root))
        manifest = json.loads((root / "app-update.json").read_text(encoding="utf-8-sig"))
        exe = root / "PNCPKing.exe"
        if (set(p.name for p in root.iterdir()) != set(NAMES) or manifest["version"] != "1.2.48"
                or manifest["schema"] != 34 or manifest["platform"] != "win-x64"
                or manifest["file"] != {"name": exe.name, "size": exe.stat().st_size, "sha256": digest(exe)}
                or old[exe.name]["digest"] == "sha256:" + manifest["file"]["sha256"]):
            raise RuntimeError("Executável ou manifesto de revisão divergente.")
        suffix = "." + os.environ["GITHUB_RUN_ID"]
        pending = {name: name + suffix + ".pending" for name in NAMES}
        expected = {}
        backup_names = []
        activated = []
        new = {}
        moved_tag = False
        promoted = False
        try:
            # Upload first. The current downloads remain available during the large transfer.
            for name, staged_name in pending.items():
                path = root / name
                expected[name] = "sha256:" + digest(path)
                path.rename(root / staged_name)
            command("gh", "release", "upload", TAG, *(str(root / n) for n in pending.values()), "-R", REPO)
            by_name = {a["name"]: a for a in api(f"releases/{release['id']}/assets")}
            for name in NAMES:
                asset = by_name[pending[name]]
                if asset["state"] != "uploaded" or asset["digest"] != expected[name]:
                    raise RuntimeError(f"Anexo divergente após upload: {name}")
                new[name] = asset
            for name in NAMES:
                rename(old[name], name + suffix + ".previous")
                backup_names.append(name)
                rename(new[name], name)
                activated.append(name)
            move_tag(PREVIOUS, REVISION)
            moved_tag = True
            # Read notes from the tested commit, not this working tree.
            notes = root / "release-notes.md"
            notes.write_text(command("git", "show", f"{REVISION}:docs/releases/{TAG}.md") + "\n")
            command("gh", "release", "edit", TAG, "-R", REPO, "--target", REVISION, "--notes-file", str(notes))
            promoted = True
        finally:
            if not promoted:
                if moved_tag:
                    move_tag(REVISION, PREVIOUS)
                for name in reversed(activated):
                    rename(new[name], pending[name])
                for name in reversed(backup_names):
                    rename(old[name], name)
            obsolete = {old[name]["id"] for name in NAMES} if promoted else {
                a["id"] for a in api(f"releases/{release['id']}/assets") if a["name"] in pending.values()
            }
            for asset_id in obsolete:
                api(f"releases/assets/{asset_id}", "--method", "DELETE")
        final = api(f"releases/{release['id']}")
        if ({a["name"]: a["digest"] for a in final["assets"]} != expected
                or api(f"git/ref/tags/{TAG}")["object"]["sha"] != REVISION
                or final["body"].strip().replace("\r\n", "\n") != notes.read_text().strip()):
            raise RuntimeError("Não foi possível confirmar a republicação.")
        print(final["html_url"], expected[exe.name])


if __name__ == "__main__":
    main()
