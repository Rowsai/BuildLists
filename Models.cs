using Dalamud.Configuration;

namespace BuildLists;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public List<SavedList> Lists { get; set; } = [];
    public List<CraftEntry> Draft { get; set; } = [];
    public string DraftName { get; set; } = "";
    public Guid? EditingListId { get; set; }
    public string ExportDirectory { get; set; } = "";
    public Dictionary<ulong, Dictionary<ulong, RetainerSnapshot>> Retainers { get; set; } = [];
}
public sealed class SavedList
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<CraftEntry> Entries { get; set; } = [];
}
public sealed class CraftEntry
{
    public uint RecipeId { get; set; }
    public int Quantity { get; set; } = 1;
}
public sealed class RetainerSnapshot
{
    public DateTimeOffset Updated { get; set; }
    public Dictionary<uint, long> Items { get; set; } = [];
}
public sealed record Ingredient(uint ItemId, string Name, int Amount);
public sealed record CraftRecipe(uint Id, string Name, string Job, int Yield, List<Ingredient> Ingredients, uint ResultItemId = 0);
public static class Materials
{
    public static bool IsCrystal(string name) => new[] { "ファイア", "アイス", "ウィンド", "アース", "ライトニング", "ウォーター" }
        .Any(element => name == element + "シャード" || name == element + "クリスタル" || name == element + "クラスター");
    public static List<(uint ItemId, int Depth)> DisplayOrder(IEnumerable<CraftEntry> entries, IReadOnlyDictionary<uint, CraftRecipe> recipes)
    {
        var roots = Aggregate(entries, recipes);
        var producers = Producers(recipes);
        var rows = new List<(uint, int)>();
        var crystals = new HashSet<uint>();
        var names = roots.ToDictionary(x => x.Key, x => x.Value.Name);
        foreach (var ingredient in recipes.Values.SelectMany(x => x.Ingredients)) names.TryAdd(ingredient.ItemId, ingredient.Name);
        void Append(uint itemId, int depth, HashSet<uint> path)
        {
            if (names.TryGetValue(itemId, out var name) && IsCrystal(name)) { crystals.Add(itemId); return; }
            if (depth > 128 || !path.Add(itemId)) throw new InvalidOperationException("素材の表示階層に循環または過度の深さがあります。");
            rows.Add((itemId, depth));
            if (producers.TryGetValue(itemId, out var recipe))
                foreach (var child in recipe.Ingredients.DistinctBy(x => x.ItemId)) Append(child.ItemId, depth + 1, path);
            path.Remove(itemId);
        }
        foreach (var root in roots) Append(root.Key, 0, []);
        rows.AddRange(crystals.OrderBy(x => x).Select(x => (x, 0)));
        return rows;
    }
    public static Dictionary<uint, CraftRecipe> Producers(IReadOnlyDictionary<uint, CraftRecipe> recipes) => recipes.Values
        .Where(x => x.ResultItemId != 0).OrderBy(x => x.Id).GroupBy(x => x.ResultItemId).ToDictionary(x => x.Key, x => x.First());

