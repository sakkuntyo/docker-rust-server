import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from map_state import MapError, Season, digest

HELPER = Path(__file__).resolve().parents[1] / "map_state.py"


class SeasonTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="rust-map-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "server"
        self.season = Season(self.root)

    def boot(self):
        self.season.prepare(6000, 1234, 287)
        self.season.file(287, "map").write_bytes(b"original terrain")
        self.season.capture_map(self.season.file(287, "map"))
        self.season.file(287, "sav").write_bytes(b"SAVRlatest buildings")
        self.season.state["ready"] = True
        self.season.store()
        return self.season

    def log(self, checksum=None):
        checksum = checksum or self.season.state["sha256"]
        return ("[Rust.MapCache] Map uploaded to backend: "
                f"https://files.facepunch.com/rust/maps/{checksum}/proceduralmap.6000.0.287_example.map")

    def test_first_map_is_immutable(self):
        season = self.boot()
        season.file(287, "map").write_bytes(b"different terrain")
        with self.assertRaises(MapError):
            season.capture_map(season.file(287, "map"))
        self.assertEqual((season.archive / "original.map").read_bytes(), b"original terrain")

    def test_recreation_restores_terrain_and_keeps_latest_save(self):
        self.boot()
        self.season.file(287, "map").write_bytes(b"overwritten terrain")
        latest = b"SAVRnewer buildings after first backup"
        self.season.file(287, "sav").write_bytes(latest)
        recreated = Season(self.root)
        recreated.prepare(6000, 1234, 287)
        self.assertEqual(recreated.file(287, "map").read_bytes(), b"original terrain")
        self.assertEqual(recreated.file(287, "sav").read_bytes(), latest)
        self.assertEqual((recreated.archive / "before-start.sav").read_bytes(), latest)
        self.assertEqual(len(list(recreated.archive.glob("replaced-*.map"))), 1)

    def test_multiple_protocol_updates_use_latest_save(self):
        season = self.boot()
        season.prepare(6000, 1234, 288)
        season.file(288, "sav").write_bytes(b"SAVRlatest protocol 288")
        season.state["ready"] = True
        season.store()
        season = Season(self.root)
        season.prepare(6000, 1234, 305)
        self.assertEqual(season.file(305, "map").read_bytes(), b"original terrain")
        self.assertEqual(season.file(305, "sav").read_bytes(), b"SAVRlatest protocol 288")
        self.assertFalse(season.file(305, "sav").is_symlink())
        self.assertFalse(season.file(306, "sav").exists())
        self.assertEqual(len(list(season.archive.glob("before-protocol-*"))), 2)

    def test_url_persists_with_metadata(self):
        season = self.boot()
        season.record_url(self.log())
        recreated = Season(self.root)
        event = json.loads((recreated.history / "map-urls.jsonl").read_text())
        self.assertEqual(event["seed"], 1234)
        self.assertEqual(event["size"], 6000)
        self.assertEqual(event["season"], season.state["id"])
        self.assertTrue(event["verified"])
        self.assertEqual(event["url"], recreated.state["original_url"])
        self.assertIn("recorded_at", event)

    def test_url_hash_mismatch_is_recorded_but_not_adopted(self):
        season = self.boot()
        with self.assertRaises(MapError):
            season.record_url(self.log("a" * 64))
        self.assertNotIn("original_url", season.state)
        self.assertFalse(json.loads((season.history / "map-urls.jsonl").read_text())["verified"])

    def test_image_and_untrusted_urls_are_ignored(self):
        season = self.boot()
        season.record_url(self.log().replace("MapCache]", "MapCache-Images]"))
        season.record_url(self.log().replace("files.facepunch.com", "example.com"))
        self.assertFalse((season.history / "map-urls.jsonl").exists())

    def test_full_wipe_keeps_history_even_when_seed_reused(self):
        season = self.boot()
        season.record_url(self.log())
        original_archive = season.archive
        (season.identity / "player.blueprints.5.db").write_bytes(b"blueprints")
        season.wipe("FULL")
        self.assertFalse(season.identity.exists())
        self.assertTrue((original_archive / "original.map").exists())
        self.assertTrue((season.history / "map-urls.jsonl").exists())
        season.prepare(6000, 1234, 288)
        self.assertNotEqual(season.archive, original_archive)
        self.assertFalse(season.file(288, "map").exists())
        self.assertFalse(season.file(288, "sav").exists())

    def test_map_wipe_retains_databases_and_history(self):
        season = self.boot()
        database = season.identity / "player.blueprints.5.db"
        database.write_bytes(b"latest blueprints")
        season.file(288, "sav").symlink_to(season.file(287, "sav").name)
        season.file(287, "sav.1").write_bytes(b"backup")
        season.wipe("MAP")
        self.assertEqual(database.read_bytes(), b"latest blueprints")
        self.assertFalse(list(season.identity.glob("*.sav*")))
        self.assertFalse(list(season.identity.glob("*.map")))
        self.assertTrue(list(season.history.glob("*/original.map")))

    def test_adoption_requires_explicit_original(self):
        self.season.identity.mkdir()
        (self.season.identity / "proceduralmap.6000.1234.287.sav").write_bytes(b"SAVRlatest")
        with self.assertRaisesRegex(MapError, "ADOPT"):
            self.season.prepare(6000, 1234, 288)
        self.assertFalse(self.season.active.exists())

    def legacy_world(self):
        identity = self.season.identity
        identity.mkdir()
        save = identity / "proceduralmap.6000.1234.287.sav"
        save.write_bytes(b"SAVRlatest through legacy alias")
        for number in range(288, 298):
            (identity / f"proceduralmap.6000.1234.{number}.sav").symlink_to(save.name)
        original = identity / "proceduralmap.6000.1234.288.map"
        original.write_bytes(b"verified recovered old terrain")
        return save, original

    def test_adopt_recovered_legacy_world_then_upgrade(self):
        save, original = self.legacy_world()
        self.season.prepare(6000, 1234, 288, str(original), digest(original))
        self.assertFalse(self.season.file(288, "sav").is_symlink())
        self.assertEqual(self.season.file(288, "sav").read_bytes(), save.read_bytes())
        self.season.file(288, "sav").write_bytes(b"SAVRnewest save")
        self.season.state["ready"] = True
        self.season.store()
        self.season.prepare(6000, 1234, 289)
        self.assertEqual(self.season.file(289, "sav").read_bytes(), b"SAVRnewest save")
        self.assertFalse(self.season.file(289, "sav").is_symlink())
        self.assertEqual(save.read_bytes(), b"SAVRlatest through legacy alias")

    def test_wrong_adoption_hash_leaves_existing_files_unchanged(self):
        save, original = self.legacy_world()
        before = save.read_bytes(), original.read_bytes()
        with self.assertRaisesRegex(MapError, "SHA-256"):
            self.season.prepare(6000, 1234, 288, str(original), "f" * 64)
        self.assertEqual(before, (save.read_bytes(), original.read_bytes()))
        self.assertFalse(self.season.active.exists())

    def test_ambiguous_adoption_stops(self):
        _, original = self.legacy_world()
        (self.season.identity / "proceduralmap.6000.1234.286.sav").write_bytes(b"SAVRother save")
        with self.assertRaisesRegex(MapError, "one current save"):
            self.season.prepare(6000, 1234, 288, str(original), digest(original))

    def test_missing_latest_save_stops(self):
        season = self.boot()
        season.file(287, "sav").unlink()
        with self.assertRaisesRegex(MapError, "save is missing"):
            season.prepare(6000, 1234, 288)
        self.assertFalse(season.file(288, "map").exists())

    def test_invalid_save_header_stops(self):
        season = self.boot()
        season.file(287, "sav").write_bytes(b"broken save")
        with self.assertRaisesRegex(MapError, "header"):
            season.prepare(6000, 1234, 288)

    def test_damaged_archive_never_overwrites_map(self):
        season = self.boot()
        (season.archive / "original.map").write_bytes(b"damaged")
        before = season.file(287, "map").read_bytes()
        with self.assertRaisesRegex(MapError, "damaged"):
            season.prepare(6000, 1234, 287)
        self.assertEqual(season.file(287, "map").read_bytes(), before)

    def test_changed_seed_without_wipe_stops(self):
        season = self.boot()
        with self.assertRaisesRegex(MapError, "intentional wipe"):
            season.prepare(6000, 5678, 288)

    def test_conflicting_target_save_stops(self):
        season = self.boot()
        season.file(288, "sav").write_bytes(b"SAVRother world")
        with self.assertRaisesRegex(MapError, "different save"):
            season.prepare(6000, 1234, 288)
        self.assertEqual(season.file(288, "sav").read_bytes(), b"SAVRother world")

    def test_save_link_cannot_escape_volume(self):
        season = self.boot()
        outside = self.root.parent / "outside.sav"
        outside.write_bytes(b"SAVRouter file")
        season.file(287, "sav").unlink()
        season.file(287, "sav").symlink_to(outside)
        with self.assertRaisesRegex(MapError, "escapes"):
            season.prepare(6000, 1234, 288)
        self.assertEqual(outside.read_bytes(), b"SAVRouter file")

    def test_history_symlink_rejected(self):
        self.season.history.rmdir()
        self.season.history.symlink_to(self.root.parent, target_is_directory=True)
        with self.assertRaisesRegex(MapError, "Unsafe"):
            Season(self.root)

    def invoke_runner(self, code, timeout=5):
        return subprocess.run([sys.executable, str(HELPER), "--root", str(self.root),
                               "run", "--timeout", str(timeout), "--", sys.executable, "-u", "-c", code],
                              capture_output=True, timeout=timeout + 5)

    def test_runner_records_url_and_preserves_console(self):
        season = self.boot()
        season.prepare(6000, 1234, 287)
        log = self.log()
        result = self.invoke_runner(f"print({log!r}); print('Spawning 123 entities from save'); print('Server startup complete')")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(log.encode(), result.stdout)
        self.assertTrue(Season(self.root).state["ready"])
        self.assertTrue((season.history / "map-urls.jsonl").exists())

    def test_startup_succeeds_without_network_upload(self):
        season = self.boot()
        season.prepare(6000, 1234, 287)
        result = self.invoke_runner("print('Spawning 1 entities from save'); print('Server startup complete')")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(Season(self.root).state["ready"])

    def test_first_boot_captures_map_even_when_upload_fails(self):
        season = self.season
        season.prepare(6000, 1234, 287)
        season.file(287, "map").write_bytes(b"new terrain")
        result = self.invoke_runner("print('[Rust.MapCache] Unable to upload map file!'); print('Server startup complete')")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((season.archive / "original.map").read_bytes(), b"new terrain")

    def test_incompatible_save_kills_before_overwrite_and_blocks_retry(self):
        season = self.boot()
        season.prepare(6000, 1234, 288)
        marker = season.file(288, "sav")
        code = ("import signal,time; from pathlib import Path; "
                f"signal.signal(signal.SIGTERM, lambda *_: Path({str(marker)!r}).write_bytes(b'bad save')); "
                "print('Invalid save (missing header)'); time.sleep(3)")
        result = self.invoke_runner(code)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(marker.read_bytes(), b"SAVRlatest buildings")
        self.assertIn("failure", Season(self.root).state)
        with self.assertRaisesRegex(MapError, "Previous startup failed"):
            Season(self.root).prepare(6000, 1234, 288)

    def test_missing_save_fallback_is_rejected(self):
        season = self.boot()
        season.prepare(6000, 1234, 288)
        result = self.invoke_runner("print(\"Couldn't load server/world.sav - file doesn't exist\"); import time; time.sleep(3)")
        self.assertEqual(result.returncode, 1)
        self.assertIn(b"expected save", result.stderr)

    def test_startup_without_save_confirmation_is_rejected(self):
        self.boot().prepare(6000, 1234, 288)
        result = self.invoke_runner("print('Server startup complete')")
        self.assertEqual(result.returncode, 1)
        self.assertIn(b"saved entities", result.stderr)

    def test_startup_timeout(self):
        self.boot().prepare(6000, 1234, 288)
        result = self.invoke_runner("import time; time.sleep(10)", timeout=1)
        self.assertEqual(result.returncode, 1)
        self.assertIn(b"timed out", result.stderr)

    def test_crash_before_ready_is_failure(self):
        self.boot().prepare(6000, 1234, 288)
        result = self.invoke_runner("raise SystemExit(7)")
        self.assertEqual(result.returncode, 1)
        self.assertIn("failure", Season(self.root).state)

    def test_prefab_incompatibility_stops(self):
        self.boot().prepare(6000, 1234, 288)
        result = self.invoke_runner('print("Prefab IDs don\'t match! 1/2 -> vehicle.prefab"); import time; time.sleep(3)')
        self.assertEqual(result.returncode, 1)
        self.assertIn(b"Incompatible world/save", result.stderr)

    def test_interrupted_start_cannot_adopt_a_partial_save(self):
        season = self.boot()
        season.prepare(6000, 1234, 288)
        season.file(288, "sav").write_bytes(b"SAVRpartial world after killed startup")
        with self.assertRaisesRegex(MapError, "incomplete startup"):
            Season(self.root).prepare(6000, 1234, 288)
        self.assertEqual((season.archive / "before-start.sav").read_bytes(), b"SAVRlatest buildings")

    def test_interrupted_prepare_retries_without_changing_save(self):
        season = self.boot()
        season.prepare(6000, 1234, 288)
        Season(self.root).prepare(6000, 1234, 288)
        self.assertEqual(season.file(288, "sav").read_bytes(), b"SAVRlatest buildings")

    def test_steam_failure_does_not_install_oxide_or_start_rust(self):
        # Exercise the actual shell flow; stop at failed SteamCMD before any game code.
        launch = HELPER.with_name("launch.sh")
        sandbox = Path(self.temporary.name)
        scripts = sandbox / "bin"
        scripts.mkdir()
        for name, body in {"steamcmd": "exit 42", "curl": "touch oxide-called", "RustDedicated": "touch rust-called"}.items():
            path = scripts / name
            path.write_text("#!/bin/sh\n" + body + "\n")
            path.chmod(0o755)
        env = os.environ | {"PATH": str(scripts) + ":" + os.environ["PATH"], "ENV_TS_EXITNODE_IP": ""}
        result = subprocess.run(["bash", str(launch)], cwd=sandbox, env=env,
                                capture_output=True, timeout=5)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn(b"SteamCMD failed", result.stderr)
        self.assertFalse((sandbox / "oxide-called").exists())
        self.assertFalse((sandbox / "rust-called").exists())


if __name__ == "__main__":
    unittest.main()
