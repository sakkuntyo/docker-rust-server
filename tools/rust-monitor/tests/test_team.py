import importlib.util
import json
from pathlib import Path
import unittest
from types import SimpleNamespace
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('team', Path(__file__).parent.parent / 'collector' / 'team.py')
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)
ID = '76561198000000001'
ROW = dict(steamID=ID, username='Demo', online='x', leader='x')


class TeamTests(unittest.TestCase):
    def test_members_include_offline_and_leader(self):
        rows = [ROW, dict(ROW, steamID='76561198000000002', online='', leader='')]
        result = mod.parse_team(json.dumps(rows), ID)
        self.assertEqual(result['State'], 'members')
        self.assertTrue(result['Members'][0]['Leader'])
        self.assertFalse(result['Members'][1]['Online'])

    def test_none_unavailable_and_failure_are_distinct(self):
        self.assertEqual(mod.parse_team('Player is not in a team', ID)['State'], 'none')
        self.assertEqual(mod.parse_team('Player not found', ID)['State'], 'unavailable')
        for raw in ['', 'Unknown command', '[]', '{}', json.dumps([ROW, ROW]), json.dumps([dict(ROW, steamID='76561198000000002')]),
                    json.dumps([dict(ROW, online='maybe')])]:
            with self.assertRaises(ValueError): mod.parse_team(raw, ID)

    def test_read_only_command_and_fixed_target(self):
        with patch.object(mod.subprocess, 'run', return_value=SimpleNamespace(returncode=0, stdout=json.dumps([ROW]))) as run:
            result = mod.team_request(dict(mode='team', container='rust-demo', steamid=ID))
            self.assertEqual(result['SteamId'], ID)
            self.assertEqual(run.call_args.args[0][-1], 'global.teaminfo ' + ID + ' --json')
            self.assertNotIn('shell', run.call_args.kwargs)

    def test_invalid_targets_never_run(self):
        with patch.object(mod.subprocess, 'run') as run:
            for container, steamid in [('rust-demo;quit', ID), ('rust-demo', ID + ';quit')]:
                self.assertIn('Error', mod.team_request(dict(mode='team', container=container, steamid=steamid)))
            run.assert_not_called()
