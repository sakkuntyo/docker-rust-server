"""Explicit admin actions. Provision the bundled helper; dispatch mutation once."""
import base64
import hashlib
import json
import re
import subprocess
import time

PROTOCOL = 'rust-monitor-admin/1'
RCON_SH = '''
if [ -z "${ENV_RCON_PORT:-}" ] || [ -z "${ENV_RCON_PASSWD:-}" ]; then exit 2; fi
exec timeout -k 1s 8s rcon -t web -T 6s -a "127.0.0.1:${ENV_RCON_PORT}" -p "$ENV_RCON_PASSWD" -- "$1"
'''
INSTALL_SH = '''
set -eu
dir=/root/rustserver/oxide/plugins
test -d "$dir"
test -f /root/rustserver/RustDedicated_Data/Managed/Oxide.Rust.dll
dest="$dir/RustMonitorAdmin.cs"
if [ ! -e "$dest" ]; then
  tmp=$(mktemp "$dir/.rust-monitor-admin.XXXXXX")
  trap 'rm -f "$tmp"' EXIT
  cat > "$tmp"
  chmod 644 "$tmp"
  ln "$tmp" "$dest" || test -f "$dest"
fi
sha256sum "$dest"
'''


def run(container, script, *args, data=None):
    command = ['sudo', '-n', 'docker', 'exec'] + (['-i'] if data is not None else [])
    result = subprocess.run(command + [container, 'sh', '-lc', script, 'rust-monitor-admin', *args],
                            input=data, capture_output=True, text=True, encoding='utf-8', timeout=15)
    if result.returncode:
        raise RuntimeError('Command unavailable')
    return result.stdout.strip()


def ensure_helper(container, source):
    if not isinstance(source, str) or not source.startswith('using System;') or len(source) > 100000:
        raise ValueError('Invalid bundled helper')
    # Existing custom/different helper files are never overwritten automatically.
    installed = run(container, INSTALL_SH, data=source)
    if installed.split()[0] != hashlib.sha256(source.encode('utf-8')).hexdigest():
        raise ValueError('Different helper installed')
    for attempt in range(8):
        try:
            ping = json.loads(run(container, RCON_SH, 'rustmonitoradmin.ping'))
            if isinstance(ping, dict) and ping.get('Protocol') == PROTOCOL:
                return
        except (ValueError, RuntimeError, subprocess.TimeoutExpired):
            pass
        if attempt < 7:
            time.sleep(1)
    raise RuntimeError('Helper did not load')


def valid_item(item, depth=0):
    return (isinstance(item, dict) and depth <= 6 and
            isinstance(item.get('Uid'), str) and re.fullmatch(r'[0-9]{1,20}', item['Uid']) and 0 < int(item['Uid']) < 2**64 and
            type(item.get('ItemId')) is int and -2**31 <= item['ItemId'] < 2**31 and item['ItemId'] != 0 and
            type(item.get('Slot')) is int and 0 <= item['Slot'] <= 1024 and
            type(item.get('Amount')) is int and 0 < item['Amount'] <= 2**31-1 and
            isinstance(item.get('Contents'), list) and len(item['Contents']) <= 64 and all(valid_item(i, depth+1) for i in item['Contents']))


def moderation_request(request, source):
    container, action = request.get('container'), request.get('action')
    rejected = {'State': 'rejected', 'Message': '操作内容を確認できません。変更していません。'}
    if (not isinstance(container, str) or not re.fullmatch(r'rust-[A-Za-z0-9][A-Za-z0-9_.-]*', container) or
            not isinstance(action, dict) or action.get('Action') not in ('delete', 'ban') or
            not isinstance(action.get('RequestId'), str) or not re.fullmatch(r'[0-9a-f]{32}', action['RequestId']) or
            not isinstance(action.get('SteamId'), str) or not re.fullmatch(r'[0-9]{17}', action['SteamId']) or
            int(action['SteamId']) < 70000000000000000):
        return rejected
    if action['Action'] == 'delete':
        if (not isinstance(action.get('WipeId'), str) or not re.fullmatch(r'save:[0-9]+:[A-Za-z0-9-]+', action['WipeId']) or
                not valid_item(action.get('Item')) or action['Item'].get('Container') not in ('main', 'belt', 'wear')):
            return rejected
    else:
        for key, limit in [('Name', 256), ('Reason', 200)]:
            value = action.get(key)
            if not isinstance(value, str) or not value.strip() or len(value) > limit or any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in value):
                return rejected
    try:
        encoded = base64.b64encode(json.dumps(action, ensure_ascii=False, allow_nan=False).encode('utf-8')).decode('ascii')
    except (TypeError, ValueError):
        return rejected
    if len(encoded) > 48000:
        return rejected
    try:
        ensure_helper(container, source)
    except (ValueError, IndexError, RuntimeError, subprocess.TimeoutExpired, OSError):
        return {'State': 'rejected', 'Message': '管理プラグインを準備できません。操作は送信していません。uMod/Oxideの稼働状態・RustMonitorAdmin.csの既存ファイル・コンパイルログを確認してください。'}
    try:
        response = json.loads(run(container, RCON_SH, 'rustmonitoradmin.execute ' + encoded))
        if (isinstance(response, dict) and response.get('Protocol') == PROTOCOL and response.get('RequestId') == action['RequestId'] and
                response.get('State') in ('accepted', 'rejected', 'unknown') and isinstance(response.get('Message'), str)):
            return response
    except (ValueError, RuntimeError, subprocess.TimeoutExpired, OSError):
        pass
    return {'State': 'unknown', 'Message': '操作結果を確認できません。自動再送しません。ゲーム内の所持品またはサーバーのBAN一覧を確認してください。'}


if __name__ == '__main__':
    print(json.dumps(moderation_request(globals().get('REQUEST', {}), globals().get('ADMIN_PLUGIN', '')), ensure_ascii=False))
