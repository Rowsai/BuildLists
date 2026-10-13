using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BuildLists;
public sealed class MainWindow : Window
{
    private readonly Plugin p;
    private int page;
    private string query = "", listFilter = "", logFilter = "", notice = "", exportDirectory = "";
    private Guid? pendingEdit, pendingDelete;
    private bool requestSwitch;
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
    public MainWindow(Plugin plugin) : base("Build Lists###BuildLists")
    {
        p = plugin; Size = new Vector2(1080, 740); SizeCondition = ImGuiCond.FirstUseEver;
        exportDirectory = p.Config.ExportDirectory;
        if (string.IsNullOrWhiteSpace(exportDirectory))
        {
            try { exportDirectory = CsvExport.DefaultDirectory(); }
            catch (Exception ex) { p.Journal.Error("ダウンロードフォルダー取得失敗", ex); }
        }
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new(760, 520), MaximumSize = new(float.MaxValue) };
    }
    public override void OnClose() => p.Journal.Info("ウィンドウを閉じる");
    public override void PreDraw()
    {
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
        var iconPath = System.IO.Path.Combine(Plugin.Interface.AssemblyLocation.Directory!.FullName, "icon.png");
        var icon = Plugin.Textures.GetFromFile(iconPath).GetWrapOrDefault();
        if (icon != null) { ImGui.Image(icon.Handle, new Vector2(40)); ImGui.SameLine(); }
        ImGui.TextColored(accent, "BUILD LISTS   /   Crafting planner");
        ImGui.Separator();
        ImGui.BeginChild("navigation", new Vector2(145, 0), true);
        for (var i = 0; i < pages.Length; i++)
            if (ImGui.Selectable(pages[i], page == i, ImGuiSelectableFlags.None, new Vector2(125, 58))) { page = i; p.Journal.Info($"画面切替: {pages[i]}"); }
        ImGui.Spacing(); ImGui.TextDisabled("v1.0.0 / API 15");
        ImGui.EndChild(); ImGui.SameLine();
        ImGui.BeginChild("content", Vector2.Zero, false);
        ImGui.TextColored(accent, pages[page]); ImGui.Spacing();
        if (notice.Length > 0) ImGui.TextWrapped(notice);
        switch (page) { case 0: Home(); break; case 1: Crafting(); break; case 2: Stock(); break; case 3: Logs(); break; }
        DrawDialogs();
        ImGui.EndChild();
    }
    private void Home()
    {
        ImGui.TextWrapped("作りたいものを選んで、必要な素材をひとつに。製作計画を保存し、所持品と照らし合わせて準備できます。");
        ImGui.Spacing();
        ImGui.TextColored(accent, $"{p.Recipes.Count:N0} レシピ    /    {p.Config.Lists.Count} 保存済みリスト");
        ImGui.Spacing(); ImGui.Separator(); ImGui.TextUnformatted("アップデート履歴");
        ImGui.BeginChild("history", new Vector2(0, 190), true);
        ImGui.TextColored(accent, "v1.0.0  —  初回リリース");
        ImGui.TextWrapped("・日本語アイテム検索と候補表示\n・個数指定、素材表示、名前を付けてリスト保存\n・必要素材の合算と所持数比較\n・リテイナー所持品の取得記録\n・INFO / WARN / ERROR の操作ログ");
        ImGui.EndChild();
        ImGui.TextWrapped("リテイナー所持数を記録するには、各リテイナーの所持品を開き、数秒待ってください。閉じている間は前回の記録を使用します。");
        if (ImGui.Button("レシピを再読み込み")) { p.Journal.Info("レシピ再読込ボタン"); p.LoadRecipes(); }
    }
    private void Crafting()
    {
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
        if (ImGui.Button(editing == null ? "Save" : "Save（上書き）"))
        {
            if (ListEditor.Save(p.Config, p.Save)) { selectedList = p.Config.EditingListId!.Value; notice = $"「{p.Config.DraftName}」を保存しました"; p.Journal.Info("製作リスト保存 / " + p.Config.DraftName); }
            else notice = "保存に失敗しました。ログを確認してください。";
        }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("新規リスト")) RequestEdit(null);
        if (editing != null) { ImGui.SameLine(); if (ImGui.Button("リスト削除")) pendingDelete = editing.Id; }
        if (ListEditor.Dirty(p.Config)) ImGui.TextDisabled("未保存の変更があります");
        if (!ListEditor.CanSave(p.Config)) ImGui.TextDisabled("名前と製作対象を指定してください。同名の別リストは保存できません。");
        ImGui.Separator();
        ImGui.TextUnformatted("作りたいアイテムを検索・選択してください");
        ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X - 65));
        if (ImGui.InputTextWithHint("##search", "アイテム名を入力…", ref query, 200))
        {
            selected = 0; suggestions = true; Search(); p.Journal.Info($"検索入力: {query} / 候補 {matches.Length} 件（最大80件）");
        }
        ImGui.SameLine();
        if (ImGui.Button("v##suggest")) { suggestions = !suggestions; Search(); p.Journal.Info("候補リスト開閉"); }
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
        if (ImGui.Button("Add"))
        {
            p.Config.Draft.Add(new CraftEntry { RecipeId = selected, Quantity = quantity });
            p.Journal.Info($"製作対象追加: {selected} ×{quantity}"); p.Save();
            selected = 0; query = ""; Search();
        }
        ImGui.EndDisabled();
        ImGui.Separator(); ImGui.TextUnformatted("選択済みの制作対象アイテム");
        ImGui.BeginChild("draft", Vector2.Zero, true);
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
            if (ImGui.SmallButton("削除")) { p.Config.Draft.Remove(entry); Search(); p.Journal.Info($"製作対象削除: {entry.RecipeId}"); p.Save(); }
            ImGui.Separator(); ImGui.PopID();
        }
        ImGui.EndChild();
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
            if (ImGui.Button("破棄して切り替え")) { LoadEditor(pendingEdit); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("キャンセル")) { p.Journal.Info("リスト切替キャンセル"); ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        if (pendingDelete != null && !ImGui.IsPopupOpen("リスト削除の確認")) ImGui.OpenPopup("リスト削除の確認");
        if (ImGui.BeginPopupModal("リスト削除の確認", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var list = p.Config.Lists.FirstOrDefault(x => x.Id == pendingDelete);
            ImGui.TextUnformatted($"「{list?.Name}」を削除しますか？");
            ImGui.TextUnformatted("このリストの編集中の変更も削除されます。");
            if (ImGui.Button("削除する"))
            {
                if (pendingDelete is Guid id && ListEditor.Delete(p.Config, id, p.Save))
                {
                    if (selectedList == id) selectedList = Guid.Empty;
                    notice = "リストを削除しました"; p.Journal.Info("製作リスト削除: " + list?.Name); Search();
                }
                else notice = "削除の保存に失敗しました。ログを確認してください。";
                pendingDelete = null; ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(); if (ImGui.Button("キャンセル")) { pendingDelete = null; p.Journal.Info("リスト削除キャンセル"); ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
    }
    private void Stock()
    {
        if (ImGui.InputTextWithHint("##listfilter", "保存したリストを検索…", ref listFilter, 100)) p.Journal.Info($"保存リスト検索: {listFilter}");
        var saved = p.Config.Lists.FirstOrDefault(x => x.Id == selectedList);
        if (ImGui.BeginCombo("制作リスト", saved?.Name ?? "リストを選択してください"))
        {
            foreach (var list in p.Config.Lists.Where(x => x.Name.Contains(listFilter, StringComparison.OrdinalIgnoreCase)))
                if (ImGui.Selectable(list.Name + "##" + list.Id, list.Id == selectedList)) { selectedList = list.Id; p.Journal.Info($"素材リスト選択: {list.Name}"); }
            ImGui.EndCombo();
        }
        var inventory = p.Inventory;
        ImGui.TextWrapped($"リテイナー記録: {inventory.Snapshots.Count} 人 / 登録確認 {inventory.KnownRetainers} 人。所持品を開くと更新されます。");
        if (inventory.Snapshots.Count > 0) ImGui.TextDisabled($"最も古い記録: {inventory.Snapshots.Values.Min(x => x.Updated).ToLocalTime():yyyy/MM/dd HH:mm}");
        ImGui.TextWrapped("手持ち = インベントリ（クリスタルを含む・NQ/HQ合計）+ リテイナーの前回記録。未取得のリテイナー分は含まれません。");
        if (!inventory.Available) ImGui.TextColored(accent, "インベントリ未取得 / ログイン後に更新されます");
        if (saved == null) { ImGui.TextDisabled("制作リストで保存したリストを選択してください"); return; }
        if (ImGui.Button("このリストを編集")) RequestEdit(saved.Id);
        ImGui.SameLine(); if (ImGui.Button("リスト削除")) pendingDelete = saved.Id;
        ExportSettings();
        ImGui.TextWrapped("中間素材もすべて自作する場合の必要数です。所持済み中間素材は展開数から差し引きません。複数レシピがある素材はレシピID順の先頭を使用します。");
        ImGui.BeginChild("stock", Vector2.Zero, true);
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
            ImGui.TextDisabled("シャード・クリスタル・クラスターは末尾に合算して表示します。");
            ImGui.TextWrapped("中間素材 → その基本素材の順に表示します。共通素材は各箇所に再表示しますが、必要数・不足数はリスト全体の合算値です。");
            if (ImGui.Button("CSV出力"))
            {
                try
                {
                    var snapshots = inventory.Snapshots;
                    var ret = materials.Keys.ToDictionary(x => x, x => snapshots.Values.Sum(s => s.Items.GetValueOrDefault(x)));
                    var path = CsvExport.Write(exportDirectory.Trim(), saved.Name, materials, inventory.Available ? inventory.OnHand : null, ret,
                        snapshots.Count, snapshots.Count == 0 ? null : snapshots.Values.Min(x => x.Updated));
                    notice = "CSVを出力しました: " + path; p.Journal.Info(notice);
                }
                catch (Exception ex) { notice = "CSV出力に失敗しました: " + ex.Message; p.Journal.Error("CSV出力失敗", ex); }
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
                ImGui.TextDisabled("階層表示は対象別の内訳です。共通素材のまとめ製作による合算値は下表・CSVを参照してください。");
            }
            if (ImGui.BeginTable("stocktable", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            {
                ImGui.TableSetupColumn("素材名"); ImGui.TableSetupColumn("手持ち / 全体必要数"); ImGui.TableSetupColumn("バッグ"); ImGui.TableSetupColumn("リテイナー記録"); ImGui.TableSetupColumn("全体不足（記録基準）"); ImGui.TableHeadersRow();
                foreach (var row in materialDisplayOrder)
                {
                    var pair = new KeyValuePair<uint, (string Name, long Amount)>(row.ItemId, materials[row.ItemId]);
                    long bags = inventory.OnHand.GetValueOrDefault(pair.Key), ret = inventory.RetainerCount(pair.Key), total = bags + ret;
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    if (row.Depth == 0) ImGui.TextUnformatted(pair.Value.Name);
                    else ImGui.TextColored(basicMaterialColor, new string(' ', Math.Min(row.Depth, 8) * 2) + "└ " + pair.Value.Name);
                    ImGui.TableNextColumn(); ImGui.TextColored(inventory.Available && total >= pair.Value.Amount ? new Vector4(.5f, .85f, .69f, 1) : accent, $"{(inventory.Available ? total.ToString("N0") : "?")} / {pair.Value.Amount:N0}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(inventory.Available ? bags.ToString("N0") : "未取得");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(inventory.Snapshots.Count > 0 ? ret.ToString("N0") : "未取得");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(inventory.Available ? Math.Max(0, pair.Value.Amount - total).ToString("N0") : "?");
                }
                ImGui.EndTable();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException) { ImGui.TextWrapped("素材展開を中止しました: " + ex.Message); }
        ImGui.EndChild();
    }
    private void DrawIngredients(CraftRecipe recipe, long crafts, Dictionary<uint, CraftRecipe> producers, HashSet<uint> path, int depth = 0)
    {
        foreach (var ingredient in recipe.Ingredients)
        {
            if (Materials.IsCrystal(ingredient.Name)) continue;
            var amount = checked(ingredient.Amount * crafts);
            if (depth > 0) ImGui.PushStyleColor(ImGuiCol.Text, basicMaterialColor);
            if (producers.TryGetValue(ingredient.ItemId, out var child) && path.Add(ingredient.ItemId))
            {
                if (ImGui.TreeNode($"{ingredient.Name} ×{amount:N0} [{child.Job}]##{ingredient.ItemId}"))
                {
                    DrawIngredients(child, checked((amount + Math.Max(1, child.Yield) - 1) / Math.Max(1, child.Yield)), producers, path, depth + 1);
                    ImGui.TreePop();
                }
                path.Remove(ingredient.ItemId);
            }
            else ImGui.TextUnformatted($"{ingredient.Name} ×{amount:N0}");
            if (depth > 0) ImGui.PopStyleColor();
        }
    }
    private void ExportSettings()
    {
        if (!ImGui.CollapsingHeader("CSVの出力先設定")) return;
        if (ImGui.InputText("出力フォルダー", ref exportDirectory, 1000)) p.Journal.Info("CSV出力先を編集");
        if (ImGui.Button("出力先を保存"))
        {
            if (!System.IO.Path.IsPathFullyQualified(exportDirectory.Trim())) { notice = "出力先には絶対パスを指定してください。"; p.Journal.Warn(notice); }
            else
            {
                var old = p.Config.ExportDirectory; p.Config.ExportDirectory = exportDirectory.Trim();
                if (p.Save()) { notice = "CSV出力先を保存しました"; p.Journal.Info(notice); }
                else { p.Config.ExportDirectory = old; notice = "出力先を保存できませんでした。"; }
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("標準のダウンロードに戻す"))
        {
            try { exportDirectory = CsvExport.DefaultDirectory(); p.Config.ExportDirectory = ""; p.Save(); p.Journal.Info("CSV出力先を標準に戻す"); }
            catch (Exception ex) { notice = ex.Message; p.Journal.Error("標準フォルダー取得失敗", ex); }
        }
    }
    private void Logs()
    {
        ImGui.TextDisabled("INFO / WARN / ERROR — 直近3,000件 / ファイルにも保存");
        if (ImGui.InputTextWithHint("##logfilter", "ログ内を検索…", ref logFilter, 100)) p.Journal.Info("ログ検索条件変更");
        if (ImGui.Checkbox("末尾に追従", ref follow)) p.Journal.Info($"ログ追従: {follow}");
        ImGui.SameLine();
        if (ImGui.Button("コピー")) { p.Journal.Info("表示ログをコピー"); ImGui.SetClipboardText(string.Join("\n", p.Journal.Entries.Where(MatchLog).Select(FormatLog))); }
        ImGui.TextWrapped(p.Journal.Path);
        ImGui.BeginChild("logoutput", Vector2.Zero, true, ImGuiWindowFlags.HorizontalScrollbar);
        foreach (var entry in p.Journal.Entries.Where(MatchLog))
            ImGui.TextColored(entry.Level == "ERROR" ? new(1, .45f, .5f, 1) : entry.Level == "WARN" ? new(1, .78f, .4f, 1) : new Vector4(.78f, .79f, .86f, 1), FormatLog(entry));
        if (follow) ImGui.SetScrollHereY(1);
        ImGui.EndChild();
    }
    private bool MatchLog(LogEntry entry) => FormatLog(entry).Contains(logFilter, StringComparison.OrdinalIgnoreCase);
    private static string FormatLog(LogEntry entry) => $"{entry.Time:HH:mm:ss.fff} [{entry.Level}] {entry.Message}";
}
