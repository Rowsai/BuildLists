using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BuildLists;
public sealed class InventoryTracker(Plugin plugin)
{
    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4, InventoryType.Crystals];
    private static readonly InventoryType[] RetainerBags = [InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerCrystals];
    public string ScanStatus { get; private set; } = "リテイナーの所持品を開いてください。";
    private DateTime candidateSince;
    private ulong capturedThisVisit;
    public Dictionary<uint, long> OnHand { get; private set; } = [];
    public bool Available { get; private set; }
    public ulong CharacterId { get; private set; }
    public int KnownRetainers { get; private set; }
    private DateTime next;
    private ulong candidateId;
    private Dictionary<uint, long>? candidate;
    private DateTime nextError;
    private HashSet<ulong>? roster;
    public bool RetainerRecordsIncomplete => CharacterId != 0 && (roster == null || roster.Any(id => !Snapshots.ContainsKey(id)));
    public Dictionary<ulong, RetainerSnapshot> Snapshots
    {
        get
        {
            var stored = plugin.Config.Retainers.GetValueOrDefault(CharacterId);
            if (stored == null) return [];
            var now = DateTimeOffset.UtcNow;
            return stored.Where(x => RetainerCache.IsFresh(x.Value, now) && (roster == null || roster.Contains(x.Key))).ToDictionary(x => x.Key, x => x.Value);
        }
    }
    public long RetainerCount(uint item) => Snapshots.Values.Sum(x => x.Items.GetValueOrDefault(item));
    public void Update(IFramework framework)
    {
        if (DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddMilliseconds(250);
        try { Scan(); }
        catch (Exception ex)
        {
            Available = false;
            if (DateTime.UtcNow >= nextError) { plugin.Journal.Error("所持品読込失敗", ex); nextError = DateTime.UtcNow.AddSeconds(30); }
        }
    }
    private unsafe void Scan()
    {
        int expired = RetainerCache.Expire(plugin.Config, DateTimeOffset.UtcNow);
        if (expired > 0) { plugin.Journal.Info($"24時間を経過したリテイナー記録をリセット: {expired} 人"); plugin.Save(); }
        ulong id = Plugin.Client.IsLoggedIn ? Plugin.Player.ContentId : 0;
        if (id != CharacterId)
        {
            CharacterId = id; OnHand = []; candidate = null; candidateId = 0; capturedThisVisit = 0; KnownRetainers = 0; roster = null;
            plugin.Journal.Info(id == 0 ? "ログアウト: 所持数を未取得に変更" : "キャラクター変更: 所持品データを切り替え");
        }
        Available = false;
        if (id == 0) { SetStatus("ログイン待ち"); return; }
        var manager = InventoryManager.Instance();
        if (manager == null) return;
        var bag = Read(manager, Bags, out _);
        if (bag != null)
        {
            Available = true;
            if (!Equal(OnHand, bag)) { OnHand = bag; plugin.Journal.Info($"インベントリ更新: {bag.Count} 種類"); }
        }
        var retainers = RetainerManager.Instance();
        if (retainers == null || !retainers->IsReady) { SetStatus("リテイナー一覧の読み込み待ち"); candidate = null; capturedThisVisit = 0; return; }
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
        if (!IsInventoryOpen()) { candidate = null; candidateId = 0; capturedThisVisit = 0; SetStatus("リテイナーの所持品を開いてください。"); return; }
        ulong rid = retainers->LastSelectedRetainerId;
        if (rid == 0 || roster == null || !roster.Contains(rid)) { candidate = null; capturedThisVisit = 0; SetStatus("選択中のリテイナーIDを確認中"); return; }
        var items = Read(manager, RetainerBags, out var reason);
        if (items == null) { candidate = null; SetStatus("所持品の読み込み待ち: " + reason); return; }
        if (candidateId != rid) capturedThisVisit = 0;
        // All five physical pages + crystals must be loaded and stable after switching.
        if (candidateId != rid || candidate == null || !Equal(candidate, items))
        { candidateId = rid; candidate = items; candidateSince = DateTime.UtcNow; SetStatus("所持品データを確認中…"); return; }
        if (DateTime.UtcNow - candidateSince < TimeSpan.FromMilliseconds(750)) return;
        if (!plugin.Config.Retainers.TryGetValue(id, out var saved)) plugin.Config.Retainers[id] = saved = [];
        if (!saved.TryGetValue(rid, out var old) || capturedThisVisit != rid || !Equal(old.Items, items) || DateTimeOffset.UtcNow - old.Updated > TimeSpan.FromMinutes(1))
        {
            saved[rid] = new RetainerSnapshot { Updated = DateTimeOffset.UtcNow, Items = items };
            capturedThisVisit = rid;
            plugin.Journal.Info($"リテイナー所持品の確認完了: {items.Count} 種類 / 確認済み {Snapshots.Count} / {KnownRetainers} 人");
            plugin.Save();
        }
        SetStatus($"所持品の確認完了（確認済み {Snapshots.Count} / {KnownRetainers} 人）");
    }
    private void SetStatus(string status)
    {
        if (ScanStatus == status) return;
        ScanStatus = status; plugin.Journal.Info("リテイナー取得状態: " + status);
    }
    private static unsafe bool IsInventoryOpen()
    {
        foreach (var name in new[] { "InventoryRetainer", "InventoryRetainerLarge", "RetainerGrid0", "RetainerGrid" })
            for (int index = 1; index <= 3; index++)
            {
                var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name, index).Address;
                if (addon != null && addon->IsVisible) return true;
            }
        return false;
    }
    private static bool Equal(Dictionary<uint, long> a, Dictionary<uint, long> b) => a.Count == b.Count && a.All(x => b.GetValueOrDefault(x.Key, -1) == x.Value);
    private static unsafe Dictionary<uint, long>? Read(InventoryManager* manager, InventoryType[] types, out string reason)
    {
        reason = "";
        var result = new Dictionary<uint, long>();
        foreach (var type in types)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded || container->Items == null || container->Size <= 0 || container->Size > 200)
            { reason = $"{type} が未読込"; return null; }
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) { reason = $"{type} スロット {i} が未読込"; return null; }
                if (item->IsSymbolic || item->ItemId == 0 || item->Quantity <= 0 || (item->Flags & InventoryItem.ItemFlags.Collectable) != 0) continue;
                result[item->ItemId] = result.GetValueOrDefault(item->ItemId) + item->Quantity;
            }
        }
        return result;
    }
}
