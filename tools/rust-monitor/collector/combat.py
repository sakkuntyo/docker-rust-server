"""Read one player's standard combat log. No server settings are changed."""
import datetime as dt
import hashlib
import json
from pathlib import Path
import re
import subprocess
import time


def combat_epoch(container):
    result = subprocess.run(['sudo', '-n', 'docker', 'top', container, '-eo', 'pid,comm,lstart'],
                            capture_output=True, text=True, timeout=8)
    rows = [line.strip() for line in result.stdout.splitlines()
            if len(line.split()) >= 3 and line.split()[1] == 'RustDedicated']
    if result.returncode or len(rows) != 1:
        raise RuntimeError('Server process unavailable')
    boot = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    return hashlib.sha256((boot + '\n' + ' '.join(rows[0].split())).encode()).hexdigest()


def combat_rcon(container, steamid):
    script = '''
if [ -z "${ENV_RCON_PORT:-}" ] || [ -z "${ENV_RCON_PASSWD:-}" ]; then exit 2; fi
exec timeout -k 1s 8s rcon -t web -T 6s -a "127.0.0.1:${ENV_RCON_PORT}" -p "$ENV_RCON_PASSWD" -- "$1"
'''
    result = subprocess.run(['sudo', '-n', 'docker', 'exec', container, 'sh', '-lc', script,
                             'rust-monitor-combat', 'server.combatlog ' + steamid + ' --json'],
                            capture_output=True, text=True, encoding='utf-8', timeout=12)
    if result.returncode:
        raise RuntimeError('RCON unavailable')
    return result.stdout


def parse_combat(raw):
    if len(raw) > 2_000_000:
        raise ValueError('Combat response too large')
    # Rust's JSON repeats the key "id": first attacker, then target.
    # A normal dictionary would silently discard the attacker's entity ID.
    class ObjectPairs(list):
        pass
    rows = json.loads(raw, object_pairs_hook=ObjectPairs)
    if type(rows) is not list or len(rows) > 5000:
        raise ValueError('Invalid combat response')
    columns = ['time', 'attacker', 'id', 'target', 'id', 'weapon', 'ammo', 'area', 'distance',
               'old_hp', 'new_hp', 'info', 'hits', 'integrity', 'travel', 'mismatch', 'desync']
    result = []
    for row in rows:
        if not isinstance(row, ObjectPairs) or len(row) != len(columns):
            raise ValueError('Invalid combat row')
        if any(not isinstance(pair, tuple) or len(pair) != 2 or pair[0] != name or
               not isinstance(pair[1], str) or len(pair[1]) > 4096 for pair, name in zip(row, columns)):
            raise ValueError('Invalid combat fields')
        values = [pair[1] for pair in row]
        if not re.fullmatch(r'[0-9]+(?:[.,][0-9]+)?s', values[0]):
            raise ValueError('Invalid combat age')
        age = float(values[0][:-1].replace(',', '.'))
        if not 0 <= age <= 315_360_000:
            raise ValueError('Invalid combat age')
        result.append({'AgeSeconds': age, 'Fields': values[1:]})
    return result


def combat_request(request):
    container, steamid = request.get('container', ''), request.get('steamid', '')
    if (request.get('mode') != 'combat' or not isinstance(container, str) or
            not re.fullmatch(r'rust-[A-Za-z0-9][A-Za-z0-9_.-]*', container) or
            not isinstance(steamid, str) or not re.fullmatch(r'[0-9]{17}', steamid)):
        return {'Error': 'コンバットログのサーバーとSteam IDを確認してください。'}
    try:
        epoch = combat_epoch(container)
        start, utc = time.monotonic(), time.time()
        raw = combat_rcon(container, steamid)
        finish = time.monotonic()
        if combat_epoch(container) != epoch:
            return {'Error': '取得中にサーバーが再起動しました。次の更新で再取得します。'}
        result = {'SteamId': steamid, 'Epoch': epoch, 'SampleSeconds': (start + finish) / 2,
                  'UncertaintySeconds': (finish - start) / 2 + 0.25,
                  'CheckedAt': dt.datetime.fromtimestamp(utc + (finish - start) / 2, dt.timezone.utc).isoformat()}
        if raw.strip().lower() == 'invalid player':
            return dict(result, Available=False, Rows=[], Reason='サーバー上に対象のプレイヤーが見つかりません。保存済みのログを表示します。')
        if not raw.strip():
            return {'Error': 'サーバーからコンバットログの応答がありません。次の更新で再取得します。'}
        return dict(result, Available=True, Rows=parse_combat(raw), Reason='')
    except ValueError:
        return {'Error': 'サーバーのコンバットログ形式を読み取れません。取得済みのログは保持します。'}
    except (RuntimeError, subprocess.TimeoutExpired, OSError):
        return {'Error': 'コンバットログを取得できません。SSH / RCONの接続状態を確認してください。'}


if __name__ == '__main__':
    print(json.dumps(combat_request(globals().get('REQUEST', {})), ensure_ascii=False))
