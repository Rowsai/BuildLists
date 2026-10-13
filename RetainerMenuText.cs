namespace BuildLists;

internal static class RetainerMenuText
{
    public static bool MatchesAction(uint row, string actual, string expected)
    {
        if (Matches(actual, expected)) return true;
        // Observed Japanese labels: the slot count is runtime data absent from sheet text.
        var text = actual.Trim();
        if (row == 2383) return Matches(text, "リテイナーを帰す");
        if (row != 2378) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(text,
            @"^アイテムの受け渡し(?:\s*[\[［]預託中[：:]\s*[0-9,]+枠[\]］])?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
    // Both inputs must be decoded SeString text, not the encoded sheet representation.
    public static bool Matches(string actual, string expected)
    {
        static string Normalize(string value) => new(value.Where(c => !char.IsWhiteSpace(c) && c != '\u200B').ToArray());
        var text = Normalize(expected);
        return text.Length > 0 && string.Equals(Normalize(actual), text, StringComparison.Ordinal);
    }
}
