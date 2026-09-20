"""Explicit admin actions. Provision the bundled helper; dispatch mutation once."""
import base64
import hashlib
import json
import datetime
import re
import subprocess
import time

PROTOCOL = 'rust-monitor-admin/1'
HELPER_VERSION = '0.1.3'
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
tmp=$(mktemp "$dir/.rust-monitor-admin.XXXXXX")
trap 'rm -f "$tmp"' EXIT
cat > "$tmp"
chmod 644 "$tmp"
if [ ! -e "$dest" ]; then
  ln "$tmp" "$dest" || test -f "$dest"
else
  oldhash=$(sha256sum "$dest" | cut -d ' ' -f 1)
  backup=""
  case "$oldhash" in
    c58cf9b48ef80a5c829908b98f0f5927b2f89c9eaaac0ba4060fcb9e83f300b8) backup="$dest.0.1.0.bak" ;;
    7bf026c96d82a1f6fb6ba2f97c66aff77250cb09fe713642db71c9da33ef0815) backup="$dest.0.1.1.bak" ;;
    cec87e671c9825e035f39b76a785b8a723da5499b0b56d888e3b6585d29d2467) backup="$dest.0.1.2.bak" ;;
  esac
  if [ -n "$backup" ]; then
    ln "$dest" "$backup" || test "$(sha256sum "$backup" | cut -d ' ' -f 1)" = "$oldhash"
    test "$(sha256sum "$dest" | cut -d ' ' -f 1)" = "$oldhash"
    mv "$tmp" "$dest"
  fi
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
    # Custom / unknown helper versions are never overwritten automatically.
    installed = run(container, INSTALL_SH, data=source)
    if installed.split()[0] != hashlib.sha256(source.encode('utf-8')).hexdigest():
        raise ValueError('Different helper installed')
    reloaded = False
    for attempt in range(8):
        try:
            ping = json.loads(run(container, RCON_SH, 'rustmonitoradmin.ping'))
            if isinstance(ping, dict) and ping.get('Protocol') == PROTOCOL and ping.get('Version') == HELPER_VERSION:
                return
            if isinstance(ping, dict) and ping.get('Protocol') == PROTOCOL and not reloaded:
                # An atomic source replacement is not picked up by every Oxide watcher.
                reloaded = True
                run(container, RCON_SH, 'oxide.reload RustMonitorAdmin')
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


def result_response(raw, request_id):
    response = json.loads(raw)
    if (isinstance(response, dict) and response.get('Protocol') == PROTOCOL and response.get('RequestId') == request_id and
            response.get('State') in ('accepted', 'rejected', 'unknown') and isinstance(response.get('Message'), str)):
        return response
    raise ValueError('Not the action result')


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
        parents = action.get('Parents', [])
        if (not isinstance(action.get('WipeId'), str) or not re.fullmatch(r'save:[0-9]+:[A-Za-z0-9-]+', action['WipeId']) or
                not valid_item(action.get('Item')) or action['Item'].get('Container') not in ('main', 'belt', 'wear') or
                not isinstance(parents, list) or len(parents) > 6 or any(not isinstance(p, dict) or
                    not isinstance(p.get('Uid'), str) or not re.fullmatch(r'[0-9]{1,20}', p['Uid']) or not 0 < int(p['Uid']) < 2**64 or
                    type(p.get('ItemId')) is not int or not -2**31 <= p['ItemId'] < 2**31 or p['ItemId'] == 0 or
                    type(p.get('Slot')) is not int or not 0 <= p['Slot'] <= 1024 for p in parents) or
                len({p['Uid'] for p in parents} | {action['Item']['Uid']}) != len(parents) + 1):
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
        command = 'rustmonitoradmin.execute ' + encoded
        # The installed rcon CLI caps commands at 1,000 bytes. Stage large item
        # descriptions in bounded chunks; only the final commit mutates the game.
        if len(command) > 1000:
            chunks = [encoded[i:i+600] for i in range(0, len(encoded), 600)]
            for index, chunk in enumerate(chunks):
                reply = json.loads(run(container, RCON_SH, f'rustmonitoradmin.prepare {action["RequestId"]} {index} {len(chunks)} {chunk}'))
                if (not isinstance(reply, dict) or reply.get('Protocol') != PROTOCOL or reply.get('RequestId') != action['RequestId'] or
                        type(reply.get('Prepared')) is not int or reply['Prepared'] != index):
                    raise ValueError('Prepare failed')
            command = 'rustmonitoradmin.commit ' + action['RequestId']
    except (ValueError, IndexError, RuntimeError, subprocess.TimeoutExpired, OSError):
        return {'State': 'rejected', 'Message': '管理プラグインを準備できません。操作は送信していません。uMod/Oxideの稼働状態・RustMonitorAdmin.csの既存ファイル・コンパイルログを確認してください。'}
    try:
        return result_response(run(container, RCON_SH, command), action['RequestId'])
    except (ValueError, RuntimeError, subprocess.TimeoutExpired, OSError):
        pass
    try:
        # Rust may send a log/kick message with the same RCON identifier first.
        # Read the already-recorded result, NEVER re-execute the mutation.
        return result_response(run(container, RCON_SH, 'rustmonitoradmin.result ' + action['RequestId']), action['RequestId'])
    except (ValueError, RuntimeError, subprocess.TimeoutExpired, OSError):
        pass
    return {'State': 'unknown', 'Message': '操作結果を確認できません。自動再送しません。ゲーム内の所持品またはサーバーのBAN一覧を確認してください。'}


