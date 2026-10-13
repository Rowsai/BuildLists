using Dalamud.Memory;
using Dalamud.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace BuildLists;

// Advance only after observing UI/data changes; retry unacknowledged actions at bounded intervals.
public sealed class RetainerConfirmation(Plugin plugin)
{
    private enum Stage { Idle, Select, OpenInventory, WaitRecord, CloseInventory, Dismiss, WaitList, CloseList }
    private Stage stage;
    private DateTimeOffset deadline, next, opened;
    private ulong character, target;
    private DateTimeOffset retryAt, talkRetryAt;
    private string lastWait = "";
    private string targetName = "";
    public bool Running => stage != Stage.Idle;
    public string Status { get; private set; } = "呼び鈴でリテイナー一覧を開いてから確認を開始してください。";
    public unsafe void Start()
    {
        if (Running) return;
        if (!Plugin.Client.IsLoggedIn || Visible("RetainerList") == null)
        { SetStatus("呼び鈴でリテイナー一覧を開いてください。"); return; }
        talkRetryAt = DateTimeOffset.MinValue;
        character = Plugin.Player.ContentId;
        Transition(Stage.Select, "未確認のリテイナーを確認します。");
    }
    public void Stop(string reason = "リテイナー記録確認を中止しました。")
    { stage = Stage.Idle; SetStatus(reason); }
    private void SetStatus(string text) { Status = text; plugin.Journal.Info("リテイナー記録確認: " + text); }
    private void Transition(Stage value, string text)
    { stage = value; retryAt = DateTimeOffset.MinValue; lastWait = ""; deadline = DateTimeOffset.UtcNow.AddSeconds(30); next = DateTimeOffset.UtcNow.AddMilliseconds(500); if (value == Stage.WaitList) retryAt = DateTimeOffset.UtcNow.AddSeconds(2); SetStatus(text); }
    public unsafe void Update(IFramework framework)
    {
        if (!Running) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (!Plugin.Client.IsLoggedIn || Plugin.Player.ContentId != character) { Stop("キャラクターが変更されたため中止しました。"); return; }
            if (now > deadline) { plugin.Journal.Warn("リテイナー記録確認タイムアウト: " + Status); Stop("読み込み待ちが30秒を超えたため中止しました。ゲーム画面を確認してください。"); return; }
            if (now < next) return;
            next = now.AddMilliseconds(200);
            var manager = RetainerManager.Instance();
            if (manager == null) { Waiting("リテイナーマネージャー待ち"); return; }
            if (stage == Stage.Select && !manager->IsReady) { Waiting("リテイナー一覧の読込待ち"); return; }
            if (stage is Stage.WaitRecord or Stage.CloseInventory or Stage.Dismiss)
                if (manager->LastSelectedRetainerId != target) { Stop("選択中のリテイナーが変わったため中止しました。"); return; }
            // Talk is a separate addon, not a SelectString menu. Handle greetings and farewells
            // only during this requested retainer interaction, before inspecting the menu.
            if (stage is Stage.OpenInventory or Stage.CloseInventory or Stage.Dismiss or Stage.WaitList)
                if (AdvanceRetainerTalk(now, manager->LastSelectedRetainerId)) return;
            switch (stage)
            {
                case Stage.Select:
                    var list = Visible("RetainerList");
                    if (list == null) return;
                    var records = plugin.Inventory.Snapshots;
                    for (uint i = 0; i < manager->GetRetainerCount(); i++)
                    {
                        var retainer = manager->GetRetainerBySortedIndex(i);
                        if (retainer == null || retainer->RetainerId == 0) return;
                        if (records.ContainsKey(retainer->RetainerId)) continue;
                        target = retainer->RetainerId;
                        targetName = retainer->NameString;
                        // Match displayed name rather than assuming the UI sort order.
                        for (int row = 0; row < 10; row++)
                        {
                            int offset = 3 + row * 10;
                            if (list->AtkValuesCount <= offset + 8) break;
                            if (list->AtkValues[offset].Type != AtkValueType.String && list->AtkValues[offset].Type != AtkValueType.ManagedString) continue;
                            var name = list->AtkValues[offset].String.Value;
                            if (name == null || !list->AtkValues[offset + 8].Bool) continue;
                            if (MemoryHelper.ReadSeStringNullTerminated((nint)name).TextValue != targetName) continue;
                            var args = stackalloc AtkValue[4];
                            args[0] = new AtkValue { Type = AtkValueType.Int, Int = 2 };
                            args[1] = new AtkValue { Type = AtkValueType.UInt, UInt = (uint)row };
                            args[2] = default; args[3] = default;
                            list->FireCallback(4, args, true);
                            opened = now;
                            Transition(Stage.OpenInventory, targetName + ": アイテムの受け渡しを待っています。");
                            return;
                        }
                        return;
                    }
                    Transition(Stage.CloseList, "すべての記録を確認しました。呼び鈴の一覧を閉じます。");
                    break;
                case Stage.OpenInventory:
                    if (manager->LastSelectedRetainerId != target)
                    { Waiting($"選択中ID更新待ち: expected={target}, actual={manager->LastSelectedRetainerId}"); return; }
                    if (InventoryVisible())
                    {
                        Transition(Stage.WaitRecord, targetName + ": 所持品の記録完了を待っています。");
                        return;
                    }
                    if (now < retryAt) return;
                    if (SelectMenu(2378))
                    {
                        // Preserve the first request time across retries.
                        if (retryAt == DateTimeOffset.MinValue) opened = now;
                        retryAt = now.AddSeconds(2);
                    }
                    break;
                case Stage.WaitRecord:
                    if (!plugin.Inventory.Snapshots.TryGetValue(target, out var snapshot) || snapshot.Updated < opened)
                    { Waiting("新しい所持品記録待ち: " + plugin.Inventory.ScanStatus); return; }
                    Transition(Stage.CloseInventory, targetName + ": 記録完了。所持品画面を閉じます。");
                    break;
                case Stage.CloseInventory:
                    if (!InventoryVisible() && Visible("SelectString") != null)
                    {
                        Transition(Stage.Dismiss, targetName + ": リテイナーを帰還させます。");
                        return;
                    }
                    if (now < retryAt) return;
                    var agents = AgentModule.Instance();
                    var agent = agents == null ? null : agents->GetAgentByInternalId(AgentId.Retainer);
                    if (agent != null && agent->IsAgentActive())
                    {
                        agent->Hide();
                        plugin.Journal.Info("リテイナー所持品を閉じる操作を送信");
                        retryAt = now.AddSeconds(2);
                    }
                    else Waiting("所持品画面を閉じるためのエージェント待ち");
                    break;
                case Stage.Dismiss:
                    if (Visible("RetainerList") != null && Visible("SelectString") == null)
                    { Transition(Stage.Select, "次の未確認リテイナーを選択します。"); return; }
                    if (InventoryVisible()) { Waiting("所持品画面が閉じるのを待機"); return; }
                    if (SelectMenu(2383))
                        Transition(Stage.WaitList, targetName + ": リテイナー一覧に戻るのを待っています。");
                    break;
                case Stage.CloseList:
                    var bellList = Visible("RetainerList");
                    if (bellList == null)
                    { Stop("すべてのリテイナー記録を確認し、呼び鈴の一覧を閉じました。"); return; }
                    if (now < retryAt) return;
                    var close = new AtkValue { Type = AtkValueType.Int, Int = -1 };
                    bellList->FireCallback(1, &close, true);
                    retryAt = now.AddSeconds(2);
                    plugin.Journal.Info("記録確認完了: 呼び鈴の一覧を閉じる操作を送信");
                    break;
                case Stage.WaitList:
                    if (Visible("RetainerList") != null && Visible("SelectString") == null)
                        Transition(Stage.Select, "次の未確認リテイナーを選択します。");
                    else if (now >= retryAt && !InventoryVisible())
                    {
                        if (SelectMenu(2383)) retryAt = now.AddSeconds(2);
                    }
                    break;
            }
        }
        catch (Exception ex) { plugin.Journal.Error("リテイナー記録確認に失敗", ex); Stop("エラーのため中止しました。ログを確認してください。"); }
    }
    private static unsafe AtkUnitBase* Visible(string name)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible && addon->IsReady ? addon : null;
    }
    private void Waiting(string reason)
    {
        if (lastWait == reason) return;
        lastWait = reason;
        plugin.Journal.Info($"リテイナー記録確認 [{stage}]: {reason}");
    }
    private unsafe bool AdvanceRetainerTalk(DateTimeOffset now, ulong selectedId)
    {
        var talk = Visible("Talk");
        if (talk == null) return false;
        // Farewell may clear the selection before the Talk addon disappears.
        if (selectedId != target && !(stage == Stage.WaitList && selectedId == 0))
        { Waiting("対象リテイナーのセリフか確認できないため待機"); return true; }
        if (now < talkRetryAt) return true;
        var atkStage = AtkStage.Instance();
        if (atkStage == null) return true;
        var evt = new AtkEvent
        {
            Listener = (AtkEventListener*)talk,
            Target = &atkStage->AtkEventTarget,
            State = new() { StateFlags = (AtkEventStateFlags)132 }
        };
        var data = new AtkEventData();
        talk->ReceiveEvent(AtkEventType.MouseDown, 0, &evt, &data);
        talk->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
        talk->ReceiveEvent(AtkEventType.MouseUp, 0, &evt, &data);
        talkRetryAt = now.AddMilliseconds(800);
        plugin.Journal.Info($"リテイナー記録確認 [{stage}]: {targetName} のセリフを進める操作を送信");
        return true;
    }
    private static unsafe bool InventoryVisible() => Visible("InventoryRetainer") != null || Visible("InventoryRetainerLarge") != null;
    private unsafe bool SelectMenu(uint row)
    {
        var addon = (AddonSelectString*)Visible("SelectString");
        if (addon == null) { Waiting("SelectString メニュー表示待ち"); return false; }
        string expected = Plugin.Data.GetExcelSheet<Addon>(Plugin.Client.ClientLanguage).GetRow(row).Text.ToDalamudString().TextValue;
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var menu = &addon->PopupMenu.PopupMenu;
        if (menu->EntryCount <= 0 || menu->EntryCount > 32 || menu->EntryNames == null) { Waiting("メニュー項目の読込待ち"); return false; }
        var observed = new List<string>();
        for (int i = 0; i < menu->EntryCount; i++)
        {
            var name = menu->EntryNames[i].Value;
            if (name == null) continue;
            string actual = MemoryHelper.ReadSeStringNullTerminated((nint)name).TextValue;
            observed.Add(actual);
            if (!RetainerMenuText.MatchesAction(row, actual, expected)) continue;
            var arg = new AtkValue { Type = AtkValueType.Int, Int = i };
            ((AtkUnitBase*)addon)->FireCallback(1, &arg, true);
            plugin.Journal.Info($"リテイナーメニュー操作: row={row}, index={i}, text={actual}");
            return true;
        }
        Waiting($"メニュー項目不一致: row={row}, expected=[{expected}], actual=[{string.Join(" | ", observed)}]");
        return false;
    }
}



