using RAQM.Api.Services.Automation;

namespace RAQM.Api.Services.EventProcessing;

public sealed class IntegrationWorkerStatus
{
    private long last;
    public void Beat() => Interlocked.Exchange(ref last, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public long Last => Interlocked.Read(ref last);
}

public sealed class IntegrationWorker(IServiceScopeFactory factory, IConfiguration configuration, IntegrationWorkerStatus status,
    ILogger<IntegrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Integrations:WorkerEnabled")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                await using var scope = factory.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<LedgerExportJobs>();
                var deliveries = scope.ServiceProvider.GetRequiredService<DeliveryDispatcher>();
                await jobs.Materialize(stoppingToken);
                await jobs.Run(stoppingToken);
                await jobs.Purge(stoppingToken);
                await deliveries.Run(stoppingToken);
                status.Beat();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning("Integration worker iteration failed ({ErrorType})", error.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
