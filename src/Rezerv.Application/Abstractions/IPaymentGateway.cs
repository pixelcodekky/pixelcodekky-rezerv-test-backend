using Rezerv.Domain.Entities;

namespace Rezerv.Application.Abstractions;

/// <summary>Port to the payment provider (2C2P PGW v4.3).</summary>
public interface IPaymentGateway
{
    /// <summary>Payment Token API - creates a payment session and the hosted payment page URL.</summary>
    Task<PaymentTokenResult> CreatePaymentTokenAsync(PaymentTokenRequest request, CancellationToken ct = default);

    /// <summary>Do Payment API - charges a saved card (customerToken) without the hosted page.</summary>
    Task<DoPaymentResult> DoPaymentAsync(DoPaymentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Payment Inquiry API - authoritative result for an invoice. The response JWT is verified like a notification,
    /// and the result is final: respCode 0000 = Succeeded, anything else = Failed.
    /// </summary>
    Task<PaymentResult> InquireAsync(string invoiceNo, CancellationToken ct = default);

    /// <summary>
    /// Verifies and decodes a backend notification {"payload": "&lt;JWT&gt;"}. Throws InvalidSignatureException on a bad signature.
    /// The result is final: respCode 0000 = Succeeded, anything else = Failed.
    /// </summary>
    PaymentResult ParseNotification(string payload);
}

/// <param name="PaymentChannels">null = no restriction (all methods enabled for the merchant).</param>
/// <param name="CustomerTokenOnly">Saved card: only the given customerToken may be used.</param>
/// <param name="Request3DS">"Y"/"N"; null leaves the merchant default.</param>
public sealed record PaymentTokenRequest(
    string InvoiceNo,
    string Description,
    decimal Amount,
    string CurrencyCode,
    bool Tokenize,
    string? CustomerToken,
    string CustomerName,
    string CustomerEmail,
    IReadOnlyList<string>? PaymentChannels,
    bool CustomerTokenOnly = false,
    string? Request3DS = null);

public sealed record PaymentTokenResult(bool IsSuccess, string? PaymentToken, string? WebPaymentUrl, string RespCode, string? RespDesc);

/// <param name="InvoiceNo">Fills the frontend return URL the browser comes back to after 3DS.</param>
public sealed record DoPaymentRequest(string InvoiceNo, string PaymentToken,string CustomerToken, string ClientIp, string CustomerName, string CustomerEmail);

public enum DoPaymentOutcome
{
    /// <summary>respCode 1001: 3DS needed; send the browser to RedirectUrl.</summary>
    Redirect,
    /// <summary>Any other respCode: no redirect; Payment Inquiry gives the result.</summary>
    Completed
}

public sealed record DoPaymentResult(DoPaymentOutcome Outcome, string RespCode, string? RespDesc, string? RedirectUrl);

/// <summary>Transaction result from a notification or an inquiry.</summary>
/// <param name="Status">Provider response code translated by the gateway; Pending means "no final result yet".</param>
public sealed record PaymentResult(
    string InvoiceNo,
    TransactionStatus Status,
    string RespCode,
    string? RespDesc,
    decimal? Amount,
    string? CurrencyCode,
    string? TranRef,
    string? AccountNo,
    string? ChannelCode,
    string? CustomerToken);
