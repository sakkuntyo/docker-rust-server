using System.IO;

namespace RustMonitor.Core;

public static class ItemCatalog
{
    private static readonly Lazy<Dictionary<string, ItemRecord>> Items = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "items.json");
        return File.Exists(path) ? Wire.Read<Dictionary<string, ItemRecord>>(File.ReadAllText(path)) : [];
    });
    public static void Name(ItemRecord item)
    {
        if (item.Name.Length > 0) return;
        if (Items.Value.TryGetValue(item.ItemId.ToString(), out var definition))
        { item.Name = definition.Name; item.ShortName = definition.ShortName; }
        else item.Name = item.ItemId != 0 ? "アイテム ID " + item.ItemId : item.ShortName;
    }
}
