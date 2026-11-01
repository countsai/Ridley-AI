namespace Ridley.App.ViewModels;

public enum ChatMessageKind { User, Assistant, System, Error }

public sealed record ChatMessage(ChatMessageKind Kind, string Text, DateTimeOffset At)
{
    public string Author => Kind switch
    {
        ChatMessageKind.User => "You",
        ChatMessageKind.Assistant => "Ridley",
        ChatMessageKind.Error => "Problem",
        _ => "Info",
    };
}
