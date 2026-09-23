namespace WwanMessenger.Core.Sms;

/// <summary>
/// Reassembles concatenated SMS parts keyed by (sender, reference, total).
/// Shared by the AT and MBIM backends.
/// </summary>
public sealed class MultipartReassembler
{
    private readonly object _lock = new();
    private readonly Dictionary<string, State> _pending = new();
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private sealed class State
    {
        public int Total;
        public Dictionary<int, string> Parts = new();
        public DateTime LastTouchUtc = DateTime.UtcNow;
        public State(int total) => Total = total;
    }

    /// <summary>Add a part; returns the full text when the message completes, else null.</summary>
    public string? Add(DeliverSms deliver)
    {
        if (!deliver.IsMultipart)
            return deliver.Text;

        lock (_lock)
        {
            string key = $"{deliver.Sender}:{deliver.ConcatRef}:{deliver.ConcatTotal}";
            if (!_pending.TryGetValue(key, out var state))
            {
                state = new State(deliver.ConcatTotal);
                _pending[key] = state;
            }
            if (state.Parts.ContainsKey(deliver.ConcatSeq)) return null; // duplicate
            state.Parts[deliver.ConcatSeq] = deliver.Text;
            state.LastTouchUtc = DateTime.UtcNow;

            if (state.Parts.Count < state.Total) return null;
            _pending.Remove(key);
            var sb = new System.Text.StringBuilder();
            for (int s = 1; s <= state.Total; s++) sb.Append(state.Parts[s]);
            return sb.ToString();
        }
    }

    /// <summary>Flush stale partials (10+ minutes old) — returns their fragments with a marker.</summary>
    public List<(string Sender, string Text)> FlushStale()
    {
        lock (_lock)
        {
            var results = new List<(string, string)>();
            var stale = _pending.Where(kv => DateTime.UtcNow - kv.Value.LastTouchUtc > StaleAfter).ToList();
            foreach (var kv in stale)
            {
                var sb = new System.Text.StringBuilder();
                for (int s = 1; s <= kv.Value.Total; s++)
                    if (kv.Value.Parts.TryGetValue(s, out var t)) sb.Append(t);
                results.Add((kv.Key.Split(':')[0], sb.ToString() + " (incomplete)"));
                _pending.Remove(kv.Key);
            }
            return results;
        }
    }
}