    public static Dictionary<uint, (string Name, long Amount)> Expand(IEnumerable<CraftEntry> entries, IReadOnlyDictionary<uint, CraftRecipe> recipes)
    {
        var amounts = Aggregate(entries, recipes);
        var producers = Producers(recipes);
        var order = new List<uint>();
        var states = new Dictionary<uint, int>();
        void Visit(uint item, int depth = 0)
        {
            if (depth > 128) throw new InvalidOperationException("素材レシピの階層が深すぎます。");
            if (states.TryGetValue(item, out var state))
            {
                if (state == 1) throw new InvalidOperationException($"素材レシピに循環があります（アイテムID {item}）。");
                return;
            }
            states[item] = 1;
            if (producers.TryGetValue(item, out var recipe))
                foreach (var material in recipe.Ingredients) Visit(material.ItemId, depth + 1);
            states[item] = 2; order.Add(item);
        }
        foreach (var item in amounts.Keys.ToArray()) Visit(item);
        // Parents first: every shared intermediate has its total demand before rounding.
        order.Reverse();
        foreach (var item in order)
        {
            if (!producers.TryGetValue(item, out var recipe)) continue;
            var amount = amounts[item].Amount;
            var yield = Math.Max(1, recipe.Yield);
            var crafts = checked((amount + yield - 1) / yield);
            foreach (var material in recipe.Ingredients)
            {
                amounts.TryGetValue(material.ItemId, out var old);
                amounts[material.ItemId] = (material.Name, checked(old.Amount + checked(crafts * material.Amount)));
            }
        }
        return amounts;
    }
    public static long Crafts(int quantity, int yield) => ((long)Math.Max(1, quantity) + Math.Max(1, yield) - 1) / Math.Max(1, yield);
    public static Dictionary<uint, (string Name, long Amount)> Aggregate(IEnumerable<CraftEntry> entries, IReadOnlyDictionary<uint, CraftRecipe> recipes)
    {
        var result = new Dictionary<uint, (string, long)>();
        // Merge identical recipes before rounding to avoid wasting multi-output crafts.
        foreach (var group in entries.GroupBy(x => x.RecipeId))
        {
            if (!recipes.TryGetValue(group.Key, out var recipe)) throw new InvalidOperationException($"レシピ {group.Key} が見つかりません。再選択してください。");
            long quantity = group.Sum(x => (long)Math.Max(1, x.Quantity));
            long crafts = (quantity + Math.Max(1, recipe.Yield) - 1) / Math.Max(1, recipe.Yield);
            foreach (var ingredient in recipe.Ingredients)
            {
                result.TryGetValue(ingredient.ItemId, out var old);
                result[ingredient.ItemId] = (ingredient.Name, checked(old.Item2 + crafts * ingredient.Amount));
            }
        }
        return result;
    }
}

public static class ListEditor
{
    public static List<CraftEntry> Copy(IEnumerable<CraftEntry> entries) => entries.Select(x => new CraftEntry { RecipeId = x.RecipeId, Quantity = x.Quantity }).ToList();
    public static void Load(Configuration config, SavedList? list)
    {
        config.EditingListId = list?.Id;
        config.DraftName = list?.Name ?? "";
        config.Draft = list == null ? [] : Copy(list.Entries);
    }
    public static bool Dirty(Configuration config)
    {
        var list = config.Lists.FirstOrDefault(x => x.Id == config.EditingListId);
        return list == null ? config.Draft.Count > 0 || config.DraftName.Length > 0 :
            list.Name != config.DraftName || !list.Entries.Select(x => (x.RecipeId, x.Quantity)).SequenceEqual(config.Draft.Select(x => (x.RecipeId, x.Quantity)));
    }
    public static bool CanSave(Configuration config) => config.Draft.Count > 0 && !string.IsNullOrWhiteSpace(config.DraftName) &&
        !config.Lists.Any(x => x.Id != config.EditingListId && string.Equals(x.Name, config.DraftName.Trim(), StringComparison.OrdinalIgnoreCase));
    public static bool Save(Configuration config, Func<bool> persist)
    {
        if (!CanSave(config)) return false;
        var previousId = config.EditingListId;
        var previousName = config.DraftName;
        var existing = config.Lists.FirstOrDefault(x => x.Id == previousId);
        var updated = new SavedList { Id = existing?.Id ?? Guid.NewGuid(), Name = config.DraftName.Trim(), Entries = Copy(config.Draft) };
        int index = existing == null ? config.Lists.Count : config.Lists.IndexOf(existing);
        if (existing == null) config.Lists.Add(updated); else config.Lists[index] = updated;
        config.EditingListId = updated.Id;
        config.DraftName = updated.Name;
        if (persist()) return true;
        if (existing == null) config.Lists.RemoveAt(index); else config.Lists[index] = existing;
        config.EditingListId = previousId;
        config.DraftName = previousName;
        return false;
    }
    public static bool Delete(Configuration config, Guid id, Func<bool> persist)
    {
        int index = config.Lists.FindIndex(x => x.Id == id);
        if (index < 0) return false;
        var old = config.Lists[index];
        var draft = config.Draft; var name = config.DraftName; var editing = config.EditingListId;
        config.Lists.RemoveAt(index);
        if (config.EditingListId == id) Load(config, null);
        if (persist()) return true;
        config.Lists.Insert(index, old); config.Draft = draft; config.DraftName = name; config.EditingListId = editing;
        return false;
    }
    public static bool AlreadyAdded(CraftRecipe recipe, Configuration config, IReadOnlyDictionary<uint, CraftRecipe> recipes) => config.Draft.Any(x =>
        x.RecipeId == recipe.Id || (recipe.ResultItemId != 0 && recipes.TryGetValue(x.RecipeId, out var existing) && existing.ResultItemId == recipe.ResultItemId));
}
