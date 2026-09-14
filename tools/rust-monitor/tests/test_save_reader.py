import importlib.util
import io
from pathlib import Path
import struct
import unittest

spec = importlib.util.spec_from_file_location('save_reader', Path(__file__).parent.parent / 'collector' / 'save_reader.py')
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)


def varint(value):
    value &= (1 << 64) - 1
    result = bytearray()
    while value >= 128:
        result.append((value & 127) | 128)
        value >>= 7
    return bytes(result + bytes([value]))


def number(field, value):
    return varint(field << 3) + varint(value)


def message(field, value):
    return varint((field << 3) | 2) + varint(len(value)) + value


def vec(x, y, z):
    return b''.join(varint((n << 3) | 5) + struct.pack('<f', v) for n, v in enumerate((x, y, z), 1))


def save(entities, version=288):
    extra = b'{"WipeId":"one"}'
    result = b'SAVRJ' + varint(len(extra)) + extra + b'D' + struct.pack('<II', 1789171200, version)
    return result + b''.join(struct.pack('<I', len(entity)) + entity for entity in entities)


def player():
    condition = varint(13) + struct.pack('<f', 73.5) + varint(21) + struct.pack('<f', 100)
    child = message(100, number(2, 442289265) + number(4, 1))
    item = number(2, -151838493) + number(3, 2) + number(4, 1000) + message(11, condition) + message(100, child)
    inventory = message(1, message(100, item)) + message(2, b'') + message(3, b'')
    return message(3, message(1, '日本語'.encode()) + number(2, 76561198012345678) + message(3, inventory))


class SaveReaderTests(unittest.TestCase):
    def test_saved_world_coordinates_include_zero_and_negative_axes(self):
        entity = player() + message(2, message(1, vec(-350.25, 0, 620.5)))
        value = reader.parse_save(io.BytesIO(save([entity])))['Players']['76561198012345678']
        self.assertEqual(value['Position'], {'X': -350.25, 'Y': 0, 'Z': 620.5})
        self.assertEqual(value['PositionReason'], '')
        origin = reader.parse_save(io.BytesIO(save([player() + message(2, b'')])))['Players']['76561198012345678']
        self.assertEqual(origin['Position'], {'X': 0, 'Y': 0, 'Z': 0})

    def test_parent_transform_is_resolved_even_when_parent_is_saved_later(self):
        entity = player() + message(2, message(1, vec(0, 2, 5))) + message(10, number(1, 42))
        parent = message(1, number(1, 42)) + message(2, message(1, vec(100, 10, -50)) + message(2, vec(0, 90, 0)))
        value = reader.parse_save(io.BytesIO(save([entity, parent])))['Players']['76561198012345678']
        for key, expected in {'X': 105, 'Y': 12, 'Z': -50}.items():
            self.assertAlmostEqual(value['Position'][key], expected)

    def test_bones_missing_parents_and_cycles_never_become_world_coordinates(self):
        for parent in (number(1, 42), number(1, 42) + number(2, 7)):
            entity = player() + message(2, message(1, vec(0, 1, 0))) + message(10, parent)
            value = reader.parse_save(io.BytesIO(save([entity])))['Players']['76561198012345678']
            self.assertIsNone(value['Position'])
            self.assertTrue(value['PositionReason'])
            self.assertTrue(value['Items'])
        transform = ((0, 0, 0), (0, 0, 0), (1, 1, 1), 42, 0)
        self.assertIsNone(reader.world_position(transform, {42: transform})[0])

    def test_nested_parents_apply_scale_and_rotation_in_local_space(self):
        child = ((0, 0, 2), (0, 0, 0), (1, 1, 1), 42, 0)
        parents = {42: ((10, 0, 0), (0, 90, 0), (2, 2, 2), 43, 0),
                   43: ((100, 5, 0), (0, 90, 0), (1, 1, 1), 0, 0)}
        result, reason = reader.world_position(child, parents)
        self.assertEqual(reason, '')
        for axis, expected in {'X': 100, 'Y': 5, 'Z': -14}.items():
            self.assertAlmostEqual(result[axis], expected)

    def test_nonfinite_position_does_not_erase_inventory(self):
        entity = player() + message(2, message(1, vec(float('nan'), 0, 0)))
        value = reader.parse_save(io.BytesIO(save([entity])))['Players']['76561198012345678']
        self.assertIsNone(value['Position'])
        self.assertTrue(value['Items'])

    def test_player_inventory_ids_nested_contents_and_empty_sections(self):
        value = reader.parse_save(io.BytesIO(save([message(14, b'world storage'), player()])))
        self.assertEqual(len(value['Players']), 1)
        member = value['Players']['76561198012345678']
        self.assertEqual(member['Name'], '日本語')
        item = member['Items'][0]
        self.assertEqual((item['Container'], item['ItemId'], item['Amount'], item['Slot']), ('main', -151838493, 1000, 2))
        self.assertEqual(item['Condition'], 73.5)
        self.assertEqual(item['Contents'][0]['ItemId'], 442289265)

    def test_truncation_never_returns_partial_player_inventory(self):
        blob = save([player()])
        for cut in (1, 3, 8, len(blob) - 1):
            with self.assertRaises((reader.SaveReadError, ValueError)):
                reader.parse_save(io.BytesIO(blob[:cut]))

    def test_future_save_version_is_explicitly_unsupported(self):
        with self.assertRaises(reader.SaveReadError):
            reader.parse_save(io.BytesIO(save([player()], 999)))

    def test_missing_inventory_section_is_not_empty(self):
        p = message(3, number(2, 76561198012345678) + message(3, message(1, b'')))
        with self.assertRaises(reader.SaveReadError):
            reader.parse_save(io.BytesIO(save([p])))

    def test_nonplayer_entities_and_npc_ids_are_excluded(self):
        self.assertEqual(reader.parse_save(io.BytesIO(save([message(8, b'corpse'), message(3, number(2, 42))])))['Players'], {})


if __name__ == '__main__':
    unittest.main()
