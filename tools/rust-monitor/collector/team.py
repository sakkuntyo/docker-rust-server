"""Read the selected player's native Rust team; no plugin or settings changes."""
import datetime as dt
import json
import re
import subprocess


def parse_team(raw, steamid):
    if raw.strip() == 'Player is not in a team':
        return dict(State='none', Members=[])
    if raw.strip() == 'Player not found':
        return dict(State='unavailable', Members=[])
    if len(raw) > 200_000:
        raise ValueError('Team response too large')
    rows = json.loads(raw)
    if not isinstance(rows, list) or not 1 <= len(rows) <= 128:
        raise ValueError('Invalid team')
    members = []
    for row in rows:
        if (not isinstance(row, dict) or set(row) != {'steamID', 'username', 'online', 'leader'} or
                not isinstance(row['steamID'], str) or not re.fullmatch(r'[0-9]{17}', row['steamID']) or
                int(row['steamID']) < 70000000000000000 or not isinstance(row['username'], str) or len(row['username']) > 512 or
                row['online'] not in ('x', '') or row['leader'] not in ('x', '')):
            raise ValueError('Invalid team member')
        members.append(dict(SteamId=row['steamID'], Name=row['username'], Online=row['online'] == 'x', Leader=row['leader'] == 'x'))
    if len({m['SteamId'] for m in members}) != len(members) or not any(m['SteamId'] == steamid for m in members):
        raise ValueError('Team does not match selected player')
    return dict(State='members', Members=members)


def team_request(request):
    container, steamid = request.get('container'), request.get('steamid')
    if (request.get('mode') != 'team' or not isinstance(container, str) or not re.fullmatch(r'rust-[A-Za-z0-9][A-Za-z0-9_.-]*', container) or
            not isinstance(steamid, str) or not re.fullmatch(r'[0-9]{17}', steamid) or int(steamid) < 70000000000000000):
        return {'Error': 'パーティのサーバーとSteam IDを確認してください。'}
    script = '''
if [ -z "${ENV_RCON_PORT:-}" ] || [ -z "${ENV_RCON_PASSWD:-}" ]; then exit 2; fi
exec timeout -k 1s 8s rcon -t web -T 6s -a "127.0.0.1:${ENV_RCON_PORT}" -p "$ENV_RCON_PASSWD" -- "$1"
'''
    try:
        result = subprocess.run(['sudo', '-n', 'docker', 'exec', container, 'sh', '-lc', script, 'rust-monitor-team',
                                 'global.teaminfo ' + steamid + ' --json'], capture_output=True, text=True, encoding='utf-8', timeout=12)
        if result.returncode:
            raise RuntimeError('Team command failed')
        return dict(SteamId=steamid, CheckedAt=dt.datetime.now(dt.timezone.utc).isoformat(), **parse_team(result.stdout, steamid))
    except (ValueError, RuntimeError, OSError, subprocess.TimeoutExpired):
        return {'Error': 'パーティを取得できません。SSH / RCONの接続状態を確認してください。'}


if __name__ == '__main__':
    print(json.dumps(team_request(globals().get('REQUEST', {})), ensure_ascii=False))
