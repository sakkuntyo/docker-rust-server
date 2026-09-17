"""Read player positions and inventories from Rust save 288; never modify it.

Only player entities are returned. World storage, corpses, locks and buildings
are ignored. Field numbers were checked against the running server's schema.
"""
import io
import json
import math
import struct
import subprocess


class SaveReadError(ValueError):
    pass


def read_varint(stream):
    value = 0
    for shift in range(0, 70, 7):
        part = stream.read(1)
        if not part:
            raise SaveReadError("Truncated varint")
        value |= (part[0] & 127) << shift
        if part[0] < 128:
            return value
    raise SaveReadError("Invalid varint")


def read_exact(stream, size):
    if size < 0 or size > 16 * 1024 * 1024:
        raise SaveReadError("Invalid record length")
    data = stream.read(size)
    if len(data) != size:
        raise SaveReadError("Truncated record")
    return data


def proto_fields(data):
    stream = io.BytesIO(data)
    result = {}
    while stream.tell() < len(data):
        key = read_varint(stream)
        number, kind = key >> 3, key & 7
        if number == 0:
            raise SaveReadError("Invalid field")
        if kind == 0:
            value = read_varint(stream)
        elif kind == 1:
            value = read_exact(stream, 8)
        elif kind == 2:
            value = read_exact(stream, read_varint(stream))
        elif kind == 5:
            value = read_exact(stream, 4)
        else:
            raise SaveReadError("Unsupported wire type")
        result.setdefault(number, []).append((kind, value))
    return result


def scalar(fields, number, kind, default=None):
    values = fields.get(number)
    if not values:
        return default
    if len(values) != 1 or values[0][0] != kind:
        raise SaveReadError("Unexpected field type")
    return values[0][1]


def float_field(fields, number):
    value = struct.unpack('<f', scalar(fields, number, 5, b'\0\0\0\0'))[0]
    if not math.isfinite(value):
        raise SaveReadError("Non-finite float")
    return value


def saved_items(data, container, depth=0):
    if depth > 6:
        raise SaveReadError("Inventory nesting too deep")
    result = []
    for kind, raw in proto_fields(data).get(100, []):
        if kind != 2 or len(result) >= 1024:
            raise SaveReadError("Invalid items")
        item = proto_fields(raw)
        itemid = scalar(item, 2, 0, 0) & 0xffffffff
        if itemid >= 0x80000000:
            itemid -= 0x100000000
        condition = proto_fields(scalar(item, 11, 2, b''))
        contents = scalar(item, 100, 2)
        result.append({"Uid": str(scalar(item, 1, 0, 0)), "Container": container, "Slot": scalar(item, 3, 0, 0),
                       "ItemId": itemid, "Name": "", "ShortName": str(itemid),
                       "Amount": scalar(item, 4, 0, 0), "Skin": str(scalar(item, 16, 0, 0)),
                       "Condition": float_field(condition, 1), "MaxCondition": float_field(condition, 2),
                       "Ammo": scalar(item, 19, 0),
                       "Contents": saved_items(contents, "contents", depth + 1) if contents is not None else []})
    return result


def vector(data, default=(0.0, 0.0, 0.0)):
    if data is None:
        return default
    fields = proto_fields(data)
    return tuple(float_field(fields, n) for n in (1, 2, 3))


def saved_transform(entity):
    raw = scalar(entity, 2, 2)
    if raw is None:
        return None
    try:
        base = proto_fields(raw)
        parent = proto_fields(scalar(entity, 10, 2, b''))
        return (vector(scalar(base, 1, 2)), vector(scalar(base, 2, 2)),
                vector(scalar(base, 8, 2), (1.0, 1.0, 1.0)),
                scalar(parent, 1, 0, 0), scalar(parent, 2, 0, 0))
    except SaveReadError:
        return None


