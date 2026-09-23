using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WwanMessenger.Core.Storage;

/// <summary>
/// JSON-file-backed message store at %AppData%\WwanMessenger\store.json.
/// SQLite is the documented upgrade path once message volume justifies it.
/// </summary>
public sealed class MessageStore : IDisposable
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Timer _flushTimer;
    private bool _dirty;
    private Dictionary<string, StoredConversation> _conversations;

    public event Action<StoredConversation, StoredMessage>? MessageAdded;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MessageStore(string? path = null)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WwanMessenger");
        Directory.CreateDirectory(dir);
        _path = path ?? Path.Combine(dir, "store.json");
        _conversations = Load();
        _flushTimer = new Timer(_ => FlushIfDirty(), null, 2000, 2000);
    }

    public IReadOnlyList<StoredConversation> Conversations
    {
        get { lock (_lock) return _conversations.Values.OrderByDescending(c => c.LastActivityUtc).ToList(); }
    }

    public StoredConversation GetOrAddConversation(string number, string? name = null)
    {
        lock (_lock)
        {
            if (!_conversations.TryGetValue(number, out var conv))
            {
                conv = new StoredConversation { Number = number, DisplayName = name };
                _conversations[number] = conv;
                _dirty = true;
            }
            else if (name is not null && conv.DisplayName != name)
            {
                conv.DisplayName = name;
                _dirty = true;
            }
            return conv;
        }
    }

    public StoredMessage AddOutbound(string number, string? name, string text)
    {
        var conv = GetOrAddConversation(number, name);
        var msg = new StoredMessage
        {
            Direction = MessageDirection.Outbound,
            Text = text,
            Status = MessageStatus.Queued,
            TimestampUtc = DateTime.UtcNow,
        };
        lock (_lock)
        {
            conv.Messages.Add(msg);
            conv.LastActivityUtc = msg.TimestampUtc;
            _dirty = true;
        }
        MessageAdded?.Invoke(conv, msg);
        return msg;
    }

    /// <summary>
    /// Record an inbound message. Deduplication is the BACKEND's job — the
    /// chat-store receiver dedupes by exact persisted message ID. A text-based
    /// heuristic here previously ate legitimate rapid-fire repeats ("ok", "ok",
    /// "ok" — a real reported bug), so the store records everything it is given.
    /// </summary>
    public StoredMessage AddInbound(string senderNumber, string? senderName, string text, DateTime timestampUtc)
    {
        var conv = GetOrAddConversation(senderNumber, senderName);
        var msg = new StoredMessage
        {
            Direction = MessageDirection.Inbound,
            Text = text,
            Status = MessageStatus.Received,
            TimestampUtc = timestampUtc,
        };
        lock (_lock)
        {
            conv.Messages.Add(msg);
            conv.LastActivityUtc = msg.TimestampUtc;
            conv.UnreadCount++;
            _dirty = true;
            MessageAdded?.Invoke(conv, msg);
        }
        return msg;
    }

    public void MarkRead(StoredConversation conv)
    {
        lock (_lock)
        {
            if (conv.UnreadCount != 0) { conv.UnreadCount = 0; _dirty = true; }
        }
    }

    public void SetPinned(StoredConversation conv, bool pinned)
    {
        lock (_lock)
        {
            conv.Pinned = pinned;
            _dirty = true;
        }
    }

    public void SetDisplayName(StoredConversation conv, string? name)
    {
        lock (_lock)
        {
            conv.DisplayName = name;
            _dirty = true;
        }
    }

    public void UpdateMessage(StoredMessage msg, MessageStatus status, int? modemRef = null)
    {
        lock (_lock)
        {
            msg.Status = status;
            if (modemRef.HasValue) msg.ModemRef = modemRef;
            _dirty = true;
        }
        FlushIfDirty();
    }

    public void FlushNow()
    {
        lock (_lock) _dirty = true;
        FlushIfDirty();
    }

    private Dictionary<string, StoredConversation> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var list = JsonSerializer.Deserialize<List<StoredConversation>>(File.ReadAllText(_path), JsonOpts);
                return list?.ToDictionary(c => c.Number, c => c) ?? new();
            }
        }
        catch { /* corrupt store — start fresh rather than crash */ }
        return new();
    }

    private void FlushIfDirty()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                var list = _conversations.Values.ToList();
                File.WriteAllText(_path, JsonSerializer.Serialize(list, JsonOpts));
                _dirty = false;
            }
            catch (IOException) { /* retry on next tick */ }
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        FlushNow();
    }
}
