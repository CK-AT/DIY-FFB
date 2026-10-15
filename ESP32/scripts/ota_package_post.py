import json
import os
import subprocess
import zipfile
from datetime import datetime
from pathlib import Path

Import("env")


def _compute_md5(path: Path) -> str:
    import hashlib

    md5 = hashlib.md5()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            md5.update(chunk)
    return md5.hexdigest()


def _load_board_versions(json_path: Path) -> dict[int, str]:
    try:
        data = json.loads(json_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    boards = data.get("boards")
    if not isinstance(boards, list):
        return {}
    mapping: dict[int, str] = {}
    for item in boards:
        if not isinstance(item, dict):
            continue
        pcb_version = item.get("pcb_version")
        name = item.get("name")
        if isinstance(pcb_version, int) and isinstance(name, str):
            mapping[pcb_version] = name
    return mapping


def _resolve_board_name(env, project_dir: Path) -> str:
    pcb_map = _load_board_versions(project_dir / "board_versions.json")
    defines = env.get("CPPDEFINES") or []
    pcb_version = None
    for define in defines:
        if isinstance(define, (list, tuple)) and len(define) == 2 and define[0] == "PCB_VERSION":
            try:
                pcb_version = int(define[1])
            except (TypeError, ValueError):
                pcb_version = None
            break
        if define == "PCB_VERSION":
            pcb_version = None
    if pcb_version in pcb_map:
        return pcb_map[pcb_version]
    return env.subst("$BOARD")


def _factory_matches(factory_path: Path, firmware_path: Path, env) -> bool:
    if not factory_path.exists():
        return False
    app_offset = int(env.subst("$ESP32_APP_OFFSET") or "0x10000", 0)
    firmware = firmware_path.read_bytes()
    with factory_path.open("rb") as handle:
        handle.seek(app_offset)
        return handle.read(len(firmware) + 1) == firmware


def _write_ffbota(source, target, env):
    if env.subst("$PIOENV") == "native":
        return

    project_dir = Path(env.subst("$PROJECT_DIR"))
    repo_root = project_dir.parent
    build_dir = Path(env.subst("$BUILD_DIR"))
    firmware_path = build_dir / "firmware.bin"
    if not firmware_path.exists():
        print(f"[ffbota] firmware not found: {firmware_path}")
        return

    version_path = project_dir / "version"
    version = version_path.read_text(encoding="utf-8").strip() if version_path.exists() else "0.0.0"
    build_env = env.subst("$PIOENV")
    board_id = _resolve_board_name(env, project_dir)

    ota_dir = repo_root / "OTA"
    ota_dir.mkdir(parents=True, exist_ok=True)
    output_path = ota_dir / f"firmware_{build_env}.ffbota"
    bin_name = f"firmware_{build_env}.bin"
    bin_path = ota_dir / bin_name

    md5_hex = _compute_md5(firmware_path)
    manifest = {
        "version": version,
        "board": board_id,
        "build_env": build_env,
        "build_timestamp": datetime.now().isoformat(),
        "md5": md5_hex,
        "size": firmware_path.stat().st_size,
        "firmware_name": firmware_path.name,
    }

    # The merged factory image (bootloader + partitions + otadata + app at 0x0)
    # is what the plugin's USB flasher writes (Plan 29). Only packaged when its
    # app part is this exact firmware.bin, so a stale merge is never shipped.
    factory_path = build_dir / "firmware.factory.bin"
    factory_ok = _factory_matches(factory_path, firmware_path, env)
    if factory_ok:
        manifest["chip"] = env.BoardConfig().get("build.mcu", "")
        manifest["factory_md5"] = _compute_md5(factory_path)
        manifest["factory_size"] = factory_path.stat().st_size
    elif factory_path.exists():
        print(f"[ffbota] {factory_path.name} does not contain this firmware.bin; package is OTA-only")

    with zipfile.ZipFile(output_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("manifest.json", json.dumps(manifest, indent=2, sort_keys=True))
        archive.write(firmware_path, "firmware.bin")
        if factory_ok:
            archive.write(factory_path, "firmware.factory.bin")

    print(f"[ffbota] wrote {output_path}")

    bin_path.write_bytes(firmware_path.read_bytes())
    print(f"[ffbota] wrote {bin_path}")

    update_info_path = ota_dir / "update_info.json"
    update_payload = {"Configurations": []}
    if update_info_path.exists():
        try:
            update_payload = json.loads(update_info_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            update_payload = {"Configurations": []}

    configs = update_payload.get("Configurations")
    if not isinstance(configs, list):
        configs = []

    try:
        branch_name = (
            subprocess.check_output(["git", "-C", str(repo_root), "rev-parse", "--abbrev-ref", "HEAD"], text=True)
            .strip()
        )
    except (subprocess.CalledProcessError, FileNotFoundError):
        branch_name = "unknown"

    url = (
        "https://raw.githubusercontent.com/CK-AT/DIY-FFB/"
        f"refs/heads/{branch_name}/OTA/{bin_name}"
    )
    entry = {"Board": board_id, "Version": version, "URL": url, "MD5": md5_hex}

    replaced = False
    for idx, cfg in enumerate(configs):
        if cfg.get("Board") == board_id:
            configs[idx] = entry
            replaced = True
            break
    if not replaced:
        configs.append(entry)

    update_payload["Configurations"] = configs
    update_info_path.write_text(json.dumps(update_payload, indent=2, sort_keys=True), encoding="utf-8")
    print(f"[ffbota] updated {update_info_path}")


env.AddPostAction("buildprog", _write_ffbota)
