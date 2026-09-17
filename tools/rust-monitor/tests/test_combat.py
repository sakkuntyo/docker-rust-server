import importlib.util
from pathlib import Path
import subprocess
import unittest
from types import SimpleNamespace
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('combat', Path(__file__).parent.parent / 'collector' / 'combat.py')
combat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(combat)

ROW = '{"time":"12.34s","attacker":"you","id":"123","target":"player","id":"456","weapon":"rifle.ak","ammo":"riflebullet","area":"head","distance":"15.3m","old_hp":"100.0","new_hp":"0.0","info":"killed","hits":"1","integrity":"1.00","travel":"0.03s","mismatch":"0.00m","desync":"35"}'
REQUEST = {'mode': 'combat', 'container': 'rust-test', 'steamid': '76561198000000001'}


class CombatTests(unittest.TestCase):
    def test_duplicate_json_id_keys_and_identical_hits_are_preserved(self):
        result = combat.parse_combat('[' + ROW + ',' + ROW + ']')
        self.assertEqual(len(result), 2)
        self.assertEqual(result[0]['AgeSeconds'], 12.34)
        self.assertEqual(result[0]['Fields'][1], '123')
        self.assertEqual(result[0]['Fields'][3], '456')

    def test_empty_and_invalid_are_different(self):
        self.assertEqual(combat.parse_combat('[]'), [])
        for raw in ('{}', 'null', '[[]]', '[{}]', 'Unknown command', ROW,
                    '[' + ROW.replace('12.34s', 'NaNs') + ']', '[' + ROW.replace('"id":"123",', '') + ']'):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                combat.parse_combat(raw)

    def test_command_is_read_only_with_validated_target(self):
        with patch.object(combat, 'combat_epoch', return_value='epoch'), patch.object(combat.subprocess, 'run', return_value=SimpleNamespace(returncode=0, stdout='[' + ROW + ']')) as run:
            result = combat.combat_request(REQUEST)
            self.assertTrue(result['Available'])
            self.assertEqual(result['SteamId'], REQUEST['steamid'])
            self.assertEqual(run.call_args.args[0][-1], 'server.combatlog 76561198000000001 --json')
            self.assertIn('"$1"', run.call_args.args[0][-3])

    def test_unavailable_player_does_not_claim_an_empty_log(self):
        with patch.object(combat, 'combat_epoch', return_value='epoch'), patch.object(combat, 'combat_rcon', return_value='invalid player'):
            result = combat.combat_request(REQUEST)
            self.assertFalse(result['Available'])
            self.assertTrue(result['Reason'])

    def test_restart_during_read_is_rejected(self):
        with patch.object(combat, 'combat_epoch', side_effect=['old', 'new']), patch.object(combat, 'combat_rcon', return_value='[]'):
            self.assertIn('Error', combat.combat_request(REQUEST))

    def test_failures_never_erase_history_or_expose_raw_errors(self):
        with patch.object(combat, 'combat_epoch', return_value='epoch'):
            for raw in ('', 'Unknown command: secret', '{}'):
                with patch.object(combat, 'combat_rcon', return_value=raw):
                    result = combat.combat_request(REQUEST)
                    self.assertIn('Error', result)
                    self.assertNotIn('secret', str(result))
            with patch.object(combat, 'combat_rcon', side_effect=subprocess.TimeoutExpired('rcon', 12)) as rcon:
                self.assertIn('Error', combat.combat_request(REQUEST))
                rcon.assert_called_once()

    def test_invalid_requests_never_reach_ssh_or_rcon(self):
        with patch.object(combat, 'combat_epoch') as epoch:
            for change in ({'steamid': '76561198000000001;quit'}, {'steamid': '７' * 17}, {'container': 'rust-test;quit'}, {'mode': 'say'}):
                self.assertIn('Error', combat.combat_request(dict(REQUEST, **change)))
            epoch.assert_not_called()

    def test_epoch_changes_with_server_process_not_helper_process(self):
        top = 'PID COMMAND STARTED\n10 RustDedicated Thu Sep 17 10:00:00 2026\n20 sleep Thu Sep 17 12:00:00 2026\n'
        with patch.object(combat.Path, 'read_text', return_value='boot'), patch.object(combat.subprocess, 'run') as run:
            run.return_value = SimpleNamespace(returncode=0, stdout=top)
            first = combat.combat_epoch('rust-test')
            run.return_value.stdout = top.replace('20 sleep', '21 sleep')
            self.assertEqual(first, combat.combat_epoch('rust-test'))
            run.return_value.stdout = top.replace('10 RustDedicated', '11 RustDedicated')
            self.assertNotEqual(first, combat.combat_epoch('rust-test'))


if __name__ == '__main__':
    unittest.main()
