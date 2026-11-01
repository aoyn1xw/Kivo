#!/usr/bin/env python3
"""Verify an exported signed iOS IPA before OTA publication."""

import datetime
import hashlib
import os
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path


def fail(message: str) -> int:
    print(f"::error::{message}")
    return 1


def run_cmd(cmd: list[str], what: str) -> tuple[int, bytes, str]:
    try:
        res = subprocess.run(cmd, capture_output=True, check=False)
    except OSError as exc:
        return 127, b"", f"Could not run {what} ({cmd[0]}): {exc}"
    return res.returncode, res.stdout, res.stderr.decode("utf-8", "replace").strip()


def parse_pubspec_version(pubspec_path: Path) -> tuple[str, str]:
    text = pubspec_path.read_text(encoding="utf-8")
    match = re.search(r"^version:\s*(\S+)", text, re.MULTILINE)
    if not match:
        raise ValueError(f"{pubspec_path} has no version line")
    raw = match.group(1)
    if "+" in raw:
        version, build = raw.split("+", 1)
    else:
        version, build = raw, "1"
    if not version or not build:
        raise ValueError(f"Malformed version line in {pubspec_path}: {raw!r}")
    return version, build


def parse_deployment_target(pbxproj_path: Path) -> str:
    text = pbxproj_path.read_text(encoding="utf-8")
    targets = sorted(
        set(re.findall(r"IPHONEOS_DEPLOYMENT_TARGET\s*=\s*([0-9.]+)\s*;", text))
    )
    if len(targets) != 1:
        raise ValueError(
            f"Runner deployment target in {pbxproj_path} is inconsistent: {targets}"
        )
    return targets[0]


def extract_cms_plist(mobileprovision_path: Path) -> dict:
    rc, stdout, err = run_cmd(
        ["security", "cms", "-D", "-i", str(mobileprovision_path)],
        "security cms",
    )
    if rc != 0 or not stdout.strip():
        raise ValueError(
            f"security cms failed to decode {mobileprovision_path} (exit {rc}): {err}"
        )
    data = plistlib.loads(stdout)
    if not isinstance(data, dict):
        raise ValueError("Decoded provisioning profile is not a plist dictionary")
    return data


def verify_profile_expiry(
    profile: dict, now: datetime.datetime | None = None
) -> datetime.datetime:
    expires = profile.get("ExpirationDate")
    if expires is None:
        raise ValueError("Provisioning profile has no ExpirationDate entry")
    if not isinstance(expires, datetime.datetime):
        raise ValueError(
            f"Provisioning profile ExpirationDate is not a date: {expires!r}"
        )
    if expires.tzinfo is None:
        expires = expires.replace(tzinfo=datetime.timezone.utc)
    ref_now = now or datetime.datetime.now(datetime.timezone.utc)
    if expires <= ref_now:
        raise ValueError(f"Provisioning profile expired at {expires.isoformat()}")
    return expires


def extract_primary_icon_Metadata(info: dict) -> tuple[str | None, list[str]]:
    icons = info.get("CFBundleIcons")
    if not isinstance(icons, dict):
        icons = info.get("CFBundleIcons~ipad")
    if not isinstance(icons, dict):
        return None, []
    primary = icons.get("CFBundlePrimaryIcon")
    if not isinstance(primary, dict):
        return None, []
    icon_name = primary.get("CFBundleIconName")
    icon_files = primary.get("CFBundleIconFiles")
    files = (
        [f for f in icon_files if isinstance(f, str) and f]
        if isinstance(icon_files, list)
        else []
    )
    return (icon_name if isinstance(icon_name, str) and icon_name else None), files


