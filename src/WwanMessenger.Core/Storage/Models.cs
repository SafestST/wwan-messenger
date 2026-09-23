namespace WwanMessenger.Core.Storage;

public enum MessageDirection { Inbound, Outbound }

public enum MessageStatus { Queued, Sent, Delivered, Failed, Received }

public sealed class StoredMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MessageDirection Direction { get; set; }
    public string Text { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public MessageStatus Status { get; set; } = MessageStatus.Queued;
    /// <summary>Modem message reference from +CMGS, if any.</summary>
    public int? ModemRef { get; set; }
}

public sealed class StoredConversation
{
    public string Number { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool Pinned { get; set; }
    public List<StoredMessage> Messages { get; set; } = new();
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

    public int UnreadCount { get; set; }
}

public static class PhoneNumbers
{
    /// <summary>Normalise a user-entered number: keep only digits and a leading '+'.</summary>
    public static string Normalize(string raw)
    {
        raw = raw.Trim();
        bool intl = raw.StartsWith('+');
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? "" : (intl ? "+" : "") + digits;
    }

    public static string Display(string number, string? name)
        => string.IsNullOrWhiteSpace(name) ? number : name;

    public static string Initials(string number, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return (parts[0][0].ToString() + parts[^1][0]).ToUpperInvariant();
            return name.Length >= 2 ? name[..2].ToUpperInvariant() : name.ToUpperInvariant();
        }
        var digits = new string(number.Where(char.IsDigit).ToArray());
        return digits.Length >= 2 ? digits[^2..] : digits.PadLeft(2, '0');
    }
}
