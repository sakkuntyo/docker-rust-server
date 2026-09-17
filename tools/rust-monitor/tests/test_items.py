import importlib.util
import json
from pathlib import Path
import subprocess
import unittest
from types import SimpleNamespace
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('items', Path(__file__).parent.parent / 'collector' / 'items.py')
items = importlib.util.module_from_spec(spec)
spec.loader.exec_module(items)
STEAM = '76561198000000001'
ITEM = dict(ItemId=1545779598, Name='Assault Rifle', ShortName='rifle.ak', Category='Weapon', StackSize=1)
REQUEST = dict(mode='giveitem', container='rust-demo', steamid=STEAM, itemid=ITEM['ItemId'], shortname=ITEM['ShortName'], amount=2)


class ItemTests(unittest.TestCase):
    def execute(self, request=REQUEST, reply='', players=None, catalog=None):
        with patch.object(items, 'run_container', side_effect=[json.dumps(catalog or [ITEM]), json.dumps(players if players is not None else [{'SteamID': STEAM}]), reply]) as run:
            result = items.item_request(request)
            return result, run.call_args_list

    def test_menu_reads_only_and_returns_live_definitions(self):
        result, calls = self.execute(dict(REQUEST, mode='itemmenu'))
        self.assertEqual(result, dict(SteamId=STEAM, Online=True, Items=[ITEM]))
        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[1].args[-1], 'playerlist')

    def test_invalid_requests_do_not_reach_container(self):
        with patch.object(items, 'run_container') as run:
            for field, value in [('steamid', STEAM + ';quit'), ('steamid', '７' * 17), ('container', 'rust-demo;quit'),
                                 ('mode', 'say'), ('itemid', True), ('amount', True), ('amount', 0), ('amount', 10001),
                                 ('amount', '2'), ('shortname', 'rifle.ak";quit'), ('shortname', 'rifle.ak\nquit'), ('shortname', '../wood')]:
                with self.subTest(field=field, value=value):
                    result = items.item_request(dict(REQUEST, **{field: value}))
                    self.assertTrue('Error' in result or result['State'] == 'rejected')
            run.assert_not_called()

    def test_offline_and_exact_item_mismatch_do_not_give(self):
        for request, players in [(REQUEST, []), (dict(REQUEST, itemid=-1), None), (dict(REQUEST, shortname='rifle'), None)]:
            result, calls = self.execute(request, players=players)
            self.assertEqual(result['State'], 'rejected')
            self.assertEqual(len(calls), 2)

    def test_give_captures_exact_player_item_amount_once(self):
        result, calls = self.execute()
        self.assertEqual(result['State'], 'accepted')
        self.assertEqual(len(calls), 3)
        self.assertEqual(calls[-1].args, ('rust-demo', items.RCON_SH, f'inventory.giveto {STEAM} "rifle.ak" 2'))

    def test_legitimate_spaces_are_one_quoted_shortname(self):
        bow = dict(ITEM, ShortName='legacy bow')
        result, calls = self.execute(dict(REQUEST, shortname='legacy bow'), catalog=[bow])
        self.assertEqual(result['State'], 'accepted')
        self.assertEqual(calls[-1].args[-1], f'inventory.giveto {STEAM} "legacy bow" 2')

    def test_full_inventory_and_disconnect_are_reported_as_rejected(self):
        for reply in ["Couldn't find player!", 'Invalid Item!', "Couldn't give item (inventory full?)"]:
            result, calls = self.execute(reply=reply)
            self.assertEqual(result['State'], 'rejected')
            self.assertEqual(len(calls), 3)

    def test_unknown_response_and_timeout_never_retry(self):
        for reply in ['Unknown command', subprocess.TimeoutExpired('rcon', 15), RuntimeError('unavailable')]:
            result, calls = self.execute(reply=reply)
            self.assertEqual(result['State'], 'unknown')
            self.assertEqual(len(calls), 3)

    def test_preflight_failure_reports_no_give(self):
        with patch.object(items, 'run_container', side_effect=subprocess.TimeoutExpired('docker', 15)) as run:
            result = items.item_request(REQUEST)
            self.assertEqual(result['State'], 'rejected')
            self.assertIn('実行していません', result['Message'])
            run.assert_called_once()

    def test_catalog_retained_alias_uses_current_dot_name(self):
        alias, current = dict(ITEM, ShortName='2module car'), dict(ITEM, ShortName='2module.car')
        for rows in ([alias, current], [current, alias]):
            with patch.object(items, 'run_container', return_value=json.dumps(rows)):
                self.assertEqual(items.read_catalog('rust-demo'), [current])

    def test_malformed_catalog_or_presence_cannot_enable_give(self):
        for raw in ['null', '{}', '[]', json.dumps([dict(ITEM, StackSize=0)]), json.dumps([dict(ITEM, ShortName='x"')])]:
            with patch.object(items, 'run_container', return_value=raw) as run:
                self.assertEqual(items.item_request(REQUEST)['State'], 'rejected')
                run.assert_called_once()
        for raw in ['null', '{}', '[{}]', 'not json']:
            with patch.object(items, 'run_container', side_effect=[json.dumps([ITEM]), raw]) as run:
                self.assertEqual(items.item_request(REQUEST)['State'], 'rejected')
                self.assertEqual(run.call_count, 2)

    def test_subprocess_keeps_command_one_argument_and_fixed_shell(self):
        command = f'inventory.giveto {STEAM} "rifle.ak" 2'
        with patch.object(items.subprocess, 'run', return_value=SimpleNamespace(returncode=0, stdout='')) as run:
            items.run_container('rust-demo', items.RCON_SH, command)
            self.assertEqual(run.call_args.args[0], ['sudo', '-n', 'docker', 'exec', 'rust-demo', 'sh', '-lc', items.RCON_SH, 'rust-monitor-items', command])
            self.assertNotIn('shell', run.call_args.kwargs)


if __name__ == '__main__':
    unittest.main()
