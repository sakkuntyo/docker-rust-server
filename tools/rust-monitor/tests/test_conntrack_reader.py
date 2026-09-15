import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('conntrack_reader', Path(__file__).parent.parent / 'collector' / 'conntrack_reader.py')
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)


def flow(original='203.0.113.10', server='100.64.0.10', peer='100.64.0.1', sport=40000, translated=40000, game=31015, protocol='udp', assured=True):
    return f'ipv4 2 {protocol} 17 119 src={original} dst=192.0.2.1 sport={sport} dport=31015 src={server} dst={peer} sport={game} dport={translated}' + (' [ASSURED]' if assured else ' [UNREPLIED]')


class ConntrackTests(unittest.TestCase):
    def match(self, text, peers=None):
        return reader.attribute_connections(text, ['100.64.0.10'], 31015, peers or ['100.64.0.1:40000'])

    def test_nat_rewritten_port_uses_reply_tuple(self):
        result = self.match(flow(sport=12345, translated=40000))[0]
        self.assertEqual((result['Ip'], result['Status']), ('203.0.113.10', 'verified'))

    def test_same_original_port_for_two_clients_is_unambiguous(self):
        text = flow(sport=12345, translated=40000) + '\n' + flow(original='203.0.113.11', sport=12345, translated=40001)
        result = self.match(text, ['100.64.0.1:40000', '100.64.0.1:40001'])
        self.assertEqual([p['Ip'] for p in result], ['203.0.113.10', '203.0.113.11'])

    def test_other_server_port_protocol_or_peer_cannot_match(self):
        for changes in ({'server': '100.64.0.11'}, {'game': 28015}, {'protocol': 'tcp'}, {'peer': '100.64.0.2'}, {'assured': False}):
            self.assertEqual(self.match(flow(**changes))[0]['Status'], 'not_found')

    def test_multiple_original_addresses_are_not_guessed(self):
        result = self.match(flow() + '\n' + flow(original='203.0.113.11'))[0]
        self.assertEqual((result['Ip'], result['Status']), ('', 'ambiguous'))

    def test_duplicates_do_not_create_false_ambiguity(self):
        self.assertEqual(self.match(flow() + '\n' + flow())[0]['Status'], 'verified')

    def test_malformed_or_unrelated_records_are_not_returned(self):
        result = self.match('udp 17 20 src=invalid dst=missing\n' + flow(translated=60000))
        self.assertEqual(result, [{'Address': '100.64.0.1:40000', 'Ip': '', 'Status': 'not_found'}])

    def test_ipv6_endpoints_and_non_endpoint_inputs(self):
        text = 'ipv6 10 udp 17 119 src=2001:db8::12 dst=2001:db8::2 sport=12 dport=31015 src=fd7a::10 dst=fd7a::1 sport=31015 dport=40000 [ASSURED]'
        result = reader.attribute_connections(text, ['fd7a::10'], 31015, ['[fd7a::1]:40000'])
        self.assertEqual(result[0]['Ip'], '2001:db8::12')
        for value in ('-oProxyCommand=evil', '1.2.3.4;echo:5', '1.2.3.4:0', '1.2.3.4:65536', '1.2.3.4', None):
            self.assertIsNone(reader.endpoint(value))

    def test_only_list_command_with_exact_server_scope_is_used(self):
        class Result:
            returncode = 0
            stdout = flow()
        with patch.object(reader.shutil, 'which', return_value='/usr/sbin/conntrack'), patch.object(reader.subprocess, 'run', return_value=Result()) as run:
            result = reader.read_connections({'gamePort': 31015, 'serverIps': ['100.64.0.10'], 'peers': ['100.64.0.1:40000']})
        self.assertEqual(result['Matches'][0]['Status'], 'verified')
        self.assertEqual(run.call_args.args[0], ['sudo', '-n', '/usr/sbin/conntrack', '-L', '-p', 'udp', '-f', 'ipv4', '--reply-src', '100.64.0.10', '--reply-port-src', '31015', '-o', 'extended'])

    def test_zero_players_needs_no_privileged_command(self):
        with patch.object(reader.subprocess, 'run') as run:
            result = reader.read_connections({'gamePort': 31015, 'serverIps': ['100.64.0.10'], 'peers': []})
        run.assert_not_called()
        self.assertEqual(result['Matches'], [])


if __name__ == '__main__':
    unittest.main()
