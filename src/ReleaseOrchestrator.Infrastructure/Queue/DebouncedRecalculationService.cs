using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Services;
using System.Threading.Channels;

namespace ReleaseOrchestrator.Infrastructure.Queue;

public class DebouncedRecalculationService(
    IServiceScopeFactory scopeFactory,
    ILogger<DebouncedRecalculationService> logger) : BackgroundService
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(15);

    public void RequestRecalculation(string reason)
        => _channel.Writer.TryWrite(reason);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var reason in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await Task.Delay(DebounceInterval, stoppingToken);

                // Drain any additional requests accumulated during the delay
                while (_channel.Reader.TryRead(out _)) { }

                await using var scope = scopeFactory.CreateAsyncScope();
                var planner = scope.ServiceProvider.GetRequiredService<IReleasePlannerService>();
                await planner.RecalculateAsync(stoppingToken);
                logger.LogInformation("Release plan recalculated. Reason: {Reason}", reason);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during release plan recalculation");
            }
        }
    }
}
