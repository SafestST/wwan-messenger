using System.Windows.Media;
using WwanMessenger.Core.Storage;

namespace WwanMessenger.App.ViewModels;

public sealed class MessageViewModel : ViewModelBase
{
    private readonly StoredMessage _model;

    public MessageViewModel(StoredMessage model) => _model = model;

    public Guid Id => _model.Id;
    public string Text => _model.Text;
    public bool IsIncoming => _model.Direction == MessageDirection.Inbound;

    public string TimeDisplay => _model.TimestampUtc.ToLocalTime().ToString("dd MMM HH:mm");

    public string StatusWord => _model.Status switch
    {
        MessageStatus.Queued => "QUEUED",
        MessageStatus.Sent => "SENT",
        MessageStatus.Delivered => "DLVD",
        MessageStatus.Failed => "FAIL",
        MessageStatus.Received => "RX",
        _ => "—",
    };

    private static Brush Res(string key, string fallbackKey = "FgMid")
        => App.Current.Resources.Contains(key)
            ? (Brush)App.Current.Resources[key]
            : (Brush)App.Current.Resources[fallbackKey];

    public Brush StatusBrush => _model.Status switch
    {
        MessageStatus.Queued => Res("FgMid"),
        MessageStatus.Sent => Res("GreenOk"),
        MessageStatus.Delivered => Res("CyanInfo"),
        MessageStatus.Failed => Res("RedBad"),
        _ => Res("FgMid"),
    };

    public void Refresh()
    {
        Raise(nameof(StatusWord));
        Raise(nameof(StatusBrush));
    }
}
