using BuildLists;
using System.Text.Json;

var recipes = new Dictionary<uint, CraftRecipe>
{
    [1] = new(1, "糸", "裁縫", 3, [new(10, "草", 2), new(11, "シャード", 1)]),
    [2] = new(2, "布", "裁縫", 1, [new(10, "草", 4), new(12, "クリスタル", 2)]),
};
int passed = 0;
void Check(string name, bool condition) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
CraftEntry Entry(uint id, int qty) => new() { RecipeId = id, Quantity = qty };
Check("multi-output rounding", Materials.Crafts(4, 3) == 2 && Materials.Crafts(3, 3) == 1);
var result = Materials.Aggregate([Entry(1, 4), Entry(2, 2)], recipes);
Check("shared materials sum across recipes", result[10].Amount == 12);
Check("crystals and shards included", result[11].Amount == 2 && result[12].Amount == 4);
Check("duplicate recipes combined before rounding", Materials.Aggregate([Entry(1, 1), Entry(1, 2)], recipes)[10].Amount == 2);
Check("empty list", Materials.Aggregate([], recipes).Count == 0);
bool missingRejected = false;
try { Materials.Aggregate([Entry(999, 1)], recipes); } catch (InvalidOperationException) { missingRejected = true; }
Check("missing recipes never silently undercount", missingRejected);
Check("large quantities avoid int overflow", Materials.Aggregate([Entry(2, int.MaxValue), Entry(2, int.MaxValue)], recipes)[10].Amount == 17179869176L);
var config = new Configuration
{
    Draft = [Entry(1, 4)],
    Lists = [new() { Name = "日本語の製作リスト", Entries = [Entry(2, 2)] }],
    Retainers = new() { [123] = new() { [456] = new() { Updated = DateTimeOffset.UtcNow, Items = new() { [10] = 90 } } }, [789] = new() { [999] = new() { Items = new() { [10] = 5 } } } }
};
var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config))!;
Check("named list and draft persistence", restored.Lists[0].Name == "日本語の製作リスト" && restored.Draft[0].Quantity == 4);
Check("retainer cache separated by character", restored.Retainers[123][456].Items[10] == 90 && restored.Retainers[789][999].Items[10] == 5);
var nested = new Dictionary<uint, CraftRecipe>
{
    [1] = new(1, "完成品", "職", 1, [new(20, "中間A", 1), new(30, "中間B", 1), new(40, "共通素材", 1)], 100),
    [2] = new(2, "中間A", "職", 1, [new(40, "共通素材", 1)], 20),
    [3] = new(3, "中間B", "職", 1, [new(40, "共通素材", 1)], 30),
    [4] = new(4, "共通素材", "職", 3, [new(50, "原材料", 2), new(60, "シャード", 1)], 40),
    [5] = new(5, "共通素材別レシピ", "別職", 1, [new(50, "原材料", 99)], 40),
};
var expanded = Materials.Expand([Entry(1, 1)], nested);
var crystalRecipes = new Dictionary<uint, CraftRecipe>
{
    [1] = new(1, "完成品", "職", 1, [new(20, "中間素材", 2), new(701, "ファイアシャード", 3)], 100),
    [2] = new(2, "中間素材", "職", 1, [new(701, "ファイアシャード", 4), new(702, "アイスクリスタル", 1), new(50, "原材料", 2)], 20),
};
var crystalRows = Materials.DisplayOrder([Entry(1, 1)], crystalRecipes);
Check("crystals deduplicated at end", crystalRows.Select(x => x.ItemId).SequenceEqual(new uint[] {20, 50, 701, 702}));
Check("crystal totals include direct and intermediate use", Materials.Expand([Entry(1, 1)], crystalRecipes)[701].Amount == 11);
Check("only elemental crafting crystals classified", Materials.IsCrystal("ウォータークラスター") && !Materials.IsCrystal("特殊クリスタル"));
var display = Materials.DisplayOrder([Entry(1, 1)], nested);
Check("parent followed immediately by its descendants", display.Select(x => x.ItemId).SequenceEqual(new uint[] {20, 40, 50, 60, 30, 40, 50, 60, 40, 50, 60}));
Check("shared materials repeat under each parent with correct depth", display[1].Depth == 1 && display[2].Depth == 2 && display[4].Depth == 0 && display[8].Depth == 0);
Check("all intermediate levels retained", expanded[20].Amount == 1 && expanded[30].Amount == 1 && expanded[40].Amount == 3);
Check("shared direct/indirect demand rounded once", expanded[50].Amount == 2 && expanded[60].Amount == 1);
Check("deterministic producer selection", Materials.Producers(nested)[40].Id == 4);
Check("recursive multi-output rounding", Materials.Expand([Entry(1, 2)], nested)[50].Amount == 4);
var cyclic = new Dictionary<uint, CraftRecipe>(nested) { [4] = new(4, "共通素材", "職", 1, [new(20, "中間A", 1)], 40) };
bool cycleRejected = false;
try { Materials.Expand([Entry(1, 1)], cyclic); } catch (InvalidOperationException) { cycleRejected = true; }
Check("cycle rejected", cycleRejected);

