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

    def test_ban_reason_is_data_not_shell_or_console_code(self):
        action = dict(ACTION, Action='ban', Reason='quotes " ; $(quit) 日本語')
        with patch.object(mod, 'ensure_helper'), patch.object(mod, 'run', return_value='{}') as run:
            mod.moderation_request(dict(container='rust-demo', action=action), SOURCE)
            command = run.call_args.args[2]
            self.assertNotIn('$(quit)', command)
            self.assertEqual(json.loads(base64.b64decode(command.split()[1]))['Reason'], action['Reason'])

    def test_timeout_or_unmatched_response_is_unknown_without_retry(self):
        for reply in ['{}', 'invalid', json.dumps(dict(Protocol=mod.PROTOCOL, RequestId='b'*32, State='accepted', Message='wrong')),
                      subprocess.TimeoutExpired('rcon', 15)]:
            with patch.object(mod, 'ensure_helper'), patch.object(mod, 'run', side_effect=[reply]) as run:
                result = mod.moderation_request(dict(container='rust-demo', action=ACTION), SOURCE)
                self.assertEqual(result['State'], 'unknown')
                self.assertEqual(run.call_count, 1)

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
        with patch.object(mod, 'run', side_effect=[digest + ' file', 'Unknown command', json.dumps(dict(Protocol=mod.PROTOCOL))]) as run, patch.object(mod.time, 'sleep'):
            mod.ensure_helper('rust-demo', SOURCE)
            self.assertEqual(run.call_count, 3)
            self.assertTrue(all(c.args[2] == 'rustmonitoradmin.ping' for c in run.call_args_list[1:]))
        self.assertNotIn('Options.Modded', mod.INSTALL_SH)

    def test_no_shell_interpolation_of_request(self):
        from types import SimpleNamespace
        with patch.object(mod.subprocess, 'run', return_value=SimpleNamespace(returncode=0, stdout='ok')) as run:
            mod.run('rust-demo', mod.RCON_SH, 'rustmonitoradmin.ping')
            args = run.call_args.args[0]
            self.assertEqual(args[-1], 'rustmonitoradmin.ping')
            self.assertNotIn('shell', run.call_args.kwargs)
            self.assertNotIn('-i', args)


if __name__ == '__main__': unittest.main()
