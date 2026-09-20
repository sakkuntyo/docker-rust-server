namespace RustMonitor.Core;

public static class InventoryTree
{
    public static bool RemoveWhere(List<ItemRecord> items, Func<ItemRecord, bool> predicate, int depth = 0)
    {
        if (depth > 6) return false;
        var changed = items.RemoveAll(i => predicate(i)) > 0;
        foreach (var item in items) changed |= RemoveWhere(item.Contents, predicate, depth + 1);
        return changed;
    }
}
