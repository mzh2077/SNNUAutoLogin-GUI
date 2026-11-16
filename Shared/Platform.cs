using System;

namespace SNNUAutoLogin.Services;

public interface ICredentialSource
{
    AppSettings Settings { get; }
    string LoadPassword();
    string LoadRulePassword(string ruleId);
    bool HasPassword { get; }
}

public interface IEventDispatcher
{
    bool HasThreadAccess { get; }
    bool TryEnqueue(Action action);
}

public sealed class EventDispatcher(Func<bool> hasThreadAccess, Func<Action, bool> enqueue) : IEventDispatcher
{
    public bool HasThreadAccess => hasThreadAccess();
    public bool TryEnqueue(Action action) => enqueue(action);

    public static IEventDispatcher Inline { get; } = new EventDispatcher(
        () => true, action => { action(); return true; });
}