def world_position(transform, transforms):
    if transform is None:
        return None, '座標の記録を読み取れません'
    point, _, _, parent, bone = transform
    visited = set()
    while parent:
        # Bone transforms live in prefabs, not in the save. Do not mistake a
        # mounted player's local coordinates for world coordinates.
        if bone or parent in visited or len(visited) >= 32 or transforms.get(parent) is None:
            return None, '乗り物などに対する相対座標のため、位置を確定できません'
        visited.add(parent)
        pos, rot, scale, parent, bone = transforms[parent]
        x, y, z = (point[i] * scale[i] for i in range(3))
        rx, ry, rz = (math.radians(angle) for angle in rot)
        # Unity Euler angles apply Z, then X, then Y, in local space.
        x, y = math.cos(rz) * x - math.sin(rz) * y, math.sin(rz) * x + math.cos(rz) * y
        y, z = math.cos(rx) * y - math.sin(rx) * z, math.sin(rx) * y + math.cos(rx) * z
        x, z = math.cos(ry) * x + math.sin(ry) * z, -math.sin(ry) * x + math.cos(ry) * z
        point = (x + pos[0], y + pos[1], z + pos[2])
    if not all(math.isfinite(n) for n in point):
        return None, '座標の値が不正です'
    return dict(zip(('X', 'Y', 'Z'), point)), ''


def parse_save(stream):
    if read_exact(stream, 4) != b'SAVR':
        raise SaveReadError("Unsupported save header")
    extra = {}
    token = read_exact(stream, 1)
    if token == b'J':
        extra = json.loads(read_exact(stream, read_varint(stream)))
        token = read_exact(stream, 1)
    if token != b'D':
        raise SaveReadError("Missing save creation time")
    created = struct.unpack('<I', read_exact(stream, 4))[0]
    version = struct.unpack('<I', read_exact(stream, 4))[0]
    if version != 288:
        raise SaveReadError("Unsupported save version: " + str(version))
    players, transforms = {}, {}
    while True:
        length = stream.read(4)
        if not length:
            break
        if len(length) != 4:
            raise SaveReadError("Truncated entity length")
        entity = proto_fields(read_exact(stream, struct.unpack('<I', length)[0]))
        transform = saved_transform(entity)
        network = proto_fields(scalar(entity, 1, 2, b''))
        uid = scalar(network, 1, 0, 0)
        if uid:
            transforms[uid] = transform
        raw_player = scalar(entity, 3, 2)
        if raw_player is None:
            continue
        player = proto_fields(raw_player)
        steamid = scalar(player, 2, 0, 0)
        if not 76561197960265728 <= steamid <= 76561202255233023:
            continue
        inv = scalar(player, 3, 2)
        if inv is None:
            continue
        items = []
        inventory = proto_fields(inv)
        for number, label in ((1, 'main'), (2, 'belt'), (3, 'wear')):
            part = scalar(inventory, number, 2)
            # A missing section is not a verified empty inventory.
            if part is None:
                raise SaveReadError("Incomplete player inventory")
            items.extend(saved_items(part, label))
        players[str(steamid)] = {"Name": scalar(player, 1, 2, b'').decode('utf-8'), "Items": items,
                               '_transform': transform}
    for player in players.values():
        player['Position'], player['PositionReason'] = world_position(player.pop('_transform'), transforms)
    return {"Created": created, "Version": version, "SaveWipeId": extra.get('WipeId', ''), "Players": players}


def read_saved_players(container, season_start, season_end):
    command = r'''
set -eu
path=$(find /root/rustserver/server/serverdata1 -maxdepth 1 -type f -name '*.sav' -printf '%T@ %p\n' | sort -nr | head -n 1 | cut -d ' ' -f 2-)
test -n "$path"
size=$(stat -c %s "$path")
test "$size" -le 268435456
before=$(stat -c '%i|%s|%Y|%y|%z' "$path")
printf '%s\n' "$before"
cat "$path"
after=$(stat -c '%i|%s|%Y|%y|%z' "$path")
test "$before" = "$after"
'''
    # The save stays on the Docker host. Only parsed player data leaves it.
    proc = subprocess.run(['sudo', '-n', 'docker', 'exec', container, 'sh', '-lc', command],
                          capture_output=True, timeout=25)
    if proc.returncode:
        raise SaveReadError("Save unavailable or changed during read")
    header, data = proc.stdout.split(b'\n', 1)
    _, size, modified, _, _ = header.decode('ascii').split('|')
    if len(data) != int(size):
        raise SaveReadError("Save size changed")
    result = parse_save(io.BytesIO(data))
    if not season_start or not season_end or not season_start <= result['Created'] < season_end:
        raise SaveReadError("Save belongs to another season")
    result['SavedAt'] = int(modified)
    return result
