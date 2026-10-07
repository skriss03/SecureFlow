using SecureFlow.Api.Options;

namespace SecureFlow.Api.Projects;

/// <summary>Deletes projects that have not been updated within SecureFlow:Retention:Days.</summary>
public sealed class RetentionService : BackgroundService
{
    private readonly ProjectStore _store;
    private readonly SecureFlowOptions.RetentionOptions _options;
    private readonly ILogger<RetentionService> _log;
    private readonly TimeProvider _time;

    public RetentionService(ProjectStore store, SecureFlowOptions options, ILogger<RetentionService> log, TimeProvider? time = null)
    {
        _store = store;
        _options = options.Retention;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Retention disabled; projects are kept indefinitely.");
            return;
        }
        _log.LogInformation("Retention: projects idle for more than {Days} day(s) are deleted every {Minutes} min.", _options.Days, _options.SweepIntervalMinutes);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.InitialDelaySeconds)), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _options.SweepIntervalMinutes)));
            do { Sweep(); } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>One pass; returns the ids removed. Projects still being scanned are never removed.</summary>
    public List<string> Sweep()
    {
        var cutoff = _time.GetUtcNow().AddDays(-_options.Days);
        var expired = _store.All
            .Where(p => p.UpdatedAt < cutoff && p.Status is ProjectStatus.Ready or ProjectStatus.Error)
            .Select(p => p.Id).ToList();
        foreach (var id in expired) _store.Delete(id);
        if (expired.Count > 0) _log.LogInformation("Retention removed {Count} project(s) older than {Days} day(s).", expired.Count, _options.Days);
        return expired;
    }
}
