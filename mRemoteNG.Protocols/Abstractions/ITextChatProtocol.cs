namespace mRemoteNG.Protocols.Abstractions;

public enum TextChatEventKind
{
    /// <summary>The remote side sent a message.</summary>
    Message,

    /// <summary>The remote side opened the chat.</summary>
    Opened,

    /// <summary>The remote side closed the chat.</summary>
    Closed,
}

public sealed record TextChatEvent(TextChatEventKind Kind, string Text = "");

/// <summary>A session with a text chat to the remote user (UltraVNC chat).</summary>
public interface ITextChatProtocol
{
    /// <summary>Raised on a background thread for chat traffic from the server.</summary>
    event EventHandler<TextChatEvent>? TextChatReceived;

    /// <summary>Asks the server to open its chat window. Does nothing while not connected.</summary>
    void OpenTextChat();

    /// <summary>Sends a message. Returns false while not connected.</summary>
    bool SendTextChat(string text);

    /// <summary>Closes the chat on the server. Does nothing while not connected.</summary>
    void CloseTextChat();
}