var editor = new Configuration { DraftName = "  計画A  ", Draft = [Entry(1, 2)] };
Check("save new named list", ListEditor.Save(editor, () => true) && editor.Lists.Count == 1 && editor.Lists[0].Name == "計画A");
var original = editor.Lists[0];
ListEditor.Load(editor, original); editor.Draft[0].Quantity = 5;
Check("edit isolates stored list", original.Entries[0].Quantity == 2 && ListEditor.Dirty(editor));
Check("update preserves identity", ListEditor.Save(editor, () => true) && editor.Lists.Count == 1 && editor.Lists[0].Id == original.Id && editor.Lists[0].Entries[0].Quantity == 5);
editor.Draft[0].Quantity = 7;
Check("failed save restores stored contents", !ListEditor.Save(editor, () => false) && editor.Lists[0].Entries[0].Quantity == 5 && editor.Draft[0].Quantity == 7);
editor.Lists.Add(new SavedList { Name = "計画B" }); editor.DraftName = "計画B";
Check("duplicate rename rejected", !ListEditor.CanSave(editor));
editor.DraftName = "計画A";
Check("added item excluded including alternate recipe", ListEditor.AlreadyAdded(nested[1], editor, nested));
editor.Draft = [Entry(4, 1)];
Check("same result from another recipe excluded", ListEditor.AlreadyAdded(nested[5], editor, nested));
editor.Draft.Clear();
Check("removed item eligible again", !ListEditor.AlreadyAdded(nested[5], editor, nested));
editor.Draft = [Entry(4, 1)];
Check("failed delete rollback", !ListEditor.Delete(editor, original.Id, () => false) && editor.Lists.Any(x => x.Id == original.Id) && editor.Draft.Count == 1);
Check("delete clears linked draft only", ListEditor.Delete(editor, original.Id, () => true) && editor.Draft.Count == 0 && editor.EditingListId == null && editor.Lists.Count == 1);

Check("CSV escaping and formula neutralization", CsvExport.Cell("a,\"b\"\n日本語") == "\"a,\"\"b\"\"\n日本語\"" && CsvExport.Cell("=1+1") == "\"'=1+1\"");
var testDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "BuildListsTests", Guid.NewGuid().ToString("N")));
var csv = CsvExport.Write(testDirectory, "テスト,\"計画\"", expanded, new Dictionary<uint, long> { [50] = 1 }, new Dictionary<uint, long>(), 0, null);
var csvBytes = File.ReadAllBytes(csv);
Check("CSV UTF8 BOM and recursive rows", csvBytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }) && File.ReadAllText(csv).Contains("原材料") && File.ReadAllLines(csv).Length == expanded.Count + 1);
var csv2 = CsvExport.Write(testDirectory, "テスト,\"計画\"", expanded, null, new Dictionary<uint, long>(), 0, null);
Check("CSV no overwrite and unknown inventory", csv != csv2 && File.ReadAllText(csv2).Contains("未取得"));
bool relativeRejected = false;
try { CsvExport.Write("relative", "test", expanded, null, new Dictionary<uint, long>(), 0, null); } catch (IOException) { relativeRejected = true; }
Check("relative export path rejected", relativeRejected);
if (OperatingSystem.IsWindows()) Check("Windows Downloads known folder resolves", Path.IsPathFullyQualified(CsvExport.DefaultDirectory()));
Console.WriteLine($"{passed} checks passed. Native game integration is not exercised by these tests.");

// Minimal interface shim only for the isolated domain-model test executable.
namespace Dalamud.Configuration { public interface IPluginConfiguration { int Version { get; set; } } }
