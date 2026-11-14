namespace RAQM.Api.Services.SaaS;

public sealed class CommercialWorker(IServiceScopeFactory factory, IConfiguration configuration, ILogger<CommercialWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("SaaS:WorkerEnabled")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            do
            {
                try
                {
                    for (var i = 0; i < 10; i++)
                    {
                        await using var scope = factory.CreateAsyncScope();
                        if (!await scope.ServiceProvider.GetRequiredService<CommercialService>().Tick(stoppingToken))
                            break;
                    }
                    await using var cleanup = factory.CreateAsyncScope();
                    await cleanup.ServiceProvider.GetRequiredService<CommercialService>().PurgeExports(stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Commercial worker iteration failed ({ErrorType})", e.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
