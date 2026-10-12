namespace Rezerv.Application.Services;

public sealed class CheckoutOptions
{
    public const string SectionName = "Checkout";

    /// <summary>
    /// Lifetime of the per-Idempotency-Key Redis lock. Must outlive the slowest checkout
    /// (Payment Token + Do Payment + Payment Inquiry), otherwise a second request could start while the first still runs.
    /// </summary>
    public TimeSpan LockTtl { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How long a request that lost the lock waits for the winner's result before answering 409.</summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan WaitPollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How long a checkout response stays in Redis for replay (MySQL remains the fallback afterwards).</summary>
    public TimeSpan ResponseTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Minimum gap between Payment Inquiry calls for one pending invoice.</summary>
    public TimeSpan InquiryThrottle { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan CatalogCacheTtl { get; set; } = TimeSpan.FromMinutes(10);
}
