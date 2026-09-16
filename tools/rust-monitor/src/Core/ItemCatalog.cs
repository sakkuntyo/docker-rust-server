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
        if (Items.Value.TryGetValue(item.ItemId.ToString(), out var definition))
        {
            if (item.Name.Length == 0) item.Name = definition.Name;
            if (item.ShortName.Length == 0) item.ShortName = definition.ShortName;
        }
        if (item.Name.Length == 0) item.Name = item.ItemId != 0 ? "アイテム ID " + item.ItemId : item.ShortName;
    }
}