def inventory_request(request, source):
    container, steamid, wipe = request.get('container'), request.get('steamid'), request.get('wipe')
    if (not isinstance(container, str) or not re.fullmatch(r'rust-[A-Za-z0-9][A-Za-z0-9_.-]*', container) or
            not isinstance(steamid, str) or not re.fullmatch(r'[0-9]{17}', steamid) or int(steamid) < 70000000000000000 or
            not isinstance(wipe, str) or not re.fullmatch(r'save:[0-9]+:[A-Za-z0-9-]+', wipe)):
        return {'Error': 'サーバー・プレイヤー・ワイプを確認してください。'}
    try:
        ensure_helper(container, source)
        reply = json.loads(run(container, RCON_SH, 'rustmonitoradmin.inventory ' + steamid))
        if not isinstance(reply, dict) or reply.get('Protocol') != PROTOCOL or reply.get('SteamId') != steamid:
            raise ValueError('Invalid inventory response')
        if reply.get('WipeId') != wipe:
            return {'Error': 'ワイプが変わりました。サーバーへ接続し直してください。'}
        if reply.get('Available') is not True:
            return {'Error': '現在の本人の所持品を取得できません。身体がない場合もあります。前回の表示を保持します。'}
        inventory = reply.get('Inventory')
        if (not isinstance(inventory, dict) or inventory.get('SteamId') != steamid or inventory.get('WipeId') != wipe or
                inventory.get('Source') != 'live' or inventory.get('Current') is not True or
                not isinstance(inventory.get('CapturedAt'), str) or not isinstance(inventory.get('Items'), list) or
                len(inventory['Items']) > 384 or any(not valid_item(i) or i.get('Container') not in ('main', 'belt', 'wear') for i in inventory['Items'])):
            raise ValueError('Invalid inventory snapshot')
        if datetime.datetime.fromisoformat(inventory['CapturedAt'].replace('Z', '+00:00')).tzinfo is None:
            raise ValueError('Missing capture timezone')
        return inventory
    except (ValueError, IndexError, RuntimeError, subprocess.TimeoutExpired, OSError):
        return {'Error': '現在の所持品を取得できません。SSH接続と管理プラグインを確認してください。前回の表示を保持します。'}


if __name__ == '__main__':
    request = globals().get('REQUEST', {})
    handler = inventory_request if request.get('mode') == 'inventory' else moderation_request
    print(json.dumps(handler(request, globals().get('ADMIN_PLUGIN', '')), ensure_ascii=False))
