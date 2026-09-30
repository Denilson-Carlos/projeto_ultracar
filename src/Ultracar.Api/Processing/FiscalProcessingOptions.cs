namespace Ultracar.Api.Processing;

public class FiscalProcessingOptions
{
    public int MaxAttempts { get; set; } = 5;
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan IntegratorTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int MaxParallelism { get; set; } = 4;
}
