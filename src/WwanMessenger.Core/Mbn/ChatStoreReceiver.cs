using Windows.ApplicationModel.Chat;
using WwanMessenger.Core.Sms;

namespace WwanMessenger.Core.Mbn;

/// <summary>
/// RECEIVE CHANNEL DISCOVERY (2026-09-22):
/// WwanSvc intercepts every inbound MBIM SMS — writes it into the Windows
/// Chat platform store (Windows.ApplicationModel.Chat) and deletes it from
/// the modem store before applications can read it (observed live as
/// OnSmsDeleteComplete S_OK on arrival). Modem-store reads are therefore
/// empty BY DESIGN on this stack. This receiver reads the Chat store
/// instead: initial history import on start, then MessageChanged-driven
/// live receive.
/// </summary>
public sealed class ChatStoreReceiver : IDisposable
{
    private ChatMessageStore? _store;
    private readonly object _seenLock = new();
    private readonly HashSet<string> _seenIds = new();
    private readonly string _seenIdsPath;
    private int _draining;
    private System.Threading.Timer? _pollTimer;

    /// <summary>Consecutive failed drains — a persistence signal that the SMS
    /// subsystem object died (e.g. WwanSvc bounce) and the backend must be
    /// recreated rather than retried.</summary>
    public int ConsecutiveFailures { get; private set; }

    public event Action<SmsIncomingMessage>? MessageReceived;
    public event Action<string>? Logged;

    private void Log(string line) => Logged?.Invoke(line);

    public ChatStoreReceiver()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WwanMessenger");
        Directory.CreateDirectory(dir);
        _seenIdsPath = Path.Combine(dir, "seen-chat-ids.json");
        try
        {
            if (File.Exists(_seenIdsPath))
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_seenIdsPath));
                if (ids is not null)
                    foreach (var id in ids) _seenIds.Add(id);
            }
        }
        catch { /* corrupt file — an empty set only risks a one-off re-import */ }
    }

    private void PersistSeenIds()
    {
        try
        {
            lock (_seenLock)
            {
                // keep the newest 4000 ids — bounded file, recent window covered
                var ids = _seenIds.Count > 4000
                    ? _seenIds.Skip(_seenIds.Count - 4000).ToList()
                    : _seenIds.ToList();
                File.WriteAllText(_seenIdsPath, System.Text.Json.JsonSerializer.Serialize(ids));
            }
        }
        catch { /* non-fatal */ }
    }

    public async Task<bool> StartAsync()
    {
        try
        {
            _store = await ChatMessageManager.RequestStoreAsync();
        }
        catch (Exception ex)
        {
            Log($"chat store unavailable: {ex.Message}");
            return false;
        }

        // Live event first (when the platform raises it)…
        _store.MessageChanged += OnMessageChanged;
        // …but DO NOT rely on it: on desktop stacks the event is not raised
        // for WwanSvc-routed SMS. A 10 s poll guarantees propagation; the
        // persisted seen-ID set makes polling idempotent ACROSS RESTARTS —
        // which is why identical rapid-fire texts ("ok", "ok", "ok") are
        // never mistaken for re-imports and never dropped.
        _pollTimer = new System.Threading.Timer(_ => _ = DrainAsync(), null, 0, 10000);

        Log("Windows chat store opened — receive channel online (event + 10s poll)");
        // first drain happens on the timer's immediate tick — history import
        // must not block the connect handshake
        return true;
    }

    private void OnMessageChanged(ChatMessageStore sender, object args)
        => _ = DrainAsync();

    private async Task DrainAsync()
    {
        // single-flight: events and polls overlap in bursts
        if (Interlocked.Exchange(ref _draining, 1) == 1) return;
        try
        {
            // Fresh reader per drain: a positioned reader may never yield
            // messages that arrived after it was created.
            var reader = _store!.GetMessageReader();
            bool changed = false;
            for (int page = 0; page < 20; page++) // hard cap: 1000 messages
            {
                IReadOnlyList<ChatMessage> batch;
                try
                {
                    batch = await reader.ReadBatchAsync();
                    ConsecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    ConsecutiveFailures++;
                    Log($"chat read failed ({ConsecutiveFailures}x): {ex.Message}");
                    break;
                }

                foreach (var m in batch)
                {
                    lock (_seenLock)
                    {
                        if (!_seenIds.Add(m.Id)) continue;
                    }
                    changed = true;
                    if (string.IsNullOrEmpty(m.From)) continue;
                    if (!m.IsIncoming) continue;

                    string body = m.Body ?? "";
                    string? mediaNote = null;
                    try
                    {
                        var atts = m.Attachments;
                        if (atts?.Count > 0)
                        {
                            var saved = new List<string>();
                            foreach (var a in atts)
                                saved.Add(await SaveAttachmentAsync(m.Id, a));
                            mediaNote = $"[{string.Join(", ", saved)}]";
                            Log($"chat inbound media from {m.From}: {mediaNote}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"attachment handling failed: {ex.Message}");
                    }

                    var display = mediaNote is null
                        ? body
                        : (body.Length == 0 ? mediaNote : $"{body} {mediaNote}");
                    if (display.Length == 0) display = "[empty message]";

                    Log($"chat inbound from {m.From}: \"{(display.Length > 40 ? display[..40] + "…" : display)}\"");
                    MessageReceived?.Invoke(new SmsIncomingMessage(
                        m.From, display, m.LocalTimestamp.LocalDateTime));

                    try { await _store!.MarkMessageReadAsync(m.Id); } catch { /* already read */ }
                }

                if (batch.Count == 0) break; // drained
            }
            if (changed) PersistSeenIds();
        }
        finally
        {
            Volatile.Write(ref _draining, 0);
        }
    }

    /// <summary>
    /// Persist a chat attachment (photo/sticker/audio) to
    /// %AppData%\WwanMessenger\attachments and return a short description.
    /// Capped at 4 MB per attachment; failures are non-fatal.
    /// </summary>
    private static async Task<string> SaveAttachmentAsync(
        string messageId, Windows.ApplicationModel.Chat.ChatMessageAttachment a)
    {
        var ext = ExtForMime(a.MimeType);
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WwanMessenger", "attachments");
        Directory.CreateDirectory(dir);

        var safe = new string(messageId.Where(char.IsLetterOrDigit).ToArray());
        if (safe.Length > 12) safe = safe[..12];
        var file = Path.Combine(dir, $"{safe}_{Interlocked.Increment(ref _attSeq)}.{ext}");

        using var stream = await a.DataStreamReference.OpenReadAsync();
        uint size = (uint)Math.Min(stream.Size, 4 * 1024 * 1024);
        if (size == 0) return "empty attachment";

        var reader = new Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync(size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(file, bytes);
        return $"{a.MimeType} → attachments\\{Path.GetFileName(file)}";
    }

    private static int _attSeq;

    private static string ExtForMime(string? mime) => (mime ?? "").Split('/').LastOrDefault() switch
    {
        "jpeg" => "jpg",
        "png" => "png",
        "gif" => "gif",
        "webp" => "webp",
        "heic" => "heic",
        "mp4" => "mp4",
        "amr" => "amr",
        "ogg" => "ogg",
        "3gpp" => "3gp",
        _ => "bin",
    };

    public void Dispose()
    {
        _pollTimer?.Dispose();
        if (_store is not null) _store.MessageChanged -= OnMessageChanged;
        _store = null;
    }
}
