using WwanMessenger.Core.Storage;

namespace WwanMessenger.App.Services;

/// <summary>First-run demo threads so the console isn't empty before the first real exchange.</summary>
public static class SeedData
{
    public static void EnsureSeeded(MessageStore store)
    {
        if (store.Conversations.Count > 0) return;

        var now = DateTime.UtcNow;

        var dispatch = store.GetOrAddConversation("+447700900123", "Dispatch Bot");
        Add(dispatch, store, MessageDirection.Inbound,
            "WWAN Messenger bench online. This thread is wired to the simulator's auto-responder — send anything and watch the console drawer.",
            now.AddHours(-26));
        Add(dispatch, store, MessageDirection.Outbound,
            "Understood. Testing the uplink.",
            now.AddHours(-26).AddMinutes(2), MessageStatus.Sent);
        Add(dispatch, store, MessageDirection.Inbound,
            "Copy that — received loud and clear at signal 22/31.",
            now.AddHours(-26).AddMinutes(3));
        Add(dispatch, store, MessageDirection.Inbound,
            "Every third reply from the bench is deliberately long, so multipart reassembly gets exercised on the real receive path.",
            now.AddHours(-25));

        var site = store.GetOrAddConversation("+447700900456", "Site Alpha");
        Add(site, store, MessageDirection.Inbound,
            "Panel 3 isolation complete. Awaiting sign-off before re-energising.",
            now.AddHours(-8));
        Add(site, store, MessageDirection.Outbound,
            "Hold position. Sign-off in the morning.",
            now.AddHours(-7), MessageStatus.Sent);
        Add(site, store, MessageDirection.Inbound,
            "Holding until first light.",
            now.AddHours(-6).AddMinutes(-45));

        store.FlushNow();
    }

    private static void Add(StoredConversation conv, MessageStore store, MessageDirection dir,
        string text, DateTime utc, MessageStatus status = MessageStatus.Received)
    {
        StoredMessage? msg = dir == MessageDirection.Inbound
            ? store.AddInbound(conv.Number, conv.DisplayName, text, utc)
            : store.AddOutbound(conv.Number, conv.DisplayName, text);
        if (msg is null) return; // duplicate seed — already present
        store.UpdateMessage(msg, status, null);
        if (dir == MessageDirection.Inbound) store.MarkRead(conv);
    }
}
