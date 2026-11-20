namespace Argus.Core.Events;

public enum AlertSeverity
{
    Info,
    Warning,
    Critical
}

public sealed record SystemAlertEvent(
    AlertSeverity Severity,
    string Source,
    string Message,
    DateTimeOffset Timestamp);

public interface IArgusEventBus
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull;
    void Publish<TEvent>(TEvent message) where TEvent : notnull;
}
