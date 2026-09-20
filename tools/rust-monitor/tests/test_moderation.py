import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('moderation', Path(__file__).parent.parent / 'collector' / 'moderation.py')
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)
SOURCE = (Path(__file__).parent.parent / 'plugin' / 'RustMonitorAdmin.cs').read_text(encoding='utf-8')
ACTION = dict(RequestId='a' * 32, Action='delete', SteamId='76561198000000001', Name='Demo', WipeId='save:1789171200:demo',
              Item=dict(Uid='18446744073709551614', ItemId=1545779598, Container='belt', Slot=0, Amount=1, Skin='0', Condition=100, MaxCondition=100, Ammo=31, Contents=[]))


class ModerationTests(unittest.TestCase):
    def test_invalid_requests_never_install_or_mutate(self):
        bad = [dict(ACTION, SteamId='７' * 17), dict(ACTION, Action='clear'), dict(ACTION, WipeId=''),
               dict(ACTION, Item=dict(ACTION['Item'], Uid='0')), dict(ACTION, Item=dict(ACTION['Item'], Container='corpse')),
               dict(ACTION, Item=dict(ACTION['Item'], Uid=str(2**64))), dict(ACTION, RequestId='bad'),
               dict(ACTION, Action='ban', Reason='quit\nban', Name='Demo')]
        with patch.object(mod, 'run') as run:
            for action in bad:
                self.assertEqual(mod.moderation_request(dict(container='rust-demo', action=action), SOURCE)['State'], 'rejected')
            self.assertEqual(mod.moderation_request(dict(container='rust-demo;quit', action=ACTION), SOURCE)['State'], 'rejected')
            run.assert_not_called()

    def test_mutation_is_one_base64_command_after_verified_helper(self):
        response = dict(Protocol=mod.PROTOCOL, RequestId=ACTION['RequestId'], State='accepted', Message='done')
        with patch.object(mod, 'ensure_helper') as prepare, patch.object(mod, 'run', return_value=json.dumps(response)) as run:
            self.assertEqual(mod.moderation_request(dict(container='rust-demo', action=ACTION), SOURCE), response)
            prepare.assert_called_once_with('rust-demo', SOURCE)
            run.assert_called_once()
            command = run.call_args.args[2]
            self.assertTrue(command.startswith('rustmonitoradmin.execute '))
            self.assertEqual(json.loads(base64.b64decode(command.split()[1])), ACTION)

    def test_nested_parent_paths_are_checked_before_dispatch(self):
        parent = dict(Uid='100', ItemId=123, Slot=7)
        for parents in [None, [dict(parent, Uid='0')], [dict(parent, Slot=-1)], [parent, parent], [dict(parent, Uid=ACTION['Item']['Uid'])], [parent]*7]:
            with patch.object(mod, 'ensure_helper') as helper, patch.object(mod, 'run') as run:
                reply = mod.moderation_request(dict(container='rust-demo', action=dict(ACTION, Parents=parents)), SOURCE)
                self.assertEqual(reply['State'], 'rejected'); helper.assert_not_called(); run.assert_not_called()
        action = dict(ACTION, Parents=[parent])
        response = dict(Protocol=mod.PROTOCOL, RequestId=ACTION['RequestId'], State='accepted', Message='done')
        with patch.object(mod, 'ensure_helper'), patch.object(mod, 'run', return_value=json.dumps(response)) as run:
            self.assertEqual(mod.moderation_request(dict(container='rust-demo', action=action), SOURCE)['State'], 'accepted')
            self.assertEqual(json.loads(base64.b64decode(run.call_args.args[2].split()[1]))['Parents'], [parent])

    def test_ban_reason_is_data_not_shell_or_console_code(self):
        action = dict(ACTION, Action='ban', Reason='quotes " ; $(quit) 日本語')
        with patch.object(mod, 'ensure_helper'), patch.object(mod, 'run', return_value='{}') as run:
            mod.moderation_request(dict(container='rust-demo', action=action), SOURCE)
            command = run.call_args_list[0].args[2]
            self.assertNotIn('$(quit)', command)
            self.assertEqual(json.loads(base64.b64decode(command.split()[1]))['Reason'], action['Reason'])

    def test_timeout_or_unmatched_response_is_unknown_without_retry(self):
        for reply in ['{}', 'invalid', json.dumps(dict(Protocol=mod.PROTOCOL, RequestId='b'*32, State='accepted', Message='wrong')),
                      subprocess.TimeoutExpired('rcon', 15)]:
            with patch.object(mod, 'ensure_helper'), patch.object(mod, 'run', side_effect=[reply, '{}']) as run:
                result = mod.moderation_request(dict(container='rust-demo', action=ACTION), SOURCE)
                self.assertEqual(result['State'], 'unknown')
                self.assertEqual(run.call_count, 2)
                self.assertEqual(run.call_args_list[1].args[2], 'rustmonitoradmin.result ' + ACTION['RequestId'])

    def test_helper_failure_never_dispatches_mutation(self):
        with patch.object(mod, 'ensure_helper', side_effect=RuntimeError), patch.object(mod, 'run') as run:
            self.assertEqual(mod.moderation_request(dict(container='rust-demo', action=ACTION), SOURCE)['State'], 'rejected')
            run.assert_not_called()

    def test_existing_helper_must_match_bundled_source(self):
        with patch.object(mod, 'run', return_value='0' * 64 + ' file') as run:
            with self.assertRaises(ValueError): mod.ensure_helper('rust-demo', SOURCE)
            self.assertEqual(run.call_count, 1)
            self.assertEqual(run.call_args.kwargs['data'], SOURCE)
        self.assertIn('ln "$tmp" "$dest"', mod.INSTALL_SH)
        self.assertNotIn('mv -f', mod.INSTALL_SH)

    def test_install_waits_for_protocol_without_touching_modded_setting(self):
        digest = hashlib.sha256(SOURCE.encode()).hexdigest()
        with patch.object(mod, 'run', side_effect=[digest + ' file', 'Unknown command', json.dumps(dict(Protocol=mod.PROTOCOL, Version=mod.HELPER_VERSION))]) as run, patch.object(mod.time, 'sleep'):
            mod.ensure_helper('rust-demo', SOURCE)
            self.assertEqual(run.call_count, 3)
            self.assertTrue(all(c.args[2] == 'rustmonitoradmin.ping' for c in run.call_args_list[1:]))
        self.assertNotIn('Options.Modded', mod.INSTALL_SH)

    def test_known_helper_upgrade_reloads_once_and_requires_new_version(self):
        digest = hashlib.sha256(SOURCE.encode()).hexdigest()
        with patch.object(mod, 'run', side_effect=[digest + ' file', json.dumps(dict(Protocol=mod.PROTOCOL)), 'Reloaded',
                json.dumps(dict(Protocol=mod.PROTOCOL, Version=mod.HELPER_VERSION))]) as run, patch.object(mod.time, 'sleep'):
            mod.ensure_helper('rust-demo', SOURCE)
            self.assertEqual([c.args[2] for c in run.call_args_list[1:]], ['rustmonitoradmin.ping', 'oxide.reload RustMonitorAdmin', 'rustmonitoradmin.ping'])
        self.assertIn('RustMonitorAdmin.cs', mod.INSTALL_SH)
        self.assertIn('c58cf9b48ef80a5c829908b98f0f5927b2f89c9eaaac0ba4060fcb9e83f300b8', mod.INSTALL_SH)
        self.assertIn('0.1.0.bak', mod.INSTALL_SH)

    def test_console_log_reply_uses_read_only_result_lookup(self):
        result = dict(Protocol=mod.PROTOCOL, RequestId=ACTION['RequestId'], State='accepted', Message='done')
        with patch.object(mod, 'ensure_helper'), patch.object(mod, 'run', side_effect=['[RustMonitorAdmin] ban log', json.dumps(result)]) as run:
            self.assertEqual(mod.moderation_request(dict(container='rust-demo', action=ACTION), SOURCE), result)
            self.assertTrue(run.call_args_list[0].args[2].startswith('rustmonitoradmin.execute '))
            self.assertEqual(run.call_args_list[1].args[2], 'rustmonitoradmin.result ' + ACTION['RequestId'])

    def test_large_item_descriptions_are_staged_below_cli_limit(self):
        action = copy.deepcopy(ACTION)
        action['Item']['Contents'] = [dict(action['Item'], Uid=str(i+100), Slot=i, Contents=[]) for i in range(12)]
        parts = []
        def respond(container, script, cmd):
            self.assertLessEqual(len(cmd.encode()), 1000)
            fields = cmd.split()
            if fields[0] == 'rustmonitoradmin.prepare':
                parts.append(fields[4])
                return json.dumps(dict(Protocol=mod.PROTOCOL, RequestId=fields[1], Prepared=int(fields[2])))
            self.assertEqual(fields, ['rustmonitoradmin.commit', action['RequestId']])
            return json.dumps(dict(Protocol=mod.PROTOCOL, RequestId=fields[1], State='accepted', Message='done'))
        with patch.object(mod, 'ensure_helper'), patch.object(mod, 'run', side_effect=respond) as run:
            self.assertEqual(mod.moderation_request(dict(container='rust-demo', action=action), SOURCE)['State'], 'accepted')
            self.assertGreater(len(parts), 1)
            self.assertEqual(json.loads(base64.b64decode(''.join(parts))), action)
            self.assertEqual(sum(c.args[2].startswith('rustmonitoradmin.commit ') for c in run.call_args_list), 1)

    def test_failed_staging_never_commits(self):
        action = copy.deepcopy(ACTION)
        action['Item']['Contents'] = [dict(action['Item'], Uid=str(i+100), Contents=[]) for i in range(12)]
        with patch.object(mod, 'ensure_helper'), patch.object(mod, 'run', return_value='{}') as run:
            self.assertEqual(mod.moderation_request(dict(container='rust-demo', action=action), SOURCE)['State'], 'rejected')
            self.assertTrue(all(c.args[2].startswith('rustmonitoradmin.prepare ') for c in run.call_args_list))

    def test_no_shell_interpolation_of_request(self):
        from types import SimpleNamespace
        with patch.object(mod.subprocess, 'run', return_value=SimpleNamespace(returncode=0, stdout='ok')) as run:
            mod.run('rust-demo', mod.RCON_SH, 'rustmonitoradmin.ping')
            args = run.call_args.args[0]
            self.assertEqual(args[-1], 'rustmonitoradmin.ping')
            self.assertNotIn('shell', run.call_args.kwargs)
            self.assertNotIn('-i', args)

    def test_inventory_reads_live_data_including_empty_items(self):
        request = dict(container='rust-demo', steamid=ACTION['SteamId'], wipe=ACTION['WipeId'])
        for items in [[], [ACTION['Item']]]:
            inventory = dict(SteamId=ACTION['SteamId'], WipeId=ACTION['WipeId'], Source='live', Current=True,
                             CapturedAt='2026-09-18T01:00:00Z', Items=items)
            reply = dict(Protocol=mod.PROTOCOL, SteamId=ACTION['SteamId'], WipeId=ACTION['WipeId'], Available=True, Inventory=inventory)
            with patch.object(mod, 'ensure_helper') as prepare, patch.object(mod, 'run', return_value=json.dumps(reply)) as run:
                self.assertEqual(mod.inventory_request(request, SOURCE), inventory)
                prepare.assert_called_once()
                run.assert_called_once_with('rust-demo', mod.RCON_SH, 'rustmonitoradmin.inventory ' + ACTION['SteamId'])

    def test_inventory_invalid_targets_and_unavailable_or_wrong_responses_do_not_clear_data(self):
        request = dict(container='rust-demo', steamid=ACTION['SteamId'], wipe=ACTION['WipeId'])
        with patch.object(mod, 'ensure_helper') as prepare, patch.object(mod, 'run') as run:
            self.assertIn('Error', mod.inventory_request(dict(request, steamid='bad;quit'), SOURCE))
            prepare.assert_not_called(); run.assert_not_called()
        inventory = dict(SteamId=ACTION['SteamId'], WipeId=ACTION['WipeId'], Source='live', Current=True, CapturedAt='2026-09-18T01:00:00Z', Items=[])
        good = dict(Protocol=mod.PROTOCOL, SteamId=ACTION['SteamId'], WipeId=ACTION['WipeId'], Available=True, Inventory=inventory)
        replies = [dict(good, Available=False), dict(good, SteamId='76561198000000002'), dict(good, WipeId='save:1:other'),
                   dict(good, Inventory=dict(inventory, Source='save')), dict(good, Inventory=dict(inventory, CapturedAt='bad'))]
        for reply in replies:
            with patch.object(mod, 'ensure_helper'), patch.object(mod, 'run', return_value=json.dumps(reply)):
                self.assertIn('Error', mod.inventory_request(request, SOURCE))


if __name__ == '__main__': unittest.main()