def verify_app_icons(app_path: Path, info: dict) -> str:
    icon_name, icon_files = extract_primary_icon_Metadata(info)
    candidates = list(dict.fromkeys(([icon_name] if icon_name else []) + icon_files))
    if not candidates:
        raise ValueError(
            "Info.plist is missing CFBundleIcons.CFBundlePrimaryIcon metadata "
            "(neither CFBundleIconName nor CFBundleIconFiles is set)"
        )

    car_path = app_path / "Assets.car"
    if car_path.is_file():
        rc, stdout, err = run_cmd(
            ["xcrun", "assetutil", "--info", str(car_path)],
            "xcrun assetutil",
        )
        if rc != 0 or not stdout.strip():
            raise ValueError(
                f"xcrun assetutil could not read {car_path} (exit {rc}): {err}"
            )
        text = stdout.decode("utf-8", "replace")
        for candidate in candidates:
            if re.search(rf'"Name"\s*:\s*"{re.escape(candidate)}"', text):
                return f"compiled Assets.car contains primary icon {candidate!r}"
        raise ValueError(
            f"Compiled {car_path} does not contain any primary icon from Info.plist "
            f"({candidates!r})"
        )

    loose_pngs = [p.name for p in app_path.glob("*.png")]
    matched_pngs = [
        name
        for name in loose_pngs
        if any(name.startswith(prefix) for prefix in candidates)
    ]
    if matched_pngs:
        return f"{len(matched_pngs)} loose primary icon PNG(s) matching {candidates!r}"

    raise ValueError(
        f"App bundle has neither Assets.car nor loose PNGs matching primary icon "
        f"{candidates!r}"
    )


def verify_codesign_identity(
    target_path: Path, expected_team: str, require_distribution: bool = False
) -> None:
    rc, _, err = run_cmd(
        ["codesign", "--verify", "--strict", str(target_path)],
        "codesign --verify",
    )
    if rc != 0:
        raise ValueError(f"Code signature verification failed for {target_path}: {err}")

    rc, stdout, err = run_cmd(
        ["codesign", "-dvvv", str(target_path)],
        "codesign -dvvv",
    )
    if rc != 0:
        raise ValueError(f"Could not inspect signature on {target_path}: {err}")

    details = f"{stdout.decode('utf-8', 'replace')}\n{err}"
    team_match = re.search(r"^TeamIdentifier=(.+)$", details, re.MULTILINE)
    signed_team = team_match.group(1).strip() if team_match else ""
    if not signed_team or signed_team == "not set" or signed_team != expected_team:
        raise ValueError(
            f"Signature TeamIdentifier on {target_path} is {signed_team!r}, "
            f"expected {expected_team!r}"
        )

    if require_distribution and "Authority=Apple Distribution:" not in details:
        raise ValueError(
            f"{target_path} was not signed with an Apple Distribution certificate"
        )


def verify_entitlements(
    app_path: Path, expected_team: str, expected_app_id: str, profile: dict
) -> dict:
    rc, stdout, err = run_cmd(
        ["codesign", "-d", "--entitlements", "-", "--xml", str(app_path)],
        "codesign --entitlements",
    )
    if rc != 0:
        raise ValueError(
            f"codesign failed to extract entitlements from {app_path} "
            f"(exit {rc}): {err}"
        )
    if not stdout.strip():
        raise ValueError(f"The signature on {app_path} contains no entitlement data")
    try:
        entitlements = plistlib.loads(stdout)
    except Exception as exc:
        raise ValueError(
            f"Signed entitlements are not a parseable XML plist: {exc}"
        ) from exc
    if not isinstance(entitlements, dict):
        raise ValueError("Signed entitlements plist is not a dictionary")

    app_id = entitlements.get("application-identifier")
    if app_id != expected_app_id:
        raise ValueError(
            f"Signed application-identifier {app_id!r} does not match "
            f"expected {expected_app_id!r}"
        )

    team_id = entitlements.get("com.apple.developer.team-identifier")
    if team_id != expected_team:
        raise ValueError(
            f"Signed com.apple.developer.team-identifier {team_id!r} does not "
            f"match expected team {expected_team!r}"
        )

    profile_ents = profile.get("Entitlements")
    allowed_groups = (
        [
            g
            for g in profile_ents.get("keychain-access-groups", [])
            if isinstance(g, str)
        ]
        if isinstance(profile_ents, dict)
        else []
    )

    groups = entitlements.get("keychain-access-groups")
    if groups is not None:
        if not isinstance(groups, list) or not all(isinstance(g, str) for g in groups):
            raise ValueError(f"Signed keychain-access-groups is malformed: {groups!r}")
        for group in groups:
            if not group.startswith(f"{expected_team}."):
                raise ValueError(
                    f"Keychain access group {group!r} is not prefixed with "
                    f"signing team {expected_team!r}"
                )
            permitted = any(
                entry == group or (entry.endswith("*") and group.startswith(entry[:-1]))
                for entry in allowed_groups
            )
            if not permitted:
                raise ValueError(
                    f"Keychain access group {group!r} is not permitted by the "
                    f"embedded provisioning profile (allowed: {allowed_groups!r})"
                )

    return entitlements


