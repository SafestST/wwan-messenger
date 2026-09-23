using System.Text;

namespace WwanMessenger.Core.Sms;

public sealed class SegmentPlan
{
    public bool UseUcs2 { get; init; }
    public List<string> Parts { get; init; } = new();
    public int Total => Parts.Count;

    /// <summary>Compose-bar counter, e.g. "GSM7 · 2 SEG · 118/306".</summary>
    public string Describe(string text)
    {
        if (UseUcs2)
        {
            int cap = Total == 1 ? 70 : 67 * Total;
            return $"UCS2 · {Total} SEG · {text.Length}/{cap}";
        }
        int septets = SmsCodecs.SeptetCount(text);
        if (septets < 0) septets = text.Length;
        int capG = Total == 1 ? 160 : 153 * Total;
        return $"GSM7 · {Total} SEG · {septets}/{capG}";
    }
}

/// <summary>Splits text into SMS segments per GSM 03.40 limits
/// (160/153 GSM-7, 70/67 UCS-2), auto-selecting the alphabet.
/// GSM-7 splitting is by septet budget so extension chars (2 septets) can't overflow a part.</summary>
public static class Segmenter
{
    public static SegmentPlan Plan(string text)
    {
        if (text.Length == 0)
            return new SegmentPlan { UseUcs2 = false, Parts = new List<string>() };

        if (SmsCodecs.IsGsm7Representable(text))
        {
            if (SmsCodecs.SeptetCount(text) <= 160)
                return new SegmentPlan { UseUcs2 = false, Parts = new List<string> { text } };

            const int budget = 153;
            var parts = new List<string>();
            var current = new StringBuilder();
            int used = 0;
            foreach (var ch in text)
            {
                int cost = SmsCodecs.SeptetCost(ch);
                if (used + cost > budget)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    used = 0;
                }
                current.Append(ch);
                used += cost;
            }
            if (current.Length > 0) parts.Add(current.ToString());
            return new SegmentPlan { UseUcs2 = false, Parts = parts };
        }
        else
        {
            const int single = 70, part = 67;
            if (text.Length <= single)
                return new SegmentPlan { UseUcs2 = true, Parts = new List<string> { text } };

            var parts = new List<string>();
            for (int i = 0; i < text.Length; i += part)
                parts.Add(text.Substring(i, Math.Min(part, text.Length - i)));
            return new SegmentPlan { UseUcs2 = true, Parts = parts };
        }
    }
}
