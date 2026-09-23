namespace WwanMessenger.Core.At;

public enum AtLineKind { Tx, Rx, Urc, Info }

public sealed class AtResponse
{
    public bool Success { get; init; }
    public string Final { get; init; } = "";
    public List<string> Lines { get; init; } = new();

    public int? CmsErrorCode
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(Final, @"\+CMS ERROR:\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
    }

    public int? CmeErrorCode
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(Final, @"\+CME ERROR:\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
    }

    public string? InfoLine(string prefix)
        => Lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
