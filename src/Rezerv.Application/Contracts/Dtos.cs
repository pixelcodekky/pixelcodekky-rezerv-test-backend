using Rezerv.Domain.Entities;

namespace Rezerv.Application.Contracts;

public sealed record CustomerDto(int Id, string FirstName, string LastName, string Email);

public sealed record ProductDto(int Id, string Name, string? Description, decimal Price, string Currency, PaymentType PaymentType);

/// <summary>Saved card as shown in the checkout dropdown. The 2C2P customerToken is never exposed.</summary>
public sealed record CustomerCardDto(int Id, string MaskedCardNo, string? PaymentChannel, DateTime CreatedAt, DateTime? LastUsedAt);

public sealed record CheckoutRequest(int CustomerId, int ProductId, int? CustomerCardId);

/// <param name="CustomerCardId">Saved card to pay with; omit to pay on the 2C2P hosted page (new payment method).</param>
public sealed record RetryRequest(int? CustomerCardId);

/// <param name="RedirectUrl">Hosted payment page or 3DS page; null when a saved-card payment needs no redirect.</param>
public sealed record CheckoutResponse(string InvoiceNo, TransactionStatus Status, string? RedirectUrl);

public sealed record TransactionDto(
    string InvoiceNo,
    TransactionStatus Status,
    int CustomerId,
    int ProductId,
    string? ProductName,
    decimal Amount,
    string Currency,
    string? PaymentChannel,
    string? MaskedCardNo,
    string? ProviderRef,
    string? RespCode,
    string? RespDesc,
    DateTime CreatedAt,
    DateTime? PaidAt,
    decimal RefundedAmount,
    decimal RefundableAmount,
    string? RetryOfInvoiceNo,
    string? RetryInvoiceNo,
    bool CanRetry);

/// <param name="Amount">Omit to refund everything not refunded yet.</param>
public sealed record RefundRequest(decimal? Amount, string? Reason);

public sealed record RefundDto(
    long Id,
    string InvoiceNo,
    RefundType Type,
    RefundStatus Status,
    decimal Amount,
    string Currency,
    string? Reason,
    string? ProviderStatus,
    string? ProviderRef,
    string? RespCode,
    string? RespDesc,
    DateTime CreatedAt,
    DateTime? CompletedAt);

internal static class DtoMapping
{
    public static CustomerDto ToDto(this Customer c) => new(c.Id, c.FirstName, c.LastName, c.Email);

    public static ProductDto ToDto(this Product p) => new(p.Id, p.Name, p.Description, p.Price, p.Currency, p.PaymentType);

    public static CustomerCardDto ToDto(this CustomerCard c) => new(c.Id, c.MaskedCardNo, c.PaymentChannel, c.CreatedAt, c.LastUsedAt);

    public static CheckoutResponse ToCheckoutResponse(this PaymentTransaction t) => new(t.InvoiceNo, t.Status, t.RedirectUrl);

    public static TransactionDto ToDto(this PaymentTransaction t) => new(
        t.InvoiceNo, t.Status, t.CustomerId, t.ProductId, t.Product?.Name, t.Amount, t.Currency,
        t.PaymentChannel, t.MaskedCardNo, t.ProviderRef, t.RespCode, t.RespDesc, t.CreatedAt, t.PaidAt, t.RefundedAmount,
        // Only succeeded payments can be refunded; pending refunds are already reserved in RefundedAmount.
        t.Status == TransactionStatus.Succeeded ? Math.Max(0m, t.Amount - t.RefundedAmount) : 0m,
        t.RetryOf?.InvoiceNo,
        t.Retry?.InvoiceNo,
        // A failed payment can be retried once; after that, retry the newest attempt instead.
        t.Status == TransactionStatus.Failed && t.Retry is null);

    public static RefundDto ToDto(this Refund r, string invoiceNo) => new(
        r.Id, invoiceNo, r.Type, r.Status, r.Amount, r.Currency, r.Reason, r.ProviderStatus, r.ProviderRef,
        r.RespCode, r.RespDesc, r.CreatedAt, r.CompletedAt);
}