def iter_nested_code(app_path: Path) -> list[Path]:
    suffixes = (".framework", ".appex", ".dylib", ".app")
    found: list[Path] = []
    for root, dirs, files in os.walk(app_path):
        root_path = Path(root)
        dirs.sort()
        for d in dirs:
            if d.endswith(suffixes):
                found.append(root_path / d)
        for f in sorted(files):
            if f.endswith(suffixes):
                found.append(root_path / f)
    return sorted(found)


def verify_ipa(
    ipa_path: Path,
    expected_bundle_id: str,
    expected_team_id: str,
    expected_profile_uuid: str,
    pubspec_path: Path,
    pbxproj_path: Path,
) -> dict[str, str]:
    if not ipa_path.is_file():
        raise ValueError(f"IPA file does not exist: {ipa_path}")

    expected_version, expected_build = parse_pubspec_version(pubspec_path)
    expected_min_os = parse_deployment_target(pbxproj_path)
    expected_full_app_id = f"{expected_team_id}.{expected_bundle_id}"

    with tempfile.TemporaryDirectory(prefix="ipa-verify-") as tmp_dir:
        tmp_path = Path(tmp_dir)
        try:
            with zipfile.ZipFile(ipa_path, "r") as zf:
                zf.extractall(tmp_path)
                for member in zf.infolist():
                    mode = (member.external_attr >> 16) & 0o777
                    if mode:
                        target = tmp_path / member.filename
                        if target.exists():
                            os.chmod(target, mode)
        except Exception as exc:
            raise ValueError(f"Could not extract IPA {ipa_path}: {exc}") from exc

        payload_dir = tmp_path / "Payload"
        apps = sorted(payload_dir.glob("*.app")) if payload_dir.is_dir() else []
        if len(apps) != 1:
            raise ValueError(
                f"IPA must contain exactly one Payload/*.app bundle (found {len(apps)})"
            )
        app_path = apps[0]

        info_path = app_path / "Info.plist"
        if not info_path.is_file():
            raise ValueError(f"App bundle is missing Info.plist: {info_path}")
        with open(info_path, "rb") as f:
            info = plistlib.load(f)
        if not isinstance(info, dict):
            raise ValueError("App Info.plist is not a dictionary")

        bundle_id = info.get("CFBundleIdentifier")
        display_name = info.get("CFBundleDisplayName")
        bundle_name = info.get("CFBundleName")
        executable = info.get("CFBundleExecutable")
        app_version = info.get("CFBundleShortVersionString")
        app_build = info.get("CFBundleVersion")
        min_os = info.get("MinimumOSVersion")

        if bundle_id != expected_bundle_id:
            raise ValueError(
                f"IPA CFBundleIdentifier {bundle_id!r} does not match "
                f"expected {expected_bundle_id!r}"
            )
        if display_name != "Kivo" or bundle_name != "Kivo":
            raise ValueError(
                f"IPA display/bundle name must be 'Kivo' "
                f"(got display={display_name!r}, name={bundle_name!r})"
            )
        if not isinstance(executable, str) or not executable:
            raise ValueError("IPA Info.plist has no CFBundleExecutable")
        exec_path = app_path / executable
        if not exec_path.is_file():
            raise ValueError(f"IPA main executable is missing: {exec_path}")
        if os.name != "nt" and not os.access(exec_path, os.X_OK):
            raise ValueError(f"IPA main executable is not executable: {exec_path}")

        if app_version != expected_version or app_build != expected_build:
            raise ValueError(
                f"IPA version/build ({app_version}+{app_build}) does not match "
                f"pubspec.yaml ({expected_version}+{expected_build})"
            )
        if min_os != expected_min_os:
            raise ValueError(
                f"IPA MinimumOSVersion {min_os!r} does not match deployment "
                f"target {expected_min_os!r}"
            )

        icon_summary = verify_app_icons(app_path, info)

        profile_path = app_path / "embedded.mobileprovision"
        if not profile_path.is_file():
            raise ValueError("IPA has no embedded.mobileprovision")
        profile = extract_cms_plist(profile_path)

        embedded_uuid = profile.get("UUID")
        if embedded_uuid != expected_profile_uuid:
            raise ValueError(
                f"Embedded profile UUID {embedded_uuid!r} does not match "
                f"expected {expected_profile_uuid!r}"
            )

        profile_teams = profile.get("TeamIdentifier")
        embedded_team = (
            profile_teams[0]
            if isinstance(profile_teams, list) and profile_teams
            else None
        )
        if embedded_team != expected_team_id:
            raise ValueError(
                f"Embedded profile team {embedded_team!r} does not match "
                f"expected {expected_team_id!r}"
            )

        profile_ents = profile.get("Entitlements")
        embedded_app_id = (
            profile_ents.get("application-identifier")
            if isinstance(profile_ents, dict)
            else None
        )
        if embedded_app_id != expected_full_app_id:
            raise ValueError(
                f"Embedded profile application-identifier {embedded_app_id!r} "
                f"does not match expected {expected_full_app_id!r}"
            )

        devices = profile.get("ProvisionedDevices")
        if not isinstance(devices, list) or not devices:
            raise ValueError(
                "Embedded provisioning profile has no ProvisionedDevices (not Ad Hoc)"
            )

        expires = verify_profile_expiry(profile)
        verify_codesign_identity(app_path, expected_team_id, require_distribution=True)
        verify_entitlements(app_path, expected_team_id, expected_full_app_id, profile)

        nested_items = iter_nested_code(app_path)
        for nested in nested_items:
            verify_codesign_identity(
                nested, expected_team_id, require_distribution=False
            )

    sha256 = hashlib.sha256(ipa_path.read_bytes()).hexdigest()
    print(
        f"Verified IPA {ipa_path.name}: bundle={bundle_id} "
        f"version={app_version} ({app_build}) min_os={min_os} "
        f"icons=({icon_summary}) expires={expires.isoformat()} "
        f"nested={len(nested_items)} sha256={sha256}"
    )
    return {
        "IPA_APP_VERSION": str(app_version),
        "IPA_BUILD_NUMBER": str(app_build),
        "IPA_MIN_OS_VERSION": str(min_os),
        "IPA_SHA256": sha256,
    }


def main(argv: list[str]) -> int:
    if len(argv) != 7:
        return fail(
            "Usage: verify_ipa.py <ipa-path> <expected-bundle-id> "
            "<expected-team-id> <expected-profile-uuid> <pubspec.yaml> "
            "<project.pbxproj>"
        )
    try:
        outputs = verify_ipa(
            ipa_path=Path(argv[1]),
            expected_bundle_id=argv[2],
            expected_team_id=argv[3],
            expected_profile_uuid=argv[4],
            pubspec_path=Path(argv[5]),
            pbxproj_path=Path(argv[6]),
        )
    except Exception as exc:
        return fail(str(exc))

    github_env = os.environ.get("GITHUB_ENV")
    if github_env:
        with open(github_env, "a", encoding="utf-8") as f:
            for key, value in outputs.items():
                f.write(f"{key}={value}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
