using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace BuildLists;
public sealed class MainWindow : Window, IDisposable
{
    private readonly IFontHandle bodyFont;
    private int animatedPage = -1;
    private double animationStart;
    private uint hoveredMaterial;
    private Vector2 hoverAnchor;
    private bool materialHoveredThisFrame;
    private float hoverAlpha;
    private Vector2 searchPopupMin, searchPopupMax;
    private double nextItemSearch;
    private readonly Dictionary<string, float> panelHeights = [];
    private readonly Stack<(string Id, bool Fill)> panels = new();
    private readonly Plugin p;
    private int page;
    private string query = "", listFilter = "", logFilter = "", notice = "", exportDirectory = "";
    private Guid? pendingEdit, pendingDelete;
    private bool requestSwitch;
    private bool collapsed;
    private Vector2 expandedSize;
    private int quantity = 1;
    private uint selected;
    private Guid selectedList;
    private bool suggestions, follow = true;
    private CraftRecipe[] matches = [];
    private SavedList? cachedList;
    private int cachedRevision = -1;
    private Dictionary<uint, (string Name, long Amount)> expandedMaterials = [];
    private List<(uint ItemId, int Depth)> materialDisplayOrder = [];
    private readonly Vector4 basicMaterialColor = new(1f, .85f, .25f, 1f);
    private string? expansionError;
    private readonly Vector4 accent = new(.72f, .53f, .97f, 1);
    private readonly string[] pages = ["ホーム", "制作リスト", "素材リスト", "ログ"];
    public MainWindow(Plugin plugin) : base("Artisan's Material Board###BuildLists")
    {
        p = plugin; Size = new Vector2(1080, 740); SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoTitleBar;
        var fontPath = System.IO.Path.Combine(Plugin.Interface.AssemblyLocation.Directory!.FullName, "Fonts", "ZenKakuGothicNew-Medium.ttf");
        bodyFont = Plugin.Interface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var config = new SafeFontConfig { SizePx = 18, OversampleH = 2, OversampleV = 2 };
            config.MergeFont = tk.AddFontFromFile(fontPath, config);
            tk.AddGameSymbol(config);
            tk.Font = config.MergeFont;
        }));
        exportDirectory = p.Config.ExportDirectory;
        if (string.IsNullOrWhiteSpace(exportDirectory))
        {
            try { exportDirectory = TsvExport.DefaultDirectory(); }
            catch (Exception ex) { p.Journal.Error("ダウンロードフォルダー取得失敗", ex); }
        }
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new(760, 520), MaximumSize = new(float.MaxValue) };
    }
    public override void OnClose() => p.Journal.Info("ウィンドウを閉じる");
    public override void OnOpen() { animatedPage = -1; }
    public void Dispose() => bodyFont.Dispose();
    public override void PreDraw()
    {
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new(760, collapsed ? 68 : 520), MaximumSize = new(float.MaxValue) };
        if (collapsed) ImGui.SetNextWindowSize(new Vector2(expandedSize.X, 68 * ImGuiHelpers.GlobalScale));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(.055f, .055f, .08f, .98f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.075f, .075f, .11f, .92f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.23f, .17f, .34f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(.38f, .26f, .53f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(.46f, .31f, .66f, 1));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(.29f, .20f, .43f, 1));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.12f, .12f, .18f, 1));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(.29f, .25f, .39f, .7f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10, 10));
    }
    public override void PostDraw() { ImGui.PopStyleVar(4); ImGui.PopStyleColor(8); }
    public override void Draw()
    {
        materialHoveredThisFrame = false;
        using var fontScope = bodyFont.Push();
        DrawTitleBar();
        if (collapsed || !IsOpen) return;
        ImGui.BeginChild("navigation", new Vector2(154 * ImGuiHelpers.GlobalScale, 0), true);
        FontAwesomeIcon[] navIcons = [FontAwesomeIcon.Home, FontAwesomeIcon.Hammer, FontAwesomeIcon.Boxes, FontAwesomeIcon.Terminal];
        for (var i = 0; i < pages.Length; i++)
        {
            bool active = page == i;
            if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.35f, .25f, .5f, 1));
            if (ImGuiComponents.IconButtonWithText(navIcons[i], pages[i], new Vector2(132, 52))) { page = i; p.Journal.Info($"画面切替: {pages[i]}"); }
            if (active) ImGui.PopStyleColor();
        }
        ImGui.Spacing(); ImGui.TextDisabled("v1.0.0.1 / API 15");
        ImGui.EndChild(); ImGui.SameLine();
        ImGui.BeginChild("content", Vector2.Zero, false);
        DrawHammerBackground();
        if (animatedPage != page) { animatedPage = page; animationStart = ImGui.GetTime(); ImGui.SetScrollY(0); }
        float progress = Math.Clamp((float)((ImGui.GetTime() - animationStart) / .24), 0, 1);
        float eased = 1 - MathF.Pow(1 - progress, 3);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (1 - eased) * 20 * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * eased);
        ImGui.TextColored(accent, pages[page]); ImGui.Spacing();
        if (notice.Length > 0) ImGui.TextWrapped(notice);
        switch (page) { case 0: Home(); break; case 1: Crafting(); break; case 2: Stock(); break; case 3: Logs(); break; }
        ImGui.PopStyleVar();
        DrawDialogs();
        ImGui.EndChild();
        DrawMaterialSearch();
    }
    private void DrawTitleBar()
    {
        float scale = ImGuiHelpers.GlobalScale;
        float height = 48 * scale;
        var origin = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, height), ImGui.GetColorU32(new Vector4(.075f, .073f, .105f, 1)), 10);
        ImGui.InvisibleButton("##titleDrag", new Vector2(Math.Max(100, width - 100 * scale), height));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        int assetSize = 32 * scale <= 40 ? 32 : 32 * scale <= 80 ? 64 : 128;
        var iconPath = System.IO.Path.Combine(Plugin.Interface.AssemblyLocation.Directory!.FullName, $"icon-{assetSize}.png");
        var icon = Plugin.Textures.GetFromFile(iconPath).GetWrapOrDefault();
        ImGui.SetCursorScreenPos(origin + new Vector2(8, 8) * scale);
        if (icon != null) ImGui.Image(icon.Handle, new Vector2(32 * scale));
        draw.AddText(origin + new Vector2(52, 14) * scale, ImGui.GetColorU32(new Vector4(.94f, .94f, .97f, 1)), "Artisan's Material Board");
        var badge = origin + new Vector2(52 * scale + ImGui.CalcTextSize("Artisan's Material Board").X + 16 * scale, 12 * scale);
        draw.AddRectFilled(badge, badge + new Vector2(82, 25) * scale, ImGui.GetColorU32(new Vector4(.23f, .17f, .34f, 1)), 12 * scale);
        draw.AddText(badge + new Vector2(10, 3) * scale, ImGui.GetColorU32(accent), "API 15");
        float closeX = origin.X + width - (12 + 38) * scale;
        ImGui.SetCursorScreenPos(new Vector2(closeX - (38 + 8) * scale, origin.Y + 5 * scale));
        if (ImGuiComponents.IconButton("collapse", collapsed ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown, new Vector2(38, 36)))
        {
            if (!collapsed) expandedSize = ImGui.GetWindowSize();
            collapsed = !collapsed;
            ImGui.SetWindowSize(collapsed ? new Vector2(ImGui.GetWindowSize().X, 68 * scale) : expandedSize);
            p.Journal.Info(collapsed ? "ウィンドウ折りたたみ" : "ウィンドウ展開");
        }
        ImGui.SetCursorScreenPos(new Vector2(closeX, origin.Y + 5 * scale));
        if (ImGuiComponents.IconButton("close", FontAwesomeIcon.Times, new Vector2(38, 36))) { IsOpen = false; }
        ImGui.SetCursorScreenPos(origin + new Vector2(0, height + 6 * scale));
        ImGui.Separator();
    }
    private static void DrawHammerBackground()
    {
        // Draw-list geometry only: no extra windows or hit targets, and no per-frame assets.
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        var area = ImGui.GetWindowSize();
        float scale = ImGuiHelpers.GlobalScale;
        float size = Math.Clamp(area.Y * .55f, 180 * scale, 360 * scale);
        double time = ImGui.GetTime();
        draw.PushClipRect(origin, origin + area, true);
        for (int i = 0; i < 3; i++)
        {
            float phase = (float)((time / 55 + i / 3.0) % 1);
            var center = origin + new Vector2(-size + phase * (area.X + 2 * size), area.Y * (.25f + i * .25f));
            float angle = -.55f + i * .18f;
            float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
            Vector2 Point(float x, float y) => center + new Vector2(x * cos - y * sin, x * sin + y * cos) * size;
            void Quad(float left, float top, float right, float bottom, Vector4 color)
                => draw.AddQuadFilled(Point(left, top), Point(right, top), Point(right, bottom), Point(left, bottom), ImGui.GetColorU32(color));
            var purple = new Vector4(.67f, .43f, .96f, .085f);
            var edge = new Vector4(.78f, .63f, 1f, .10f);
            Quad(-.055f, -.20f, .055f, .49f, purple); // handle
            Quad(-.075f, .18f, .075f, .49f, purple); // wrapped grip
            Quad(-.30f, -.40f, .30f, -.17f, purple); // hammer head
            Quad(-.33f, -.43f, -.24f, -.14f, edge); // striking face
            Quad(.24f, -.43f, .33f, -.14f, edge);
            Quad(-.10f, .46f, .10f, .53f, edge); // pommel
            for (int band = 0; band < 4; band++) Quad(-.075f, .21f + band * .065f, .075f, .225f + band * .065f, edge);
        }
        draw.PopClipRect();
    }
    private static bool ActionButton(string label)
    {
        var icon = label.Contains("削除") || label.Contains("破棄") ? FontAwesomeIcon.Trash :
            label.Contains("Save") || label.Contains("保存") ? FontAwesomeIcon.Save :
            label.Contains("出力") ? FontAwesomeIcon.FileExport :
            label.Contains("編集") ? FontAwesomeIcon.Edit :
            label.Contains("コピー") ? FontAwesomeIcon.Copy :
            label.Contains("キャンセル") ? FontAwesomeIcon.Times :
            label.Contains("戻す") || label.Contains("読み込み") ? FontAwesomeIcon.Sync :
            label.StartsWith("v##") ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.Plus;
        return ImGuiComponents.IconButtonWithText(icon, label.StartsWith("v##") ? "候補##suggest" : label);
    }
    private void Home()
    {
        BeginPanel("overview", "製作を、ひとつの計画に。");
        ImGui.TextWrapped("作りたいものを選んで、必要な素材をひとつに。製作計画を保存し、所持品と照らし合わせて準備できます。");
        ImGui.Spacing();
        ImGui.TextColored(accent, $"{p.Recipes.Count:N0} レシピ    /    {p.Config.Lists.Count} 保存済みリスト");
        EndPanel();
        BeginPanel("history", "アップデート履歴");
        ImGui.TextColored(accent, "v1.0.0.1 — 2026/10/14");
        foreach (var change in ReleaseHistory.Changes) ImGui.TextWrapped("・" + change);
        ImGui.Spacing(); ImGui.Separator();
        ImGui.TextColored(accent, "v1.0.0.0 — 初回リリース");
        ImGui.TextWrapped("・日本語アイテム検索と候補表示\n・個数指定、素材表示、名前を付けてリスト保存\n・必要素材の合算と所持数比較\n・リテイナー所持品の取得記録\n・INFO / WARN / ERROR の操作ログ");
        EndPanel();
        BeginPanel("inventoryGuide", "所持品の記録");
        ImGui.TextWrapped("リテイナー所持数を記録するには、各リテイナーの所持品を開き、数秒待ってください。閉じている間は前回の記録を使用します。");
        if (ActionButton("レシピを再読み込み")) { p.Journal.Info("レシピ再読込ボタン"); p.LoadRecipes(); }
        EndPanel();
    }
    private void BeginPanel(string id, string title, bool fill = false)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.12f, .105f, .17f, .88f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(.43f, .32f, .61f, .7f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18, 16) * ImGuiHelpers.GlobalScale);
        float height = fill ? Math.Max(220 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y - 8) : panelHeights.GetValueOrDefault(id, 180 * ImGuiHelpers.GlobalScale);
        panels.Push((id, fill));
        ImGui.BeginChild(id, new Vector2(0, height), true, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.TextColored(accent, title);
        ImGui.Separator(); ImGui.Spacing();
    }
    private void EndPanel()
    {
        var panel = panels.Pop();
        if (!panel.Fill) panelHeights[panel.Id] = ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y;
        ImGui.EndChild(); ImGui.PopStyleVar(); ImGui.PopStyleColor(2); ImGui.Spacing();
    }
    private void Crafting()
    {
        BeginPanel("listEditor", "制作リストの管理");
        var editing = p.Config.Lists.FirstOrDefault(x => x.Id == p.Config.EditingListId);
        if (ImGui.BeginCombo("編集するリスト", editing?.Name ?? "新規リスト"))
        {
            if (ImGui.Selectable("新規リスト", editing == null)) RequestEdit(null);
            foreach (var list in p.Config.Lists)
                if (ImGui.Selectable(list.Name + "##edit" + list.Id, list.Id == p.Config.EditingListId) && list.Id != p.Config.EditingListId) RequestEdit(list.Id);
            ImGui.EndCombo();
        }
        var name = p.Config.DraftName;
        if (ImGui.InputTextWithHint("リスト名", "保存する名前を入力…", ref name, 100)) { p.Config.DraftName = name; p.Journal.Info("リスト名変更"); p.Save(); }
        ImGui.BeginDisabled(!ListEditor.CanSave(p.Config));
        if (ActionButton(editing == null ? "Save" : "Save（上書き）"))
        {
            if (ListEditor.Save(p.Config, p.Save)) { selectedList = p.Config.EditingListId!.Value; notice = $"「{p.Config.DraftName}」を保存しました"; p.Journal.Info("製作リスト保存 / " + p.Config.DraftName); }
            else notice = "保存に失敗しました。ログを確認してください。";
        }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ActionButton("新規リスト")) RequestEdit(null);
        if (editing != null) { ImGui.SameLine(); if (ActionButton("リスト削除")) pendingDelete = editing.Id; }
        if (ListEditor.Dirty(p.Config)) ImGui.TextDisabled("未保存の変更があります");
        if (!ListEditor.CanSave(p.Config)) ImGui.TextDisabled("名前と製作対象を指定してください。同名の別リストは保存できません。");
        EndPanel();
        BeginPanel("itemSearch", "アイテムを追加");
        ImGui.TextUnformatted("作りたいアイテムを検索・選択してください");
        ImGui.SetNextItemWidth(Math.Max(100, ImGui.GetContentRegionAvail().X - ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.ChevronDown, "候補") - ImGui.GetStyle().ItemSpacing.X));
        if (ImGui.InputTextWithHint("##search", "アイテム名を入力…", ref query, 200))
        {
            selected = 0; suggestions = true; Search(); p.Journal.Info($"検索入力: {query} / 候補 {matches.Length} 件（最大80件）");
        }
        ImGui.SameLine();
        if (ActionButton("v##suggest")) { suggestions = !suggestions; Search(); p.Journal.Info("候補リスト開閉"); }
        if (suggestions)
        {
            ImGui.BeginChild("suggestions", new Vector2(0, 155), true);
            foreach (var recipe in matches)
                if (ImGui.Selectable($"{recipe.Name}  [{recipe.Job}]  ×{recipe.Yield}##{recipe.Id}"))
                { selected = recipe.Id; query = recipe.Name; suggestions = false; p.Journal.Info($"候補選択: {recipe.Id} {recipe.Name}"); }
            if (matches.Length == 0) ImGui.TextDisabled("一致するレシピがありません");
            ImGui.EndChild();
        }
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("完成品の個数", ref quantity)) { quantity = Math.Clamp(quantity, 1, 999999); p.Journal.Info($"追加個数変更: {quantity}"); }
        ImGui.SameLine(); ImGui.BeginDisabled(!p.Recipes.TryGetValue(selected, out var candidate) || ListEditor.AlreadyAdded(candidate, p.Config, p.Recipes));
        if (ActionButton("Add"))
        {
            p.Config.Draft.Add(new CraftEntry { RecipeId = selected, Quantity = quantity });
            p.Journal.Info($"製作対象追加: {selected} ×{quantity}"); p.Save();
            selected = 0; query = ""; Search();
        }
        ImGui.EndDisabled();
        EndPanel();
        BeginPanel("draft", "選択済みの制作対象アイテム", true);
        if (p.Config.Draft.Count == 0) ImGui.TextDisabled("アイテムを選び Add を押してください");
        foreach (var entry in p.Config.Draft.ToArray())
        {
            ImGui.PushID((int)entry.RecipeId);
            if (p.Recipes.TryGetValue(entry.RecipeId, out var recipe))
            {
                ImGui.TextColored(accent, $"{recipe.Name}  /  {recipe.Job}");
                var qty = entry.Quantity; ImGui.SetNextItemWidth(145);
                if (ImGui.InputInt("個数", ref qty)) { entry.Quantity = Math.Clamp(qty, 1, 999999); p.Journal.Info($"製作数変更: {entry.RecipeId} → {entry.Quantity}"); p.Save(); }
                var crafts = Materials.Crafts(entry.Quantity, recipe.Yield);
                ImGui.SameLine(); ImGui.TextDisabled($"{crafts} 回製作 / {crafts * recipe.Yield} 個完成");
            }
            else ImGui.TextUnformatted($"未解決レシピ: {entry.RecipeId}（削除して再選択してください）");
            if (ActionButton("削除")) { p.Config.Draft.Remove(entry); Search(); p.Journal.Info($"製作対象削除: {entry.RecipeId}"); p.Save(); }
            ImGui.Separator(); ImGui.PopID();
        }
        EndPanel();
    }
    private void Search() => matches = p.Recipes.Values.Where(x => !ListEditor.AlreadyAdded(x, p.Config, p.Recipes) && x.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Name).ThenBy(x => x.Id).Take(80).ToArray();
    private void RequestEdit(Guid? id)
    {
        pendingEdit = id;
        if (ListEditor.Dirty(p.Config)) requestSwitch = true;
        else LoadEditor(id);
    }
    private void LoadEditor(Guid? id)
    {
        ListEditor.Load(p.Config, p.Config.Lists.FirstOrDefault(x => x.Id == id));
        selected = 0; query = ""; suggestions = false; Search(); page = 1;
        p.Save(); notice = ""; p.Journal.Info("編集リスト切替: " + (p.Config.DraftName.Length == 0 ? "新規" : p.Config.DraftName));
    }
    private void DrawDialogs()
    {
        if (requestSwitch) { ImGui.OpenPopup("編集内容の切り替え"); requestSwitch = false; }
        if (ImGui.BeginPopupModal("編集内容の切り替え", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("未保存の変更を破棄して切り替えますか？");
            if (ActionButton("破棄して切り替え")) { LoadEditor(pendingEdit); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ActionButton("キャンセル")) { p.Journal.Info("リスト切替キャンセル"); ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        if (pendingDelete != null && !ImGui.IsPopupOpen("リスト削除の確認")) ImGui.OpenPopup("リスト削除の確認");
        if (ImGui.BeginPopupModal("リスト削除の確認", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var list = p.Config.Lists.FirstOrDefault(x => x.Id == pendingDelete);
            ImGui.TextUnformatted($"「{list?.Name}」を削除しますか？");
            ImGui.TextUnformatted("このリストの編集中の変更も削除されます。");
            if (ActionButton("削除する"))
            {
                if (pendingDelete is Guid id && ListEditor.Delete(p.Config, id, p.Save))
                {
                    if (selectedList == id) selectedList = Guid.Empty;
                    notice = "リストを削除しました"; p.Journal.Info("製作リスト削除: " + list?.Name); Search();
                }
                else notice = "削除の保存に失敗しました。ログを確認してください。";
                pendingDelete = null; ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(); if (ActionButton("キャンセル")) { pendingDelete = null; p.Journal.Info("リスト削除キャンセル"); ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
    }
    private void Stock()
    {
        BeginPanel("stockSettings", "素材リスト・所持品情報");
        if (ImGui.InputTextWithHint("##listfilter", "保存したリストを検索…", ref listFilter, 100)) p.Journal.Info($"保存リスト検索: {listFilter}");
        var saved = p.Config.Lists.FirstOrDefault(x => x.Id == selectedList);
        if (ImGui.BeginCombo("制作リスト", saved?.Name ?? "リストを選択してください"))
        {
            foreach (var list in p.Config.Lists.Where(x => x.Name.Contains(listFilter, StringComparison.OrdinalIgnoreCase)))
                if (ImGui.Selectable(list.Name + "##" + list.Id, list.Id == selectedList)) { selectedList = list.Id; p.Journal.Info($"素材リスト選択: {list.Name}"); }
            ImGui.EndCombo();
        }
        var inventory = p.Inventory;
        DrawRetainerWarning();
        ImGui.TextWrapped($"リテイナー記録: {inventory.Snapshots.Count} 人 / 登録確認 {inventory.KnownRetainers} 人。所持品を開くと更新されます。");
        ImGui.TextWrapped(inventory.ScanStatus);
        if (p.Confirmation.Running)
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Stop, "確認を中止")) p.Confirmation.Stop();
        }
        else if (inventory.RetainerRecordsIncomplete)
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Search, "リテイナー記録確認")) p.Confirmation.Start();
        }
        ImGui.TextWrapped(p.Confirmation.Status);
        if (inventory.RetainerRecordsIncomplete)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, .3f, .35f, 1));
            ImGui.TextWrapped("リテイナー確認が完了していません。リテイナーの所持品を開いてください。");
            ImGui.PopStyleColor();
        }
        if (inventory.Snapshots.Count > 0) ImGui.TextDisabled($"最も古い記録: {inventory.Snapshots.Values.Min(x => x.Updated).ToLocalTime():yyyy/MM/dd HH:mm}");
        ImGui.TextWrapped("手持ち = インベントリ（クリスタルを含む・NQ/HQ合計）+ リテイナーの前回記録。未取得のリテイナー分は含まれません。");
        if (!inventory.Available) ImGui.TextColored(accent, "インベントリ未取得 / ログイン後に更新されます");
        if (saved == null) { ImGui.TextDisabled("制作リストで保存したリストを選択してください"); EndPanel(); return; }
        if (ActionButton("このリストを編集")) RequestEdit(saved.Id);
        ImGui.SameLine(); if (ActionButton("リスト削除")) pendingDelete = saved.Id;
        ExportSettings();
        ImGui.TextWrapped("中間素材もすべて自作する場合の必要数です。所持済み中間素材は展開数から差し引きません。複数レシピがある素材はレシピID順の先頭を使用します。");
        EndPanel();
        BeginPanel("stock", "必要な素材", true);
        try
        {
            if (!ReferenceEquals(cachedList, saved) || cachedRevision != p.RecipeRevision)
            {
                cachedList = saved; cachedRevision = p.RecipeRevision; expansionError = null;
                try { expandedMaterials = Materials.Expand(saved.Entries, p.Recipes); materialDisplayOrder = Materials.DisplayOrder(saved.Entries, p.Recipes); p.Journal.Info($"全階層の素材を集計: {saved.Name} / {expandedMaterials.Count} 種類"); }
                catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
                { expandedMaterials = []; expansionError = ex.Message; p.Journal.Error("素材展開失敗", ex); }
            }
            if (expansionError != null) throw new InvalidOperationException(expansionError);
            var materials = expandedMaterials;
            ImGui.TextColored(basicMaterialColor, "黄色：中間素材を作るための基本素材");
            ImGui.TextColored(new Vector4(.4f, .86f, .54f, 1), "緑：揃っている基本素材");
            ImGui.SameLine(); ImGui.TextColored(new Vector4(.38f, .68f, 1f, 1), "青：揃っている中間素材");
            ImGui.TextDisabled("シャード・クリスタル・クラスターは末尾に合算して表示します。");
            ImGui.TextWrapped("中間素材 → その基本素材の順に表示します。共通素材は各箇所に再表示しますが、必要数・不足数はリスト全体の合算値です。");
            if (ActionButton("TSV出力"))
            {
                try
                {
                    var snapshots = inventory.Snapshots;
                    var ret = materials.Keys.ToDictionary(x => x, x => snapshots.Values.Sum(s => s.Items.GetValueOrDefault(x)));
                    var path = TsvExport.Write(exportDirectory.Trim(), saved.Name, materials, inventory.Available ? inventory.OnHand : null, ret,
                        snapshots.Count, snapshots.Count == 0 ? null : snapshots.Values.Min(x => x.Updated), materialDisplayOrder);
                    notice = "TSVを出力しました: " + path; p.Journal.Info(notice);
                }
                catch (Exception ex) { notice = "TSV出力に失敗しました: " + ex.Message; p.Journal.Error("TSV出力失敗", ex); }
            }
            if (ImGui.CollapsingHeader("製作の内訳（階層表示）"))
            {
                var producers = Materials.Producers(p.Recipes);
                foreach (var entry in saved.Entries)
                {
                    var recipe = p.Recipes[entry.RecipeId];
                    ImGui.PushID((int)entry.RecipeId);
                    if (ImGui.TreeNode($"{recipe.Name} ×{entry.Quantity}"))
                    {
                        DrawIngredients(recipe, Materials.Crafts(entry.Quantity, recipe.Yield), producers, new HashSet<uint>());
                        ImGui.TreePop();
                    }
                    ImGui.PopID();
                }
                ImGui.TextDisabled("階層表示は対象別の内訳です。共通素材のまとめ製作による合算値は下表・TSVを参照してください。");
            }
            if (ImGui.BeginTable("stocktable", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            {
                ImGui.TableSetupColumn("素材名"); ImGui.TableSetupColumn("手持ち / 全体必要数"); ImGui.TableSetupColumn("バッグ"); ImGui.TableSetupColumn("リテイナー記録"); ImGui.TableSetupColumn("全体不足（記録基準）");
                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                foreach (var header in new[] { "素材名", "手持ち / 全体必要数", "バッグ", "リテイナー記録", "全体不足（記録基準）" })
                { ImGui.TableNextColumn(); if (header == "リテイナー記録") DrawRetainerWarning(); ImGui.TextUnformatted(header); }
                foreach (var row in materialDisplayOrder)
                {
                    var pair = new KeyValuePair<uint, (string Name, long Amount)>(row.ItemId, materials[row.ItemId]);
                    long bags = inventory.OnHand.GetValueOrDefault(pair.Key), ret = inventory.RetainerCount(pair.Key), total = bags + ret;
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    bool basic = row.Depth > 0 || Materials.IsCrystal(pair.Value.Name);
                    var color = MaterialColor(pair.Key, basic, row.Depth > 0 ? basicMaterialColor : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
                    ImGui.TextColored(color, (row.Depth > 0 ? new string(' ', Math.Min(row.Depth, 8) * 2) + "└ " : "") + pair.Value.Name);
                    HoverMaterial(pair.Key);
                    ImGui.TableNextColumn(); ImGui.TextColored(MaterialColor(pair.Key, basic, accent), $"{(inventory.Available ? total.ToString("N0") : "?")} / {pair.Value.Amount:N0}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(inventory.Available ? bags.ToString("N0") : "未取得");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(inventory.Snapshots.Count > 0 ? ret.ToString("N0") : "未取得");
                    ImGui.TableNextColumn();
                    long deficit = Math.Max(0, pair.Value.Amount - total);
                    ImGui.TextColored(inventory.Available && deficit > 0 ? new Vector4(1, .3f, .35f, 1) : ImGui.GetStyle().Colors[(int)ImGuiCol.Text], inventory.Available ? deficit.ToString("N0") : "?");
                }
                ImGui.EndTable();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException) { ImGui.TextWrapped("素材展開を中止しました: " + ex.Message); }
        EndPanel();
    }
    private void DrawIngredients(CraftRecipe recipe, long crafts, Dictionary<uint, CraftRecipe> producers, HashSet<uint> path, int depth = 0)
    {
        foreach (var ingredient in recipe.Ingredients)
        {
            if (Materials.IsCrystal(ingredient.Name)) continue;
            var amount = checked(ingredient.Amount * crafts);
            ImGui.PushStyleColor(ImGuiCol.Text, MaterialColor(ingredient.ItemId, depth > 0, depth > 0 ? basicMaterialColor : new Vector4(.94f, .94f, .97f, 1)));
            if (producers.TryGetValue(ingredient.ItemId, out var child) && path.Add(ingredient.ItemId))
            {
                bool open = ImGui.TreeNode($"{ingredient.Name} ×{amount:N0} [{child.Job}]##{ingredient.ItemId}");
                HoverMaterial(ingredient.ItemId);
                if (open)
                {
                    DrawIngredients(child, checked((amount + Math.Max(1, child.Yield) - 1) / Math.Max(1, child.Yield)), producers, path, depth + 1);
                    ImGui.TreePop();
                }
                path.Remove(ingredient.ItemId);
            }
            else { ImGui.TextUnformatted($"{ingredient.Name} ×{amount:N0}"); HoverMaterial(ingredient.ItemId); }
            ImGui.PopStyleColor();
        }
    }
    private Vector4 MaterialColor(uint itemId, bool basic, Vector4 fallback)
    {
        var inventory = p.Inventory;
        if (!inventory.Available || !expandedMaterials.TryGetValue(itemId, out var required)) return fallback;
        long owned = inventory.OnHand.GetValueOrDefault(itemId) + inventory.RetainerCount(itemId);
        return owned >= required.Amount ? (basic ? new Vector4(.4f, .86f, .54f, 1) : new Vector4(.38f, .68f, 1f, 1)) : fallback;
    }
    private bool IsMissing(uint itemId) => Plugin.Client.IsLoggedIn && p.Inventory.Available && expandedMaterials.TryGetValue(itemId, out var required)
        && p.Inventory.OnHand.GetValueOrDefault(itemId) + p.Inventory.RetainerCount(itemId) < required.Amount;
    private void HoverMaterial(uint itemId)
    {
        // Text has no interactive ID. Hit-test its visible rectangle immediately,
        // including when our own floating action window overlaps the parent window.
        if (hoverAlpha > 0 && ImGui.IsMouseHoveringRect(searchPopupMin, searchPopupMax, false)) return;
        if (!ImGui.IsItemVisible() || !ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), true)
            || !ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenOverlapped | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
            || !IsMissing(itemId)) return;
        if (hoveredMaterial != itemId) hoverAlpha = 0;
        hoveredMaterial = itemId;
        materialHoveredThisFrame = true;
        hoverAnchor = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y);
    }
    private void DrawMaterialSearch()
    {
        if (page != 2 || !IsOpen || collapsed || !IsMissing(hoveredMaterial)) { hoverAlpha = 0; hoveredMaterial = 0; return; }
        bool overButton = hoverAlpha > 0 && ImGui.IsMouseHoveringRect(searchPopupMin, searchPopupMax, false);
        if (!materialHoveredThisFrame && !overButton) { hoverAlpha = 0; hoveredMaterial = 0; return; }
        hoverAlpha = Math.Min(1, hoverAlpha + ImGui.GetIO().DeltaTime / .2f);
        ImGui.SetNextWindowPos(hoverAnchor);
        ImGui.SetNextWindowBgAlpha(.98f * hoverAlpha);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * hoverAlpha);
        if (ImGui.Begin("##BuildListsMaterialSearch", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
        {
            // Keep the action above its parent without stealing keyboard focus.
            ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
            searchPopupMin = ImGui.GetWindowPos(); searchPopupMax = searchPopupMin + ImGui.GetWindowSize();
            ImGui.BeginDisabled(ImGui.GetTime() < nextItemSearch || !IsMissing(hoveredMaterial));
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Search, "所持状況を確認")) SearchMaterial(hoveredMaterial);
            ImGui.EndDisabled();
        }
        ImGui.End(); ImGui.PopStyleVar();
    }
    private unsafe void SearchMaterial(uint itemId)
    {
        if (!IsMissing(itemId) || ImGui.GetTime() < nextItemSearch) return;
        try
        {
            var finder = ItemFinderModule.Instance();
            if (finder == null || finder->IsRequestUnfulfilled) { p.Journal.Warn("所持状況の確認が利用できないか、検索処理中です。"); return; }
            nextItemSearch = ImGui.GetTime() + 1;
            finder->SearchForItem(itemId, true);
            p.Journal.Info($"ゲームの所持状況を確認: {itemId}");
            hoverAlpha = 0; hoveredMaterial = 0;
        }
        catch (Exception ex) { p.Journal.Error("所持状況の確認に失敗", ex); }
    }
    private void DrawRetainerWarning()
    {
        if (!p.Inventory.RetainerRecordsIncomplete) return;
        float alpha = .25f + .75f * (.5f + .5f * MathF.Sin((float)ImGui.GetTime() * MathF.Tau / 1.2f));
        var pos = ImGui.GetCursorScreenPos();
        float size = ImGui.GetTextLineHeight();
        ImGui.GetWindowDrawList().AddCircle(pos + new Vector2(size * .5f), size * .27f, ImGui.GetColorU32(new Vector4(1, .2f, .25f, alpha)), 24, 2);
        ImGui.Dummy(new Vector2(size));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("リテイナー確認が完了していません。リテイナーの所持品を開いてください。");
        ImGui.SameLine();
    }
    private void ExportSettings()
    {
        if (!ImGui.CollapsingHeader("TSVの出力先設定")) return;
        if (ImGui.InputText("出力フォルダー", ref exportDirectory, 1000)) p.Journal.Info("TSV出力先を編集");
        if (ActionButton("出力先を保存"))
        {
            if (!System.IO.Path.IsPathFullyQualified(exportDirectory.Trim())) { notice = "出力先には絶対パスを指定してください。"; p.Journal.Warn(notice); }
            else
            {
                var old = p.Config.ExportDirectory; p.Config.ExportDirectory = exportDirectory.Trim();
                if (p.Save()) { notice = "TSV出力先を保存しました"; p.Journal.Info(notice); }
                else { p.Config.ExportDirectory = old; notice = "出力先を保存できませんでした。"; }
            }
        }
        ImGui.SameLine();
        if (ActionButton("標準のダウンロードに戻す"))
        {
            try { exportDirectory = TsvExport.DefaultDirectory(); p.Config.ExportDirectory = ""; p.Save(); p.Journal.Info("TSV出力先を標準に戻す"); }
            catch (Exception ex) { notice = ex.Message; p.Journal.Error("標準フォルダー取得失敗", ex); }
        }
    }
    private void Logs()
    {
        BeginPanel("logSettings", "ログの表示設定");
        ImGui.TextDisabled("INFO / WARN / ERROR — 直近3,000件 / ファイルにも保存");
        if (ImGui.InputTextWithHint("##logfilter", "ログ内を検索…", ref logFilter, 100)) p.Journal.Info("ログ検索条件変更");
        if (ImGui.Checkbox("末尾に追従", ref follow)) p.Journal.Info($"ログ追従: {follow}");
        ImGui.SameLine();
        if (ActionButton("コピー")) { p.Journal.Info("表示ログをコピー"); ImGui.SetClipboardText(string.Join("\n", p.Journal.Entries.Where(MatchLog).Select(FormatLog))); }
        ImGui.TextWrapped(p.Journal.Path);
        EndPanel();
        BeginPanel("logoutput", "操作ログ", true);
        foreach (var entry in p.Journal.Entries.Where(MatchLog))
            ImGui.TextColored(entry.Level == "ERROR" ? new(1, .45f, .5f, 1) : entry.Level == "WARN" ? new(1, .78f, .4f, 1) : new Vector4(.78f, .79f, .86f, 1), FormatLog(entry));
        if (follow) ImGui.SetScrollHereY(1);
        EndPanel();
    }
    private bool MatchLog(LogEntry entry) => FormatLog(entry).Contains(logFilter, StringComparison.OrdinalIgnoreCase);
    private static string FormatLog(LogEntry entry) => $"{entry.Time:HH:mm:ss.fff} [{entry.Level}] {entry.Message}";
}





