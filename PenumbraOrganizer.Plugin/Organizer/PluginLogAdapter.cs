namespace PenumbraOrganizer.Plugin.Organizer;

using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

/// <summary>
/// Wraps Dalamud's IPluginLog behind ILogger&lt;T&gt; so the linked standalone-app
/// WorkbookWorkflowService (which takes ILogger&lt;T&gt;) logs through this plugin's own logging
/// pipeline instead of pulling in a full DI/logging framework this plugin doesn't otherwise use.
/// </summary>
public sealed class PluginLogAdapter<T> : ILogger<T>
{
    private readonly IPluginLog _log;

    public PluginLogAdapter(IPluginLog log) => _log = log;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        switch (logLevel)
        {
            case LogLevel.Warning:
                _log.Warning(message);
                break;
            case LogLevel.Error:
            case LogLevel.Critical:
                _log.Error(message);
                break;
            default:
                _log.Information(message);
                break;
        }
    }
}
