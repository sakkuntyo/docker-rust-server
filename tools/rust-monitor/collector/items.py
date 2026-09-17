"""Read native item definitions; give only on an explicit, validated request.

The give command is dispatched once. An uncertain result is never retried.
"""
import json
import re
import subprocess

CATALOG_SH = '''
test -d /root/rustserver/Bundles/items || exit 2
exec jq -s '[.[] | {ItemId:.itemid,Name:.Name,ShortName:.shortname,Category:.Category,StackSize:.stackable}]' /root/rustserver/Bundles/items/*.json
'''
RCON_SH = '''
if [ -z "${ENV_RCON_PORT:-}" ] || [ -z "${ENV_RCON_PASSWD:-}" ]; then exit 2; fi
exec timeout -k 1s 8s rcon -t web -T 6s -a "127.0.0.1:${ENV_RCON_PORT}" -p "$ENV_RCON_PASSWD" -- "$1"
'''


def shortname_valid(value):
    return isinstance(value, str) and value == value.strip() and re.fullmatch(r'[a-z0-9][a-z0-9._ -]{0,95}', value)


def run_container(container, script, *args):
    result = subprocess.run(['sudo', '-n', 'docker', 'exec', container, 'sh', '-lc', script, 'rust-monitor-items', *args],
                            capture_output=True, text=True, encoding='utf-8', timeout=15)
    if result.returncode:
        raise RuntimeError('Container command unavailable')
    return result.stdout


def read_catalog(container):
    raw = run_container(container, CATALOG_SH)
    if len(raw) > 5_000_000:
        raise ValueError('Catalog too large')
    rows = json.loads(raw)
    if not isinstance(rows, list) or not 1 <= len(rows) <= 10000:
        raise ValueError('Invalid catalog')
    items = {}
    for row in rows:
        if (not isinstance(row, dict) or type(row.get('ItemId')) is not int or not -2147483648 <= row['ItemId'] <= 2147483647 or
                row['ItemId'] == 0 or not shortname_valid(row.get('ShortName')) or
                not isinstance(row.get('Name'), str) or not 1 <= len(row['Name']) <= 256 or
                not isinstance(row.get('Category'), str) or len(row['Category']) > 64 or
                type(row.get('StackSize')) is not int or not 1 <= row['StackSize'] <= 2147483647):
            raise ValueError('Invalid item definition')
        old = items.get(row['ItemId'])
        # Some installs retain legacy space-separated car aliases alongside their replacement.
        if old is None or (' ' in old['ShortName'] and ' ' not in row['ShortName']):
            items[row['ItemId']] = row
    return list(items.values())


def online(container, steamid):
    raw = run_container(container, RCON_SH, 'playerlist')
    if len(raw) > 2_000_000:
        raise ValueError('Player list too large')
    players = json.loads(raw)
    if not isinstance(players, list) or any(not isinstance(p, dict) or not str(p.get('SteamID', '')).isdigit() for p in players):
        raise ValueError('Invalid player list')
    return any(str(p['SteamID']) == steamid for p in players)


def item_request(request):
    container, steamid, mode = request.get('container'), request.get('steamid'), request.get('mode')
    if (mode not in ('itemmenu', 'giveitem') or not isinstance(container, str) or
            not re.fullmatch(r'rust-[A-Za-z0-9][A-Za-z0-9_.-]*', container) or
            not isinstance(steamid, str) or not re.fullmatch(r'[0-9]{17}', steamid)):
        return {'Error': '付与先のサーバーとSteam IDを確認してください。'}
    if mode == 'giveitem' and (type(request.get('itemid')) is not int or request['itemid'] == 0 or
            not shortname_valid(request.get('shortname')) or type(request.get('amount')) is not int or not 1 <= request['amount'] <= 10000):
        return {'State': 'rejected', 'Message': 'アイテムと数量（1～10,000）を確認してください。'}
    try:
        items = read_catalog(container)
        connected = online(container, steamid)
        if mode == 'itemmenu':
            return {'SteamId': steamid, 'Online': connected, 'Items': items}
        if not connected:
            return {'State': 'rejected', 'Message': '対象プレイヤーはオフラインです。付与していません。'}
        if not any(i['ItemId'] == request['itemid'] and i['ShortName'] == request['shortname'] for i in items):
            return {'State': 'rejected', 'Message': '対象サーバーに一致するアイテムがありません。一覧を再取得してください。'}
    except (ValueError, RuntimeError, subprocess.TimeoutExpired, OSError):
        if mode == 'giveitem':
            return {'State': 'rejected', 'Message': 'アイテム一覧または接続状態を確認できません。付与は実行していません。'}
        return {'Error': 'アイテム一覧または接続状態を確認できません。付与は実行していません。'}
    try:
        # Steam ID and quantity are digits, shortname excludes quotes/control chars.
        # RCON receives a single command argument; the shell evaluates only fixed code.
        command = f'inventory.giveto {steamid} "{request["shortname"]}" {request["amount"]}'
        response = run_container(container, RCON_SH, command).strip()
        failures = {"Couldn't find player!": '対象プレイヤーが見つかりません。', 'Invalid Item!': 'サーバーがこのアイテムを受け付けませんでした。',
                    "Couldn't give item (inventory full?)": '所持品に空きがないため付与できませんでした。'}
        if response in failures:
            return {'State': 'rejected', 'Message': failures[response]}
        if not response:
            return {'State': 'accepted', 'Message': '付与しました。所持品表示への反映は次のサーバー保存後です。'}
    except (RuntimeError, subprocess.TimeoutExpired, OSError):
        pass
    return {'State': 'unknown', 'Message': '付与結果を確認できません。自動再送しません。再実行する前にゲーム内の所持品を確認してください。'}


if __name__ == '__main__':
    print(json.dumps(item_request(globals().get('REQUEST', {})), ensure_ascii=False))
