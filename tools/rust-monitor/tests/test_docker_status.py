import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("docker_status", Path(__file__).parent.parent / "collector" / "docker_status.py")
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)


class DockerCollectorTests(unittest.TestCase):
    def raw(self):
        return {"name": "Test server", "rcon_port": "28016", "max": "100", "cycle": "daily", "end": "1789387200", "created": "1789300910",
                "size": "2700", "seed": "1789171597022", "maprow": "2026-09-13T12:06:18Z,https://example.invalid/a.map,https://example.invalid/a.jpg",
                "current": [{"SteamId": "76561198000000001", "Name": "日本語"}],
                "history": [{"steamid": "76561198000000001", "name": "日本語", "addunixtimestamp": 1789303000},
                            {"steamid": "76561198000000001", "name": "旧名", "addunixtimestamp": 1789302000},
                            {"steamid": "76561198000000002", "name": "Offline", "addunixtimestamp": 1789304000}]}

    def test_summary_only_and_wipe_boundary(self):
        class Process:
            stdout = io.StringIO("2026-09-13T12:00:38.123456789Z INFO: online: 99 / 100\n"
                                 "2026-09-13T12:30:02.123456789Z INFO: online: 3 / 100\n"
                                 "2026-09-13T13:00:02.123456789Z INFO: online: 8 / 100\n"
                                 "2026-09-13T13:30:02.123456789Z INFO: online: 8 / 100\n"
                                 "2026-09-14T12:30:02.123456789Z INFO: online: 90 / 100\n")
            def wait(self, timeout):
                return 0
        with patch.object(collector, "docker", return_value=json.dumps(self.raw())), patch.object(collector.subprocess, "Popen", return_value=Process()):
            result = collector.collect("rust-test", True)
        self.assertEqual(result["Peak"], 8)
        self.assertEqual(result["Samples"], 3)
        self.assertEqual(result["PeakAt"], "2026-09-13T13:00:02.123456Z")
        self.assertEqual(result["PeakLastAt"], "2026-09-13T13:30:02.123456Z")
        self.assertEqual(result["Current"], 1)
        self.assertEqual(result["SeasonUnique"], 2)
        self.assertNotIn("Players", result)
        self.assertNotIn("MapUrl", result)
        self.assertNotIn("MapAt", result)

    def test_legacy_roster_first_seen_is_not_last_login(self):
        with patch.object(collector, "docker", return_value=json.dumps(self.raw())):
            result = collector.collect("rust-test", False)
        offline = next(p for p in result["Players"] if not p["Online"])
        self.assertEqual(offline["LastSeen"], "")
        online = next(p for p in result["Players"] if p["Online"])
        self.assertEqual(online["Name"], "日本語")
        self.assertEqual(result["Seed"], "1789171597022")

    def test_previous_map_is_not_current_map(self):
        raw = self.raw()
        raw["maprow"] = "2026-09-12T12:06:18Z,https://example.invalid/old.map,https://example.invalid/old.jpg"
        with patch.object(collector, "docker", return_value=json.dumps(raw)):
            self.assertEqual(collector.collect("rust-test", False)["MapUrl"], "")

    def test_monthly_is_five_weeks(self):
        raw = self.raw()
        raw.update(cycle="monthly", end="1790337600", created="1787313730")
        with patch.object(collector, "docker", return_value=json.dumps(raw)):
            result = collector.collect("rust-test", False)
        self.assertEqual(result["SeasonStart"], "2026-08-21T12:00:00Z")

    def test_zero_players_and_empty_history_are_valid_zero(self):
        raw = self.raw()
        raw.update(current=[], history=[], rcon_status="ok", history_status="ok")
        with patch.object(collector, "docker", return_value=json.dumps(raw)):
            result = collector.collect("rust-test", False)
        self.assertEqual(result["Current"], 0)
        self.assertEqual(result["SeasonUnique"], 0)
        self.assertEqual(result["Error"], "")

    def test_missing_history_does_not_hide_live_count_or_metadata(self):
        raw = self.raw()
        raw.update(current=[], history=None, history_status="missing")
        with patch.object(collector, "docker", return_value=json.dumps(raw)):
            result = collector.collect("rust-test", False)
        self.assertEqual(result["Current"], 0)
        self.assertIsNone(result["SeasonUnique"])
        self.assertEqual(result["Name"], "Test server")
        self.assertEqual(result["Capacity"], 100)
        self.assertEqual(result["SeasonEnd"], "2026-09-14T12:00:00Z")
        self.assertIn("未作成", result["Error"])

    def test_rcon_timeout_does_not_hide_history_or_peak(self):
        raw = self.raw()
        raw.update(current=None, rcon_status="timeout")
        def peak(name, start, cutoff, end, result):
            result.update(Peak=8, PeakAt="2026-09-13T13:00:00Z")
        with patch.object(collector, "docker", return_value=json.dumps(raw)), patch.object(collector, "read_peak", side_effect=peak):
            result = collector.collect("rust-test", True)
        self.assertIsNone(result["Current"])
        self.assertEqual(result["SeasonUnique"], 2)
        self.assertEqual(result["Peak"], 8)
        self.assertEqual(result["Capacity"], 100)
        self.assertNotIn("Players", result)

    def test_startup_with_missing_history_keeps_metadata(self):
        raw = self.raw()
        raw.update(current=None, history=None, rcon_status="unavailable", history_status="missing")
        with patch.object(collector, "docker", return_value=json.dumps(raw)):
            result = collector.collect("rust-test", False)
        self.assertIsNone(result["Current"])
        self.assertIsNone(result["SeasonUnique"])
        self.assertEqual(result["Capacity"], 100)
        self.assertTrue(result["SeasonStart"])

    def test_connection_reset_is_distinct_from_zero_and_missing_history(self):
        raw = self.raw()
        raw.update(current=None, history=None, rcon_status="connection_reset", history_status="missing")
        with patch.object(collector, "docker", return_value=json.dumps(raw)):
            result = collector.collect("rust-test", False)
        self.assertIsNone(result["Current"])
        self.assertIn("サーバー側から切断", result["Error"])
        self.assertIn("履歴は未作成", result["Error"])

    def test_log_failure_keeps_other_columns(self):
        with patch.object(collector, "docker", return_value=json.dumps(self.raw())), patch.object(collector, "read_peak", side_effect=OSError()):
            result = collector.collect("rust-test", True)
        self.assertEqual(result["Current"], 1)
        self.assertEqual(result["SeasonUnique"], 2)
        self.assertIsNone(result["Peak"])
        self.assertEqual(result["LogError"], "ログ取得待ち")

    def test_missing_period_is_unknown_not_row_failure(self):
        raw = self.raw()
        raw.update(end="", created="0")
        with patch.object(collector, "docker", return_value=json.dumps(raw)):
            result = collector.collect("rust-test", True)
        self.assertEqual(result["Current"], 1)
        self.assertEqual(result["SeasonEnd"], "")
        self.assertEqual(result["WipeId"], "")

    def test_ssh_server_includes_saved_offline_inventory_without_plugin(self):
        saved = {'Created': 1789301000, 'SavedAt': 1789303000, 'SaveWipeId': 'fixture',
                 'Players': {'76561198000000003': {'Name': 'Sleeper', 'Items': [], 'Position': {'X': 0, 'Y': 7, 'Z': -500}}}}
        with patch.object(collector, 'docker', return_value=json.dumps(self.raw())), patch.object(collector, 'read_saved_players', return_value=saved, create=True):
            result = collector.read_server('rust-test')
        self.assertEqual(len(result['Players']), 3)
        self.assertFalse(result['Inventories'][0]['Current'])
        self.assertEqual(result['Inventories'][0]['Source'], 'save')
        self.assertEqual(result['Server']['WipeId'], result['Inventories'][0]['WipeId'])
        self.assertTrue(result['PresenceAvailable'])
        member = next(p for p in result['Players'] if p['SteamId'] == '76561198000000003')
        self.assertEqual((member['X'], member['Y'], member['Z']), (0, 7, -500))
        self.assertEqual(member['PositionAt'], result['Server']['SaveAt'])
        self.assertFalse(member['Online'])

    def test_ssh_presence_failure_does_not_mark_saved_players_offline(self):
        raw = self.raw()
        raw.update(current=None, rcon_status='timeout')
        with patch.object(collector, 'docker', return_value=json.dumps(raw)), patch.object(collector, 'read_saved_players', side_effect=ValueError(), create=True):
            result = collector.read_server('rust-test')
        self.assertFalse(result['PresenceAvailable'])
        self.assertTrue(all(p['ObservedAt'] == '' for p in result['Players']))
        self.assertTrue(result['Server']['MapAvailable'])
        self.assertIn('セーブ', result['Warning'])


if __name__ == "__main__":
    unittest.main()
