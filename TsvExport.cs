using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace BuildLists;
public static class TsvExport
{
    // Windows Known Folder API also respects a Downloads folder relocated by the user.
    public static string DefaultDirectory()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        nint address = 0;
        try
        {
            Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref downloads, 0, 0, out address));
            return Marshal.PtrToStringUni(address) ?? throw new IOException("ダウンロードフォルダーを取得できません。");
        }
        finally { if (address != 0) Marshal.FreeCoTaskMem(address); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, nint token, out nint path);
    public static string Cell(string value)
    {
        // Prevent spreadsheet software from interpreting user-controlled names as formulas.
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public static string Write(string directory, string listName, Dictionary<uint, (string Name, long Amount)> materials,
        IReadOnlyDictionary<uint, long>? bags, IReadOnlyDictionary<uint, long> retainers, int snapshotCount, DateTimeOffset? oldest, IReadOnlyList<(uint ItemId, int Depth)>? displayOrder = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new IOException("出力先には絶対パスを指定してください。");
        Directory.CreateDirectory(directory);
        var safeName = new string(listName.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).Take(60).ToArray());
        var path = Path.Combine(directory, $"BuildLists_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.tsv");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file, new UTF8Encoding(true));
        writer.NewLine = "\r\n";
        writer.WriteLine("素材名\t手持ち/全体必要数\tバッグ\tリテイナー記録\t全体不足");
        string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        var order = displayOrder ?? materials.OrderBy(x => Materials.IsCrystal(x.Value.Name)).ThenBy(x => x.Value.Name).Select(x => (x.Key, 0)).ToArray();
        foreach (var display in order)
        {
            var row = materials[display.ItemId];
            long b = bags?.GetValueOrDefault(display.ItemId) ?? 0, r = retainers.GetValueOrDefault(display.ItemId);
            string[] values = [row.Name, (bags == null ? "?" : Number(b + r)) + " / " + Number(row.Amount),
                bags == null ? "未取得" : Number(b), snapshotCount == 0 ? "未取得" : Number(r),
                bags == null ? "?" : Number(Math.Max(0, row.Amount - b - r))];
            writer.WriteLine(string.Join("\t", values.Select(Cell)));
        }
        return path;
    }
}

