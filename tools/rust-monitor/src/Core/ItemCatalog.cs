using System.IO;

namespace RustMonitor.Core;

public static class ItemCatalog
{
    // Retain names for historical inventories without offering retired items in the menu.
    private static readonly Dictionary<string, CatalogItem> Historical = new()
    {
        ["-1759188988"] = new() { Name = "Hab Repair", ShortName = "habrepair" },
        ["363467698"] = new() { Name = "Chocolate Bar", ShortName = "chocholate" },
        ["946662961"] = new() { Name = "Car Key", ShortName = "car.key" },
        ["-1884328185"] = new() { Name = "ScrapTransportHeliRepair", ShortName = "scraptransportheli.repair" }
    };
    private static readonly Lazy<Dictionary<string, CatalogItem>> Items = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "items.json");
        var items = File.Exists(path) ? Wire.Read<Dictionary<string, CatalogItem>>(File.ReadAllText(path)) : [];
        foreach (var pair in items) if (int.TryParse(pair.Key, out var id)) pair.Value.ItemId = id;
        return items;
    });
    public static IReadOnlyList<CatalogItem> All => Items.Value.Values.ToArray();
    public static void Name(ItemRecord item)
    {
        if (Items.Value.TryGetValue(item.ItemId.ToString(), out var definition) || Historical.TryGetValue(item.ItemId.ToString(), out definition))
        {
            if (item.Name.Length == 0) item.Name = definition.Name;
            // Native saves use the numeric item ID as a placeholder shortname.
            // Resolve it even for cached records that already have a display name.
            item.ShortName = definition.ShortName;
        }
        if (item.Name.Length == 0) item.Name = item.ItemId != 0 ? "アイテム ID " + item.ItemId : item.ShortName;
    }
}
