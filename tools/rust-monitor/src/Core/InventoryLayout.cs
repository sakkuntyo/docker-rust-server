namespace RustMonitor.Core;

public sealed record InventorySlot(int Index, ItemRecord? Item, bool PositionKnown = true);

public static class InventoryLayout
{
    public static List<InventorySlot> Slots(IEnumerable<ItemRecord> items, string container)
    {
        var minimum = container switch { "main" => 24, "belt" => 6, "wear" => 8, _ => 0 };
        var records = items.Where(i => i.Container == container).ToList();
        var largest = records.Where(i => i.Slot is >= 0 and < 96).Select(i => i.Slot + 1).DefaultIfEmpty(0).Max();
        var capacity = Math.Max(minimum, largest);
        var slots = Enumerable.Range(0, capacity).Select(i => new InventorySlot(i, null)).ToList();
        foreach (var item in records)
        {
            if (item.Slot >= 0 && item.Slot < capacity && slots[item.Slot].Item == null)
                slots[item.Slot] = new InventorySlot(item.Slot, item);
            else slots.Add(new InventorySlot(item.Slot, item, false));
        }
        return slots;
    }
    public static double Durability(ItemRecord item) => float.IsFinite(item.Condition) && float.IsFinite(item.MaxCondition) && item.MaxCondition > 0
        ? Math.Clamp((double)item.Condition / item.MaxCondition, 0, 1) : 0;
}
