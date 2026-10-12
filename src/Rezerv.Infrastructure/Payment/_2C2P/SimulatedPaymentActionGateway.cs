using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Rezerv.Application.Abstractions;

namespace Rezerv.Infrastructure.Payment._2C2P
{
    /// <summary>
    /// Development-only stand-in for the 2C2P Payment Action API, for the shared demo merchant (JT01) whose portal
    /// nobody can upload keys to. Enabled by _2C2P:PaymentAction:Simulate (refused outside Development).
    /// Every payment is treated as settled, so full and partial refunds are accepted and succeed at once.
    /// Nothing is sent to 2C2P: no money is returned. State is in memory (singleton) and lost on restart.
    /// </summary>
    public sealed class SimulatedPaymentActionGateway(ILogger<SimulatedPaymentActionGateway> logger) : IPaymentActionGateway
    {
        private const string SuccessCode = "00";
        private const string RefundedStatus = "RF";
        private const string SettledStatus = "S";
        private const string Description = "Simulated (no call to 2C2P)";

        // invoiceNo -> refunds; idempotencyId -> result, so a repeated request answers like 2C2P's idempotencyID would.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<RefundStatusEntry>> _refunds = new();
        private readonly ConcurrentDictionary<string, RefundActionResult> _byIdempotencyId = new();
        private int _nextReference;

        public bool IsConfigured => true;

        public Task<PaymentActionStatus> GetPaymentStatusAsync(string invoiceNo, decimal amount, CancellationToken ct = default)
        {
            logger.LogWarning("SIMULATED 2C2P payment status for {InvoiceNo}: settled", invoiceNo);
            return Task.FromResult(new PaymentActionStatus(CaptureState.Settled, SuccessCode, Description, SettledStatus));
        }

        public Task<RefundActionResult> RefundAsync(RefundActionRequest request, CancellationToken ct = default)
        {
            var result = _byIdempotencyId.GetOrAdd(request.IdempotencyId, _ =>
            {
                var reference = $"SIM{Interlocked.Increment(ref _nextReference):D6}";
                _refunds.GetOrAdd(request.InvoiceNo, _ => new())
                    .Enqueue(new RefundStatusEntry(request.Amount, RefundOutcome.Succeeded, RefundedStatus, reference));
                return new RefundActionResult(RefundOutcome.Succeeded, SuccessCode, Description, RefundedStatus, reference);
            });

            logger.LogWarning("SIMULATED 2C2P refund of {Amount} for {InvoiceNo}: {Reference}; no money was returned",
                request.Amount, request.InvoiceNo, result.ReferenceNo);
            return Task.FromResult(result);
        }

        // Never chosen: GetPaymentStatusAsync always reports Settled.
        public Task<RefundActionResult> VoidAsync(RefundActionRequest request, CancellationToken ct = default) =>
            Task.FromResult(new RefundActionResult(RefundOutcome.Failed, "99", "Void is not simulated", null, null));

        public Task<RefundStatusResult> GetRefundStatusAsync(string invoiceNo, decimal amount, CancellationToken ct = default)
        {
            var entries = _refunds.TryGetValue(invoiceNo, out var list) ? list.ToList() : [];
            return Task.FromResult(new RefundStatusResult(SuccessCode, Description, RefundedStatus, entries));
        }

        public Task<CardTokenRemovalResult> RemoveCardTokenAsync(CardTokenRemovalRequest request, CancellationToken ct = default)
        {
            logger.LogWarning("SIMULATED 2C2P card token removal; the token was not deleted at 2C2P");
            return Task.FromResult(new CardTokenRemovalResult(true, SuccessCode, Description));
        }
    }
}
