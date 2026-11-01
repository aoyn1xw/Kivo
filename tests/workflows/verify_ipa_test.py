"""Focused regression tests for .github/scripts/verify_ipa.py."""

import datetime
import importlib.util
import json
import os
import plistlib
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[2] / ".github" / "scripts" / "verify_ipa.py"
spec = importlib.util.spec_from_file_location("verify_ipa", SCRIPT)
verify_ipa = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify_ipa)

TEAM = "ABCDE12345"
BUNDLE_ID = "app.winter6686.date9988"
FULL_APP_ID = f"{TEAM}.{BUNDLE_ID}"
PROFILE_UUID = "11111111-2222-3333-4444-555555555555"


def make_info_plist(**overrides) -> dict:
    info = {
        "CFBundleIdentifier": BUNDLE_ID,
        "CFBundleDisplayName": "Kivo",
        "CFBundleName": "Kivo",
        "CFBundleExecutable": "Runner",
        "CFBundleShortVersionString": "0.1.0",
        "CFBundleVersion": "1",
        "MinimumOSVersion": "18.0",
        "CFBundleIcons": {
            "CFBundlePrimaryIcon": {
                "CFBundleIconFiles": ["AppIcon60x60"],
                "CFBundleIconName": "AppIcon",
            }
        },
    }
    info.update(overrides)
    return info


def make_profile_plist(_drop=(), **overrides) -> dict:
    profile = {
        "UUID": PROFILE_UUID,
        "Name": "Kivo Ad Hoc",
        "TeamIdentifier": [TEAM],
        "ExpirationDate": datetime.datetime(
            2099, 1, 1, 0, 0, 0, tzinfo=datetime.timezone.utc
        ),
        "ProvisionedDevices": ["00008110-0011223344556677"],
        "Entitlements": {
            "application-identifier": FULL_APP_ID,
            "com.apple.developer.team-identifier": TEAM,
            "keychain-access-groups": [f"{TEAM}.*"],
        },
    }
    profile.update(overrides)
    for key in _drop:
        profile.pop(key, None)
    return profile


def make_entitlements_plist(**overrides) -> dict:
    ents = {
        "application-identifier": FULL_APP_ID,
        "com.apple.developer.team-identifier": TEAM,
        "get-task-allow": False,
        "keychain-access-groups": [f"{TEAM}.{BUNDLE_ID}"],
    }
    ents.update(overrides)
    return ents


class VerifyIpaTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.pubspec = self.tmp / "pubspec.yaml"
        self.pubspec.write_text("name: kivo\nversion: 0.1.0+1\n", encoding="utf-8")
        self.pbxproj = self.tmp / "project.pbxproj"
        self.pbxproj.write_text(
            "IPHONEOS_DEPLOYMENT_TARGET = 18.0;\nIPHONEOS_DEPLOYMENT_TARGET = 18.0;\n",
            encoding="utf-8",
        )
        self.profile_dict = make_profile_plist()
        self.entitlements_bytes = plistlib.dumps(make_entitlements_plist())
        self.assetutil_payload = json.dumps(
            [
                {"AssetStorageVersion": "Xcode 16.0"},
                {"Name": "AppIcon", "AssetType": "MultiSizeImage"},
            ]
        ).encode("utf-8")
        self.nested_teams: dict[str, str] = {}
        self.broken_signatures: set[str] = set()
        self.outer_authority = f"Authority=Apple Distribution: Test ({TEAM})"
        self.seen_commands: list[list[str]] = []

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def build_ipa(
        self,
        *,
        info: dict | None = None,
        with_car: bool = True,
        loose_icons: tuple[str, ...] = (),
        nested_paths: tuple[str, ...] = ("Frameworks/Flutter.framework/Flutter",),
    ) -> Path:
        ipa_path = self.tmp / "Kivo.ipa"
        info_data = plistlib.dumps(info if info is not None else make_info_plist())
        with zipfile.ZipFile(ipa_path, "w") as zf:
            zf.writestr("Payload/Runner.app/Info.plist", info_data)

            exec_info = zipfile.ZipInfo("Payload/Runner.app/Runner")
            exec_info.external_attr = 0o755 << 16
            zf.writestr(exec_info, b"\xcf\xfa\xed\xfe")

            zf.writestr("Payload/Runner.app/embedded.mobileprovision", b"cms-profile")
            if with_car:
                zf.writestr("Payload/Runner.app/Assets.car", b"compiled-car")
            for icon in loose_icons:
                zf.writestr(f"Payload/Runner.app/{icon}", b"png")
            for rel in nested_paths:
                zf.writestr(f"Payload/Runner.app/{rel}", b"macho")
        return ipa_path

    def fake_subprocess_run(self, cmd, capture_output=True, check=False, **kwargs):
        self.seen_commands.append(list(cmd))
        exe = cmd[0]
        if exe == "security" and cmd[1:3] == ["cms", "-D"]:
            return subprocess.CompletedProcess(
                cmd, 0, plistlib.dumps(self.profile_dict), b""
            )
        if exe == "xcrun" and cmd[1:3] == ["assetutil", "--info"]:
            if self.assetutil_payload is None:
                return subprocess.CompletedProcess(cmd, 1, b"", b"corrupt car")
            return subprocess.CompletedProcess(cmd, 0, self.assetutil_payload, b"")
        if exe == "codesign":
            target = cmd[-1]
            name = Path(target).name
            if "--verify" in cmd:
                if name in self.broken_signatures:
                    return subprocess.CompletedProcess(
                        cmd, 1, b"", b"invalid signature"
                    )
                return subprocess.CompletedProcess(cmd, 0, b"", b"")
            if "-dvvv" in cmd:
                team = self.nested_teams.get(name, TEAM)
                stderr = (
                    f"Executable={target}\n"
                    f"Identifier={BUNDLE_ID}\n"
                    f"{self.outer_authority}\n"
                    f"TeamIdentifier={team}\n"
                ).encode("utf-8")
                return subprocess.CompletedProcess(cmd, 0, b"", stderr)
            if "--entitlements" in cmd:
                return subprocess.CompletedProcess(cmd, 0, self.entitlements_bytes, b"")
        return subprocess.CompletedProcess(cmd, 1, b"", b"unexpected command")

    def run_verify(self, ipa_path: Path) -> dict[str, str]:
        with mock.patch.object(
            verify_ipa.subprocess, "run", side_effect=self.fake_subprocess_run
        ):
            return verify_ipa.verify_ipa(
                ipa_path=ipa_path,
                expected_bundle_id=BUNDLE_ID,
                expected_team_id=TEAM,
                expected_profile_uuid=PROFILE_UUID,
                pubspec_path=self.pubspec,
                pbxproj_path=self.pbxproj,
            )

    def test_valid_ipa_passes_and_pins_xml_entitlements(self) -> None:
        ipa = self.build_ipa(
            nested_paths=(
                "Frameworks/Flutter.framework/Flutter",
                "Frameworks/libswiftCore.dylib",
                "PlugIns/Share.appex/Share",
            )
        )
        out = self.run_verify(ipa)
        self.assertEqual(out["IPA_APP_VERSION"], "0.1.0")
        self.assertEqual(out["IPA_BUILD_NUMBER"], "1")
        self.assertEqual(out["IPA_MIN_OS_VERSION"], "18.0")
        self.assertEqual(len(out["IPA_SHA256"]), 64)
        ent_cmds = [c for c in self.seen_commands if "--entitlements" in c]
        self.assertEqual(len(ent_cmds), 1)
        self.assertIn("--xml", ent_cmds[0])
        self.assertIn("-", ent_cmds[0])

    def test_entitlements_checks_apple_team_key_and_keychain_groups(self) -> None:
        ipa = self.build_ipa()
        # Wrong com.apple.developer.team-identifier fails
        self.entitlements_bytes = plistlib.dumps(
            make_entitlements_plist(
                **{"com.apple.developer.team-identifier": "WRONGTEAM"}
            )
        )
        with self.assertRaisesRegex(ValueError, "com.apple.developer.team-identifier"):
            self.run_verify(ipa)

        # DER / unparseable or empty entitlement output fails closed
        self.entitlements_bytes = b"\xfa\xde\x71\x71\x00\x01"
        with self.assertRaisesRegex(ValueError, "not a parseable XML plist"):
            self.run_verify(ipa)

        self.entitlements_bytes = b""
        with self.assertRaisesRegex(ValueError, "no entitlement data"):
            self.run_verify(ipa)

        # Disallowed keychain group fails
        self.profile_dict = make_profile_plist(
            Entitlements={
                "application-identifier": FULL_APP_ID,
                "com.apple.developer.team-identifier": TEAM,
                "keychain-access-groups": [f"{TEAM}.{BUNDLE_ID}"],
            }
        )
        self.entitlements_bytes = plistlib.dumps(
            make_entitlements_plist(
                **{"keychain-access-groups": [f"{TEAM}.other.group"]}
            )
        )
        with self.assertRaisesRegex(ValueError, "not permitted"):
            self.run_verify(ipa)

    def test_profile_expiry_handles_naive_utc_expired_and_missing(self) -> None:
        ipa = self.build_ipa()
        # Naive future date (plistlib default) is normalized to UTC and passes
        self.profile_dict = make_profile_plist(
            ExpirationDate=datetime.datetime(2099, 6, 1, 12, 0, 0)
        )
        self.run_verify(ipa)

        # Naive past date fails
        self.profile_dict = make_profile_plist(
            ExpirationDate=datetime.datetime(2020, 1, 1, 0, 0, 0)
        )
        with self.assertRaisesRegex(ValueError, "expired"):
            self.run_verify(ipa)

        # Missing ExpirationDate fails
        self.profile_dict = make_profile_plist(_drop=("ExpirationDate",))
        with self.assertRaisesRegex(ValueError, "no ExpirationDate"):
            self.run_verify(ipa)

    def test_primary_icon_validation_supports_car_and_loose_pngs(self) -> None:
        # Loose PNG matching CFBundlePrimaryIcon passes without Assets.car
        ipa_loose = self.build_ipa(with_car=False, loose_icons=("AppIcon60x60@2x.png",))
        self.run_verify(ipa_loose)

        # Assets.car missing primary icon name fails even if unrelated 'icon' exists
        ipa_car = self.build_ipa(with_car=True)
        self.assetutil_payload = json.dumps(
            [{"Name": "SomeOtherIcon", "AssetType": "Image"}]
        ).encode("utf-8")
        with self.assertRaisesRegex(ValueError, "does not contain any primary icon"):
            self.run_verify(ipa_car)

        # Missing CFBundlePrimaryIcon in Info.plist fails
        ipa_no_meta = self.build_ipa(info=make_info_plist(CFBundleIcons={}))
        with self.assertRaisesRegex(ValueError, "CFBundlePrimaryIcon"):
            self.run_verify(ipa_no_meta)

    def test_nested_code_verification_catches_broken_and_foreign_team(self) -> None:
        ipa = self.build_ipa(
            nested_paths=(
                "Frameworks/Flutter.framework/Flutter",
                "PlugIns/Share.appex/Share",
                "PlugIns/Share.appex/Frameworks/Inner.framework/Inner",
            )
        )
        # All nested bundles (including Inner.framework inside Share.appex) are verified when valid
        self.run_verify(ipa)
        verified_names = {
            Path(c[-1]).name
            for c in self.seen_commands
            if c[:2] == ["codesign", "--verify"]
        }
        self.assertIn("Share.appex", verified_names)
        self.assertIn("Inner.framework", verified_names)

        # Foreign team on Inner.framework inside PlugIns/Share.appex/Frameworks/Inner.framework fails
        self.nested_teams["Inner.framework"] = "FOREIGN999"
        with self.assertRaisesRegex(
            ValueError,
            r"Inner\.framework.*TeamIdentifier|TeamIdentifier.*Inner\.framework",
        ):
            self.run_verify(ipa)

        # Invalid signature on Inner.framework inside PlugIns/Share.appex/Frameworks/Inner.framework fails
        self.nested_teams.clear()
        self.broken_signatures.add("Inner.framework")
        with self.assertRaisesRegex(
            ValueError,
            r"Code signature verification failed for .*Inner\.framework",
        ):
            self.run_verify(ipa)


if __name__ == "__main__":
    unittest.main()
