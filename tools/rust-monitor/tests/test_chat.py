import importlib.util
import json
from pathlib import Path
import subprocess
import unittest
from types import SimpleNamespace
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('monitor_chat', Path(__file__).parent.parent / 'collector' / 'chat.py')
chat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(chat)


class ChatTests(unittest.TestCase):
    def test_empty_history_is_valid(self):
        with patch.object(chat, 'chat_rcon', return_value='[]'):
            self.assertEqual(chat.chat_request({'mode': 'chat', 'container': 'rust-test'})['Messages'], [])

    def test_channels_duplicates_and_unicode_are_preserved(self):
        row = dict(Channel=1, Message='日本語 🦀 <b>literal</b>', UserId='76561198000000001', Username='名前', Time=1789477200)
        self.assertEqual(chat.parse_chat(json.dumps([row, row])), [row, row])

    def test_malformed_history_is_not_an_empty_success(self):
        for raw in ('{}', 'Unknown command', '[{}]', '[{"Time": "invalid"}]'):
            with self.subTest(raw=raw), patch.object(chat, 'chat_rcon', return_value=raw):
                self.assertIn('Error', chat.chat_request({'mode': 'chat', 'container': 'rust-test'}))

    def test_send_is_one_literal_rcon_argument(self):
        message = '日本語 "double" \'single\' \\ ; quit $(echo test) `test` 🦀'
        with patch.object(chat.subprocess, 'run', return_value=SimpleNamespace(returncode=0, stdout='')) as run:
            self.assertTrue(chat.chat_request({'mode': 'say', 'container': 'rust-test', 'message': message})['Accepted'])
            run.assert_called_once()
            args = run.call_args.args[0]
            self.assertEqual(args[-1], 'global.say ' + message)
            self.assertNotIn(message, args[-3])  # Shell script is fixed, with quoted $1.
            self.assertIn('"$1"', args[-3])
            self.assertNotIn('shell', run.call_args.kwargs)

    def test_timeout_does_not_retry_or_claim_delivery(self):
        with patch.object(chat, 'chat_rcon', side_effect=subprocess.TimeoutExpired('rcon', 12)) as send:
            self.assertFalse(chat.chat_request({'mode': 'say', 'container': 'rust-test', 'message': 'test'})['Accepted'])
            send.assert_called_once()

    def test_error_output_cannot_be_a_success_or_leak_credentials(self):
        with patch.object(chat, 'chat_rcon', return_value='Error: secret must not be returned'):
            self.assertEqual(chat.chat_request({'mode': 'say', 'container': 'rust-test', 'message': 'test'}), {'Accepted': False})

    def test_control_characters_and_limits_rejected_before_rcon(self):
        for message in ('', '  ', 'x' * 257, 'a\nquit', 'a\rquit', 'a\x00b', 'a\u2028quit', '🦀' * 129):
            with self.subTest(message=repr(message)), patch.object(chat, 'chat_rcon') as send:
                self.assertIn('Error', chat.chat_request({'mode': 'say', 'container': 'rust-test', 'message': message}))
                send.assert_not_called()
        self.assertEqual(chat.chat_message('🦀' * 128), '🦀' * 128)

    def test_arbitrary_modes_and_containers_rejected(self):
        with patch.object(chat, 'chat_rcon') as rcon:
            for request in ({'mode': 'quit', 'container': 'rust-test'}, {'mode': 'say', 'container': 'rust-test; quit'}, {'mode': 'chat', 'container': '-x'}):
                self.assertIn('Error', chat.chat_request(request))
            rcon.assert_not_called()


if __name__ == '__main__':
    unittest.main()
