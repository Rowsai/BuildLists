using Dalamud.Game.Command;
using Dalamud.Game;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace BuildLists;
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface Interface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPlayerState Player { get; private set; } = null!;
    [PluginService] internal static IClientState Client { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    public Configuration Config { get; }
    public Journal Journal { get; }
    public Dictionary<uint, CraftRecipe> Recipes { get; } = [];
    public int RecipeRevision { get; private set; }
    public InventoryTracker Inventory { get; }
    private readonly WindowSystem windows = new("BuildLists");
    private readonly MainWindow window;
    public Plugin()
    {
        Journal = new Journal(Log, Interface.GetPluginConfigDirectory());
        Config = Interface.GetPluginConfig() as Configuration ?? new();
        LoadRecipes();
        Inventory = new InventoryTracker(this);
        window = new MainWindow(this);
        windows.AddWindow(window);
        Commands.AddHandler("/buildlists", new CommandInfo(OnCommand) { HelpMessage = "Build Lists を開く" });
        Interface.UiBuilder.Draw += windows.Draw;
        Interface.UiBuilder.OpenMainUi += Open;
        Interface.UiBuilder.OpenConfigUi += Open;
        Framework.Update += Inventory.Update;
        Journal.Info("Build Lists v1.0.0 起動 / API 15");
        Journal.Warn("リテイナー所持数は前回取得の記録です。各リテイナーの所持品を開くと更新されます。");
    }
    public void LoadRecipes()
    {
        try
        {
            var result = new Dictionary<uint, CraftRecipe>();
            // Recipe is language-neutral: resolve localized names from explicitly
            // Japanese sheets rather than following RowRef.Value's default language.
            var items = Data.GetExcelSheet<Item>(ClientLanguage.Japanese);
            var craftTypes = Data.GetExcelSheet<CraftType>(ClientLanguage.Japanese);
            foreach (var r in Data.GetExcelSheet<Recipe>())
            {
                if (r.ItemResult.RowId == 0) continue;
                var name = items.GetRow(r.ItemResult.RowId).Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var ingredients = new List<Ingredient>();
                for (var i = 0; i < r.Ingredient.Count; i++)
                {
                    var item = r.Ingredient[i];
                    if (item.RowId != 0 && r.AmountIngredient[i] > 0)
                        ingredients.Add(new Ingredient(item.RowId, items.GetRow(item.RowId).Name.ToString(), r.AmountIngredient[i]));
                }
                result[r.RowId] = new(r.RowId, name, craftTypes.GetRow(r.CraftType.RowId).Name.ToString(), r.AmountResult, ingredients, r.ItemResult.RowId);
            }
            Recipes.Clear();
            foreach (var pair in result) Recipes.Add(pair.Key, pair.Value);
            RecipeRevision++;
            Journal.Info($"レシピ読込完了: {Recipes.Count} 件 / 名称言語: 日本語");
            if (Recipes.Count == 0) Journal.Warn("製作レシピが0件です。ゲームデータとAPIのバージョンを確認してください。");
        }
        catch (Exception ex) { Journal.Error("レシピ読込失敗", ex); }
    }
    public bool Save()
    {
        try { Interface.SavePluginConfig(Config); Journal.Info("設定・製作リストを保存"); return true; }
        catch (Exception ex) { Journal.Error("保存失敗", ex); return false; }
    }
    private void OnCommand(string command, string args) { Journal.Info("コマンド実行 /buildlists"); Open(); }
    private void Open() { window.IsOpen = true; Journal.Info("ウィンドウを開く"); }
    public void Dispose()
    {
        Framework.Update -= Inventory.Update;
        Interface.UiBuilder.Draw -= windows.Draw;
        Interface.UiBuilder.OpenMainUi -= Open;
        Interface.UiBuilder.OpenConfigUi -= Open;
        Commands.RemoveHandler("/buildlists");
        windows.RemoveAllWindows();
        Save();
        Journal.Info("Build Lists 終了");
    }
}
