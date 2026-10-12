namespace Rezerv.Application.Abstractions;

/// <summary>
/// Port to 2C2P Payment Maintenance (Payment Action API): status inquiry, void and refund of a completed payment.
/// Separate from <see cref="IPaymentGateway"/> because it uses its own endpoint and certificate-based security.
/// </summary>
public interface IPaymentActionGateway
{
    /// <summary>False when the keys for the Payment Action API are not configured; refunds are then unavailable.</summary>
    bool IsConfigured { get; }

    /// <summary>Where the payment is in its lifecycle (authorised, settled, voided), which decides void or refund.</summary>
    Task<PaymentActionStatus> GetPaymentStatusAsync(string invoiceNo, decimal amount, CancellationToken ct = default);

    /// <summary>Cancels an authorised, unsettled payment (full amount).</summary>
    Task<RefundActionResult> VoidAsync(RefundActionRequest request, CancellationToken ct = default);

    /// <summary>Refunds a settled payment, fully or partially.</summary>
    Task<RefundActionResult> RefundAsync(RefundActionRequest request, CancellationToken ct = default);

    /// <summary>Refund records 2C2P holds for an invoice.</summary>
    Task<RefundStatusResult> GetRefundStatusAsync(string invoiceNo, decimal amount, CancellationToken ct = default);

    /// <summary>Deletes a stored card token from the 2C2P vault (Store Card Maintenance, action D).</summary>
    Task<CardTokenRemovalResult> RemoveCardTokenAsync(CardTokenRemovalRequest request, CancellationToken ct = default);
}

/// <param name="CustomerToken">The customerToken (storeCardUniqueID) to delete.</param>
/// <param name="CardholderName">Mandatory in the 2C2P maintenance request.</param>
public sealed record CardTokenRemovalRequest(string CustomerToken, string CardholderName, string? CardholderEmail);

/// <param name="Removed">True only when 2C2P confirmed the deletion (respCode 00).</param>
public sealed record CardTokenRemovalResult(bool Removed, string RespCode, string? RespReason);

public enum CaptureState
{
    /// <summary>Approved but not settled yet: can be voided (full amount).</summary>
    Authorized,
    /// <summary>Settled (possibly partly refunded already): can be refunded.</summary>
    Settled,
    Voided,
    /// <summary>Anything else (pending, failed, expired...): neither void nor refund is possible now.</summary>
    Other
}

public enum RefundOutcome
{
    Succeeded,
    /// <summary>Accepted but not final yet (e.g. RP refund pending, VP void pending, or the answer was lost).</summary>
    Pending,
    Failed
}

/// <param name="IdempotencyId">Sent to 2C2P so a repeated request cannot refund twice on their side either.</param>
public sealed record RefundActionRequest(string InvoiceNo, decimal Amount, string IdempotencyId);

public sealed record PaymentActionStatus(CaptureState State, string RespCode, string? RespDesc, string? Status);

public sealed record RefundActionResult(RefundOutcome Outcome, string RespCode, string? RespDesc, string? Status, string? ReferenceNo);

public sealed record RefundStatusEntry(decimal Amount, RefundOutcome Outcome, string? Status, string? ReferenceNo);

public sealed record RefundStatusResult(string RespCode, string? RespDesc, string? Status, IReadOnlyList<RefundStatusEntry> Refunds);
