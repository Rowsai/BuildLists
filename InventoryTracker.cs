using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BuildLists;
public sealed class InventoryTracker(Plugin plugin)
{
    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4, InventoryType.Crystals];
    private static readonly InventoryType[] RetainerBags = [InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7, InventoryType.RetainerCrystals];
    public Dictionary<uint, long> OnHand { get; private set; } = [];
    public bool Available { get; private set; }
    public ulong CharacterId { get; private set; }
    public int KnownRetainers { get; private set; }
    private DateTime next;
    private ulong candidateId;
    private Dictionary<uint, long>? candidate;
    private DateTime nextError;
    private HashSet<ulong>? roster;
    public Dictionary<ulong, RetainerSnapshot> Snapshots
    {
        get
        {
            var stored = plugin.Config.Retainers.GetValueOrDefault(CharacterId);
            if (stored == null) return [];
            return roster == null ? stored : stored.Where(x => roster.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        }
    }
    public long RetainerCount(uint item) => Snapshots.Values.Sum(x => x.Items.GetValueOrDefault(item));
    public void Update(IFramework framework)
    {
        if (DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(1);
        try { Scan(); }
        catch (Exception ex)
        {
            Available = false;
            if (DateTime.UtcNow >= nextError) { plugin.Journal.Error("所持品読込失敗", ex); nextError = DateTime.UtcNow.AddSeconds(30); }
        }
    }
    private unsafe void Scan()
    {
        ulong id = Plugin.Client.IsLoggedIn ? Plugin.Player.ContentId : 0;
        if (id != CharacterId)
        {
            CharacterId = id; OnHand = []; candidate = null; candidateId = 0; KnownRetainers = 0; roster = null;
            plugin.Journal.Info(id == 0 ? "ログアウト: 所持数を未取得に変更" : "キャラクター変更: 所持品データを切り替え");
        }
        Available = false;
        if (id == 0) return;
        var manager = InventoryManager.Instance();
        if (manager == null) return;
        var bag = Read(manager, Bags);
        if (bag != null)
        {
            Available = true;
            if (!Equal(OnHand, bag)) { OnHand = bag; plugin.Journal.Info($"インベントリ更新: {bag.Count} 種類"); }
        }
        var retainers = RetainerManager.Instance();
        if (retainers == null || !retainers->IsReady) return;
        KnownRetainers = retainers->GetRetainerCount();
        var activeIds = new HashSet<ulong>();
        for (uint index = 0; index < KnownRetainers; index++)
        {
            var retainer = retainers->GetRetainerBySortedIndex(index);
            if (retainer != null && retainer->RetainerId != 0) activeIds.Add(retainer->RetainerId);
        }
        if (activeIds.Count == KnownRetainers) roster = activeIds;
        // The shared retainer containers are only associated with the active retainer
        // while its inventory UI is visible. Never infer ownership from a stale ID alone.
        var grid = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("RetainerGrid0").Address;
        var large = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("RetainerGrid").Address;
        if ((grid == null || !grid->IsVisible) && (large == null || !large->IsVisible)) { candidate = null; candidateId = 0; return; }
        ulong rid = retainers->LastSelectedRetainerId;
        if (rid == 0) return;
        var items = Read(manager, RetainerBags);
        if (items == null) { candidate = null; return; }
        // Require two consecutive fully loaded, stable samples after switching retainers.
        if (candidateId != rid || candidate == null || !Equal(candidate, items)) { candidateId = rid; candidate = items; return; }
        if (!plugin.Config.Retainers.TryGetValue(id, out var saved)) plugin.Config.Retainers[id] = saved = [];
        if (!saved.TryGetValue(rid, out var old) || !Equal(old.Items, items) || DateTimeOffset.UtcNow - old.Updated > TimeSpan.FromMinutes(1))
        {
            saved[rid] = new RetainerSnapshot { Updated = DateTimeOffset.UtcNow, Items = items };
            plugin.Journal.Info($"リテイナー所持品を記録: {items.Count} 種類");
            plugin.Save();
        }
    }
    private static bool Equal(Dictionary<uint, long> a, Dictionary<uint, long> b) => a.Count == b.Count && a.All(x => b.GetValueOrDefault(x.Key, -1) == x.Value);
    private static unsafe Dictionary<uint, long>? Read(InventoryManager* manager, InventoryType[] types)
    {
        var result = new Dictionary<uint, long>();
        foreach (var type in types)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded || container->Items == null || container->Size < 0 || container->Size > 200) return null;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->IsSymbolic || item->ItemId == 0 || item->Quantity <= 0 || (item->Flags & InventoryItem.ItemFlags.Collectable) != 0) continue;
                result[item->ItemId] = result.GetValueOrDefault(item->ItemId) + item->Quantity;
            }
        }
        return result;
    }
}
