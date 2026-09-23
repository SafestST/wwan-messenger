using System.Text.RegularExpressions;
using WwanMessenger.Core.At;
using WwanMessenger.Core.Channels;
using WwanMessenger.Core.Modem;

namespace WwanMessenger.Core.Sms;

public sealed class SendResult
{
    public bool Success { get; init; }
    public int? ModemRef { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// High-level SMS operations over a modem channel.
///
/// Protocol strategy: PDU mode primary (charset-independent, works on every
/// conformant modem, full control of multipart segmentation); text mode only as
/// a fallback if a modem rejects PDU sends. Receive: CNMI=2,1 (store + notify),
/// then CMGR per index, CMGD after processing; an unread sweep (CMGL=0) runs on
/// connect so messages that arrived while we were closed are picked up.
/// </summary>
public sealed class SmsService : ISmsBackend
{
    private static readonly Regex CmtiRe = new(@"^\+CMTI:\s*""(\w+)"",(\d+)$", RegexOptions.Compiled);
    private static readonly Regex CmglRe = new(@"^\+CMGL:\s*(\d+),\d*,.*$", RegexOptions.Compiled);

    private readonly AtTransceiver _at;
    private readonly IModemChannel _channel;
    private readonly System.Threading.Timer? _signalPoll;
    private readonly object _concatLock = new();
    private readonly Dictionary<string, ConcatState> _pendingConcat = new();

    public ModemStatus Status { get; } = new();

    public event Action<Sms.SmsIncomingMessage>? MessageReceived;
    public event Action? StatusChanged;
    public event Action<AtLineKind, string>? Logged
    {
        add => _at.Logged += value;
        remove => _at.Logged -= value;
    }

    public SmsService(IModemChannel channel)
    {
        _channel = channel;
        _at = new AtTransceiver(channel);
        _at.UrcReceived += OnUrc;
        _signalPoll = new System.Threading.Timer(_ => PollStatusSafe(), null, 15000, 15000);
    }

    public IModemChannel Channel => _channel;

    // ---- lifecycle ---------------------------------------------------------

    public async Task<bool> ConnectAsync()
    {
        Status.Link = LinkState.Connecting;
        Status.ChannelName = _channel.Name;
        StatusChanged?.Invoke();

        try { _channel.Open(); }
        catch (Exception ex)
        {
            Log(AtLineKind.Info, $"open failed: {ex.Message}");
            Status.Link = LinkState.Offline;
            StatusChanged?.Invoke();
            return false;
        }

        var r = await _at.ExecuteAsync("ATE0");
        if (!r.Success) { FailConnect(r); return false; }

        var id = await _at.ExecuteAsync("ATI");
        if (id.Success)
        {
            var identity = string.Join(" / ", id.Lines.Where(l => l.Length > 0));
            if (identity.Length > 0) Status.ModemIdentity = identity;
        }

        await _at.ExecuteAsync("AT+CMEE=2");
        await _at.ExecuteAsync("AT+CMGF=0");     // PDU mode
        await _at.ExecuteAsync("AT+CSCS=\"GSM\"");
        await _at.ExecuteAsync("AT+CNMI=2,1,0,0,0");

        ReadSimState();
        PollStatusSafe();
        await SweepUnreadAsync();

        Status.Link = LinkState.Online;
        StatusChanged?.Invoke();
        return true;
    }

    private void FailConnect(AtResponse r)
    {
        Log(AtLineKind.Info, $"handshake failed: {r.Final}");
        Status.Link = LinkState.Offline;
        StatusChanged?.Invoke();
    }

    public void Disconnect()
    {
        try { if (_channel.IsOpen) _channel.Close(); } catch { }
        Status.Link = LinkState.Offline;
        Status.Sim = SimState.Unknown;
        Status.Net = NetState.Unknown;
        Status.OperatorName = "";
        StatusChanged?.Invoke();
    }

    // ---- send --------------------------------------------------------------

    public async Task<SendResult> SendAsync(string destination, string text)
    {
        var plan = Segmenter.Plan(text);
        if (plan.Total == 0) return new SendResult { Success = false, Detail = "empty message" };

        int concatRef = Random.Shared.Next(256);
        int? lastRef = null;

        for (int i = 0; i < plan.Total; i++)
        {
            var pdu = SmsPdu.EncodeSubmit(destination, plan.Parts[i], concatRef, i + 1, plan.Total);
            var r = await SendPduAsync(pdu);
            if (!r.Success)
            {
                // Data-safety: a TIMEOUT means the PDU was submitted and the
                // outcome is UNKNOWN — falling back to text mode would resend
                // it. Text-mode retry only after an explicit modem rejection.
                if (r.Detail.Contains("TIMEOUT"))
                {
                    return new SendResult
                    {
                        Success = false,
                        Detail = "UNCONFIRMED: PDU submitted but no completion - not resent (duplicate risk)",
                    };
                }

                // Some firmwares/configs dislike PDU sends — try text mode once per message.
                var fallback = await TrySendTextModeAsync(destination, text, plan);
                if (fallback.Success) return fallback;
                return new SendResult
                {
                    Success = false,
                    Detail = r.Detail,
                };
            }
            lastRef = r.ModemRef;
        }

        return new SendResult { Success = true, ModemRef = lastRef };
    }

    private async Task<SendResult> SendPduAsync(SubmitPdu pdu)
    {
        var r = await _at.ExecuteWithPromptAsync(
            $"AT+CMGS={pdu.TpduOctets}",
            () => _channel.WriteRaw(ConcatBytes(
                System.Text.Encoding.ASCII.GetBytes(pdu.Hex),
                new byte[] { 0x1A })),
            timeoutMs: 30000);

        if (!r.Success)
            return new SendResult { Success = false, Detail = r.Final };

        int? mr = null;
        var m = Regex.Match(r.InfoLine("+CMGS:") ?? "", @"\+CMGS:\s*(\d+)");
        if (m.Success) mr = int.Parse(m.Groups[1].Value);
        return new SendResult { Success = true, ModemRef = mr };
    }

    private async Task<SendResult> TrySendTextModeAsync(string destination, string text, SegmentPlan plan)
    {
        Log(AtLineKind.Info, "PDU send failed — attempting text-mode fallback");
        var cmgf = await _at.ExecuteAsync("AT+CMGF=1");
        if (!cmgf.Success) return new SendResult { Success = false, Detail = "text mode unavailable" };

        bool ucs2 = plan.UseUcs2;
        try
        {
            if (ucs2)
            {
                var set = await _at.ExecuteAsync("AT+CSCS=\"UCS2\"");
                if (!set.Success) return new SendResult { Success = false, Detail = "CSCS UCS2 rejected" };
            }

            string destArg = ucs2
                ? SmsPdu.BytesToHex(System.Text.Encoding.BigEndianUnicode.GetBytes(destination))
                : "\"" + destination + "\"";

            var r = await _at.ExecuteWithPromptAsync(
                $"AT+CMGS={destArg}",
                () =>
                {
                    var payload = ucs2
                        ? SmsPdu.BytesToHex(System.Text.Encoding.BigEndianUnicode.GetBytes(text))
                        : text;
                    _channel.WriteRaw(ConcatBytes(
                        System.Text.Encoding.ASCII.GetBytes(payload),
                        new byte[] { 0x1A }));
                },
                timeoutMs: 30000);

            if (!r.Success) return new SendResult { Success = false, Detail = DescribeSendError(r) };

            int? mr = null;
            var m = Regex.Match(r.InfoLine("+CMGS:") ?? "", @"\+CMGS:\s*(\d+)");
            if (m.Success) mr = int.Parse(m.Groups[1].Value);
            return new SendResult { Success = true, ModemRef = mr };
        }
        finally
        {
            if (ucs2) await _at.ExecuteAsync("AT+CSCS=\"GSM\"");
            await _at.ExecuteAsync("AT+CMGF=0");
        }
    }

    private static string DescribeSendError(AtResponse r) => r.Final switch
    {
        "TIMEOUT" => "modem did not respond (timeout)",
        var f when f.StartsWith("CHANNEL ERROR") => f,
        var f when f.StartsWith("+CMS ERROR") => $"network rejected: {f}",
        var f when f.StartsWith("+CME ERROR") => $"modem error: {f}",
        _ => r.Final,
    };

    // ---- receive -----------------------------------------------------------

    private void OnUrc(string line)
    {
        var m = CmtiRe.Match(line);
        if (!m.Success) return;
        string storage = m.Groups[1].Value;
        if (storage is not ("ME" or "SM" or "MT")) return;
        int idx = int.Parse(m.Groups[2].Value);
        Task.Run(() => FetchAndDeleteAsync(idx));
    }

    private async Task FetchAndDeleteAsync(int idx)
    {
        var r = await _at.ExecuteAsync($"AT+CMGR={idx}", timeoutMs: 10000);
        if (!r.Success)
        {
            Log(AtLineKind.Info, $"CMGR {idx} failed: {r.Final}");
            await _at.ExecuteAsync($"AT+CMGD={idx}");
            return;
        }

        string? pdu = r.Lines.FirstOrDefault(l => l.Length > 0 && l.All(c => Uri.IsHexDigit(c)) && l.Length % 2 == 0);
        if (pdu is null)
        {
            Log(AtLineKind.Info, $"CMGR {idx}: no PDU in response");
            await _at.ExecuteAsync($"AT+CMGD={idx}");
            return;
        }

        await _at.ExecuteAsync($"AT+CMGD={idx}");

        try
        {
            var deliver = SmsPdu.DecodeDeliver(pdu);
            HandleDelivered(deliver);
        }
        catch (Exception ex)
        {
            Log(AtLineKind.Info, $"PDU decode failed: {ex.Message}");
        }
    }

    private async Task SweepUnreadAsync()
    {
        var r = await _at.ExecuteAsync("AT+CMGL=0", timeoutMs: 15000);
        if (!r.Success)
        {
            // Some firmwares reject the numeric form; quoted form is the common alternative.
            r = await _at.ExecuteAsync("AT+CMGL=\"REC UNREAD\"", timeoutMs: 15000);
            if (!r.Success) return;
        }

        for (int i = 0; i < r.Lines.Count - 1; i++)
        {
            var header = CmglRe.Match(r.Lines[i]);
            if (!header.Success) continue;
            int idx = int.Parse(header.Groups[1].Value);
            string pdu = r.Lines[i + 1];
            try
            {
                var deliver = SmsPdu.DecodeDeliver(pdu);
                HandleDelivered(deliver);
            }
            catch (Exception ex)
            {
                Log(AtLineKind.Info, $"sweep PDU decode failed: {ex.Message}");
            }
            await _at.ExecuteAsync($"AT+CMGD={idx}");
        }
    }

    private void HandleDelivered(DeliverSms deliver)
    {
        if (!deliver.IsMultipart)
        {
            MessageReceived?.Invoke(new SmsIncomingMessage(
                deliver.Sender, deliver.Text, deliver.Timestamp.LocalDateTime));
            return;
        }

        // Reassemble concatenated parts.
        string key = $"{deliver.Sender}:{deliver.ConcatRef}:{deliver.ConcatTotal}";
        lock (_concatLock)
        {
            if (!_pendingConcat.TryGetValue(key, out var state))
            {
                state = new ConcatState(deliver.ConcatTotal);
                _pendingConcat[key] = state;
            }
            if (state.Parts.ContainsKey(deliver.ConcatSeq)) return; // duplicate
            state.Parts[deliver.ConcatSeq] = deliver.Text;
            state.LastTouchUtc = DateTime.UtcNow;

            if (state.Parts.Count < state.Total) return;
            _pendingConcat.Remove(key);

            var sb = new System.Text.StringBuilder();
            for (int s = 1; s <= state.Total; s++) sb.Append(state.Parts[s]);
            MessageReceived?.Invoke(new SmsIncomingMessage(
                deliver.Sender, sb.ToString(), deliver.Timestamp.LocalDateTime));
        }

        // opportunistic cleanup of stale partials
        lock (_concatLock)
        {
            var stale = _pendingConcat.Where(kv => (DateTime.UtcNow - kv.Value.LastTouchUtc).TotalMinutes > 10).ToList();
            foreach (var kv in stale)
            {
                var sb = new System.Text.StringBuilder();
                for (int s = 1; s <= kv.Value.Total; s++)
                    if (kv.Value.Parts.TryGetValue(s, out var t)) sb.Append(t);
                MessageReceived?.Invoke(new SmsIncomingMessage(
                    kv.Key.Split(':')[0], sb.ToString() + " (incomplete)", DateTime.Now));
                _pendingConcat.Remove(kv.Key);
            }
        }
    }

    // ---- status ------------------------------------------------------------

    private void PollStatusSafe()
    {
        try { PollStatus(); }
        catch (Exception ex) { Log(AtLineKind.Info, $"status poll failed: {ex.Message}"); }
    }

    private void PollStatus()
    {
        if (!_channel.IsOpen) return;

        var csq = _at.Execute("AT+CSQ", timeoutMs: 3000);
        if (csq.Success)
        {
            var m = Regex.Match(csq.InfoLine("+CSQ:") ?? "", @"\+CSQ:\s*(\d+),");
            if (m.Success) Status.Rssi = int.Parse(m.Groups[1].Value);
        }

        var cereg = _at.Execute("AT+CEREG?", timeoutMs: 3000);
        if (cereg.Success)
        {
            var m = Regex.Match(cereg.InfoLine("+CEREG:") ?? "", @"\+CEREG:\s*\d,(\d)");
            if (m.Success)
            {
                Status.Net = m.Groups[1].Value switch
                {
                    "1" => NetState.Home,
                    "5" => NetState.Roaming,
                    "2" => NetState.Searching,
                    "3" => NetState.Denied,
                    _ => NetState.Unknown,
                };
            }
        }

        if (string.IsNullOrEmpty(Status.OperatorName) && Status.Net is NetState.Home or NetState.Roaming)
        {
            var cops = _at.Execute("AT+COPS?", timeoutMs: 5000);
            var m = Regex.Match(cops.InfoLine("+COPS:") ?? "", @"""([^""]+)""");
            if (m.Success) Status.OperatorName = m.Groups[1].Value;
        }

        StatusChanged?.Invoke();
    }

    private void ReadSimState()
    {
        var r = _at.Execute("AT+CPIN?", timeoutMs: 3000);
        if (!r.Success)
        {
            Status.Sim = r.CmeErrorCode switch
            {
                10 or 11 or 13 => SimState.Absent,
                _ => SimState.Unknown,
            };
            if (r.Final.Contains("sim not inserted", StringComparison.OrdinalIgnoreCase))
                Status.Sim = SimState.Absent;
        }
        else
        {
            var pin = r.InfoLine("+CPIN:") ?? "";
            Status.Sim = pin.Contains("READY") ? SimState.Ready
                : pin.Contains("SIM PIN") ? SimState.PinRequired
                : SimState.Unknown;
        }
        StatusChanged?.Invoke();
    }

    private void Log(AtLineKind kind, string text) => _at.RaiseLog(kind, text);

    private static byte[] ConcatBytes(byte[] a, byte[] b)
    {
        var res = new byte[a.Length + b.Length];
        Array.Copy(a, res, a.Length);
        Array.Copy(b, 0, res, a.Length, b.Length);
        return res;
    }

    public void Dispose()
    {
        _signalPoll?.Dispose();
        _at.UrcReceived -= OnUrc;
        _at.Dispose();
        _channel.Dispose();
    }

    private sealed class ConcatState
    {
        public int Total;
        public Dictionary<int, string> Parts = new();
        public DateTime LastTouchUtc = DateTime.UtcNow;
        public ConcatState(int total) => Total = total;
    }
}

public sealed record SmsIncomingMessage(string Sender, string Text, DateTime ReceivedLocal);
