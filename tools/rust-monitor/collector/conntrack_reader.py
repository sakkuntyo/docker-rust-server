"""Read-only UDP NAT attribution; only requested player endpoints are returned."""
import datetime as dt
import getpass
import ipaddress
import json
import re
import shutil
import subprocess


def endpoint(value):
    if not isinstance(value, str):
        return None
    if value.startswith('[') and ']:' in value:
        host, port = value[1:].split(']:', 1)
    elif value.count(':') == 1:
        host, port = value.split(':')
    else:
        return None
    try:
        address = ipaddress.ip_address(host)
        number = int(port)
        if 1 <= number <= 65535:
            return str(address), number
    except ValueError:
        pass
    return None


def attribute_connections(text, server_ips, game_port, peers):
    servers = {str(ipaddress.ip_address(value)) for value in server_ips}
    requested = {value: endpoint(value) for value in peers}
    found = {value: set() for value in peers}
    for line in text.splitlines():
        if not re.search(r'\budp\s+17\s+\d+\s', line) or '[ASSURED]' not in line:
            continue
        fields = {key: re.findall(r'\b' + key + r'=([^\s]+)', line) for key in ('src', 'dst', 'sport', 'dport')}
        if any(len(values) != 2 for values in fields.values()):
            continue
        try:
            original_ip = str(ipaddress.ip_address(fields['src'][0]))
            reply_src = str(ipaddress.ip_address(fields['src'][1]))
            reply_dst = str(ipaddress.ip_address(fields['dst'][1]))
            reply_sport, reply_dport = int(fields['sport'][1]), int(fields['dport'][1])
        except ValueError:
            continue
        if reply_src not in servers or reply_sport != game_port:
            continue
        for value, peer in requested.items():
            # Match the post-NAT IP AND port seen by Rust, not original sport:
            # NAT can rewrite ports when clients use the same source port.
            if peer == (reply_dst, reply_dport):
                found[value].add(original_ip)
    return [{'Address': address, 'Ip': next(iter(ips)) if len(ips) == 1 else '',
             'Status': 'verified' if len(ips) == 1 else 'ambiguous' if ips else 'not_found'} for address, ips in found.items()]


def read_connections(request):
    port = request.get('gamePort')
    servers, peers = request.get('serverIps', []), request.get('peers', [])
    if type(port) is not int or not 1 <= port <= 65535 or not 1 <= len(servers) <= 8 or len(peers) > 2000:
        raise ValueError('Invalid connection scope')
    for value in servers:
        ipaddress.ip_address(value)
    if any(endpoint(value) is None for value in peers):
        raise ValueError('Invalid peer endpoint')
    now = dt.datetime.now(dt.timezone.utc).isoformat().replace('+00:00', 'Z')
    if not peers:
        return {'CheckedAt': now, 'Status': 'ok', 'Matches': []}
    binary = shutil.which('conntrack')
    if binary is None:
        # Some non-login SSH environments omit /usr/sbin from PATH.
        from pathlib import Path
        binary = next((p for p in ('/usr/sbin/conntrack', '/sbin/conntrack') if Path(p).is_file()), None)
    if binary is None:
        return {'CheckedAt': now, 'Status': 'tool_missing', 'Matches': []}
    outputs = []
    try:
        for address in sorted(set(servers)):
            family = 'ipv6' if ipaddress.ip_address(address).version == 6 else 'ipv4'
            result = subprocess.run(['sudo', '-n', binary, '-L', '-p', 'udp', '-f', family,
                                     '--reply-src', address, '--reply-port-src', str(port), '-o', 'extended'],
                                    capture_output=True, text=True, timeout=8)
            if result.returncode:
                return {'CheckedAt': now, 'Status': 'unavailable', 'Matches': []}
            outputs.append(result.stdout)
    except (OSError, subprocess.SubprocessError):
        return {'CheckedAt': now, 'Status': 'unavailable', 'Matches': []}
    return {'CheckedAt': dt.datetime.now(dt.timezone.utc).isoformat().replace('+00:00', 'Z'), 'Status': 'ok',
            'Matches': attribute_connections('\n'.join(outputs), servers, port, peers)}


def read_ip_route(name, docker_run):
    try:
        raw = docker_run('exec', name, 'sh', '-lc',
                     'printf "%s\\n" "${ENV_SERVER_PORT:-}"; tailscale status --json', timeout=10)
        port, status = raw.split('\n', 1)
        status = json.loads(status)
        active = [p for p in status.get('Peer', {}).values() if p.get('ExitNode')]
        if len(active) != 1 or not active[0].get('Online'):
            return {'Reason': '使用中の中継サーバーを確認できません'}
        node = active[0]
        return {'Host': node['HostName'], 'User': getpass.getuser(), 'GamePort': int(port),
                'ServerIps': status.get('Self', {}).get('TailscaleIPs', []), 'GatewayIps': node.get('TailscaleIPs', [])}
    except (ValueError, KeyError, RuntimeError, OSError, subprocess.SubprocessError):
        return {'Reason': '通信経路を取得できません'}


def read_presence(name, docker_run):
    script = r'''
set -eu
current=$(timeout -k 1s 8s rcon -t web -T 5s -a "127.0.0.1:${ENV_RCON_PORT}" -p "$ENV_RCON_PASSWD" playerlist 2>/dev/null)
printf '%s' "$current" | jq -e 'if type == "array" then {PresenceAvailable:true,Players:[.[] | {SteamId:(.SteamID|tostring),Address:.Address,ConnectionSeconds:.ConnectedSeconds}]} else error("not array") end'
'''
    try:
        result = json.loads(docker_run('exec', name, 'sh', '-lc', script, timeout=12))
        result['CheckedAt'] = dt.datetime.now(dt.timezone.utc).isoformat().replace('+00:00', 'Z')
        return result
    except (ValueError, RuntimeError, OSError, subprocess.SubprocessError):
        return {'PresenceAvailable': False, 'Players': []}
