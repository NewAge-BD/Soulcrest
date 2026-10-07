namespace Soulcrest.App.Services;

/// <summary>
/// Raises a <c>Changed</c> event from a capture or tracking thread. Every subscriber runs on its own: one that
/// throws (a save that fails because the file is locked) is logged to errors.log instead of ending the
/// thread that raised the event, and the other subscribers still run (review 2026-10-07).
/// </summary>
public static class SafeEvent
{
    public static void Raise(Action? handler, string source)
    {
        if (handler is null)
            return;
        foreach (var subscriber in handler.GetInvocationList().Cast<Action>())
        {
            try
            {
                subscriber();
            }
            catch (Exception e)
            {
                LogFile.Error($"{source} → {subscriber.Method.DeclaringType?.Name}.{subscriber.Method.Name}", e);
            }
        }
    }
}
