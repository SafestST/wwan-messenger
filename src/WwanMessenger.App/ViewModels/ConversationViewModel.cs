using System.Collections.ObjectModel;
using WwanMessenger.Core.Storage;

namespace WwanMessenger.App.ViewModels;

public sealed class ConversationViewModel : ViewModelBase
{
    private int _unreadCount;
    private string _lastPreview = "";
    private string _lastTime = "";

    public ConversationViewModel(StoredConversation model)
    {
        Model = model;
        foreach (var m in model.Messages)
            Messages.Add(new MessageViewModel(m));
        _unreadCount = model.UnreadCount;
        _isPinned = model.Pinned;
        RecomputeLast();
    }

    public StoredConversation Model { get; }
    public string Number => Model.Number;

    public string Title => string.IsNullOrWhiteSpace(Model.DisplayName) ? Number : Model.DisplayName!;

    public string Initials => PhoneNumbers.Initials(Number, Model.DisplayName);

    public ObservableCollection<MessageViewModel> Messages { get; } = new();

    public int UnreadCount
    {
        get => _unreadCount;
        private set { if (Set(ref _unreadCount, value)) Raise(nameof(HasUnread)); }
    }

    public bool HasUnread => _unreadCount > 0;

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        private set => Set(ref _isPinned, value);
    }

    public void SetPinned(bool value)
    {
        IsPinned = value;
        Model.Pinned = value;
    }

    public void SetDisplayName(string? name)
    {
        Model.DisplayName = name;
        Raise(nameof(Title));
        Raise(nameof(Initials));
    }

    public string LastPreview
    {
        get => _lastPreview;
        private set => Set(ref _lastPreview, value);
    }

    public string LastTime
    {
        get => _lastTime;
        private set => Set(ref _lastTime, value);
    }

    public void Append(MessageViewModel message)
    {
        Messages.Add(message);
        RecomputeLast();
    }

    public void MarkRead()
    {
        UnreadCount = 0;
        Model.UnreadCount = 0;
    }

    public void NotifyIncoming()
    {
        UnreadCount++;
        Model.UnreadCount = UnreadCount;
        RecomputeLast();
    }

    private void RecomputeLast()
    {
        if (Model.Messages.Count == 0) { LastPreview = ""; LastTime = ""; return; }
        var last = Model.Messages[^1];
        var oneLine = last.Text.Replace("\r", "").Replace('\n', ' ');
        LastPreview = oneLine.Length > 42 ? oneLine[..42] + "…" : oneLine;
        LastTime = last.TimestampUtc.ToLocalTime().ToString("dd/MM HH:mm");
    }
}
