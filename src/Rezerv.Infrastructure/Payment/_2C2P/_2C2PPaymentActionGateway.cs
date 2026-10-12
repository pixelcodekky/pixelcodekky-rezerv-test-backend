using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;

namespace Rezerv.Infrastructure.Payment._2C2P
{
    /// <summary>
    /// 2C2P Payment Maintenance (Payment Action API). The request is a PaymentProcessRequest XML document, encrypted
    /// and signed (<see cref="RsaJose"/>) and POSTed as text/plain; the response is a signed and encrypted
    /// PaymentProcessResponse. Like the payment gateway, the HttpClient has no retry policy: a retried refund is
    /// left to the idempotencyID and our own reconciliation instead.
    /// </summary>
    public sealed class _2C2PPaymentActionGateway(HttpClient http, PaymentActionKeys keys, IOptions<_2C2POptions> options,
        ILogger<_2C2PPaymentActionGateway> logger) : IPaymentActionGateway
    {
        private readonly _2C2POptions _options = options.Value;

        public bool IsConfigured => keys.IsConfigured;

        public async Task<PaymentActionStatus> GetPaymentStatusAsync(string invoiceNo, decimal amount, CancellationToken ct = default)
        {
            var response = await SendAsync(_2C2PActionCodes.Inquiry, invoiceNo, amount, null, ct);
            var respCode = response.Val("respCode") ?? string.Empty;
            var status = response.Val("status");
            var state = respCode == _2C2PActionCodes.Success ? _2C2PActionCodes.ToCaptureState(status) : CaptureState.Other;
            logger.LogInformation("2C2P payment status for {InvoiceNo}: {RespCode} {Status} -> {State}", invoiceNo, respCode, status, state);
            return new PaymentActionStatus(state, respCode, response.Val("respDesc"), status);
        }

        public Task<RefundActionResult> VoidAsync(RefundActionRequest request, CancellationToken ct = default) =>
            ActAsync(_2C2PActionCodes.Void, request, _2C2PActionCodes.ToVoidOutcome, ct);

        public Task<RefundActionResult> RefundAsync(RefundActionRequest request, CancellationToken ct = default) =>
            ActAsync(_2C2PActionCodes.Refund, request, _2C2PActionCodes.ToRefundOutcome, ct);

        public async Task<RefundStatusResult> GetRefundStatusAsync(string invoiceNo, decimal amount, CancellationToken ct = default)
        {
            var response = await SendAsync(_2C2PActionCodes.RefundStatus, invoiceNo, amount, null, ct);
            var entries = response.Descendants("refund")
                .Select(r => new RefundStatusEntry(
                    r.Decimal("amount") ?? 0m,
                    _2C2PActionCodes.ToRefundOutcome(r.Val("status")),
                    r.Val("status"),
                    r.Val("referenceNo")))
                .ToList();
            logger.LogInformation("2C2P refund status for {InvoiceNo}: {RespCode}, {Count} refund(s)", invoiceNo, response.Val("respCode"), entries.Count);
            return new RefundStatusResult(response.Val("respCode") ?? string.Empty, response.Val("respDesc"), response.Val("status"), entries);
        }

        private async Task<RefundActionResult> ActAsync(string processType, RefundActionRequest request, Func<string?, RefundOutcome> outcome, CancellationToken ct)
        {
            var response = await SendAsync(processType, request.InvoiceNo, request.Amount, request.IdempotencyId, ct);
            var respCode = response.Val("respCode") ?? string.Empty;
            var status = response.Val("status");
            // A non-success respCode is a rejection; with success, the status says whether it is final yet.
            var result = respCode == _2C2PActionCodes.Success ? outcome(status) : RefundOutcome.Failed;
            logger.LogInformation("2C2P {ProcessType} for {InvoiceNo} ({Amount}): {RespCode} {RespDesc} {Status} -> {Outcome}",
                processType, request.InvoiceNo, request.Amount, respCode, response.Val("respDesc"), status, result);

            return new RefundActionResult(result, respCode, response.Val("respDesc"), status,
                response.Val("refundReferenceNo") ?? response.Val("referenceNo"));
        }

        public async Task<CardTokenRemovalResult> RemoveCardTokenAsync(CardTokenRemovalRequest request, CancellationToken ct = default)
        {
            var response = await PostAsync(BuildMaintenanceRequest(request), "card token removal", ct);

            var answeredToken = response.Val("storeCardUniqueID");
            if (answeredToken is not null && !string.Equals(answeredToken, request.CustomerToken, StringComparison.Ordinal))
                throw new PaymentGatewayException("2C2P card token removal answered for a different card token.");

            var respCode = response.Val("respCode") ?? string.Empty;
            var removed = respCode == _2C2PActionCodes.Success;
            logger.LogInformation("2C2P card token removal ({MaskedCardNo}): {RespCode} {RespReason}",
                response.Val("panMasked"), respCode, response.Val("respReason"));
            return new CardTokenRemovalResult(removed, respCode, response.Val("respReason"));
        }

        private async Task<XElement> SendAsync(string processType, string invoiceNo, decimal amount, string? idempotencyId, CancellationToken ct)
        {
            var root = await PostAsync(BuildRequest(processType, invoiceNo, amount, idempotencyId), processType, ct);

            var answeredInvoice = root.Val("invoiceNo");
            if (answeredInvoice is not null && !string.Equals(answeredInvoice, invoiceNo, StringComparison.Ordinal))
                throw new PaymentGatewayException($"2C2P Payment Action {processType} answered for invoice {answeredInvoice}, not {invoiceNo}.");
            return root;
        }

        private async Task<XElement> PostAsync(string xml, string processType, CancellationToken ct)
        {
            var body = RsaJose.EncryptAndSign(xml, keys.TwoC2PPublic, keys.MerchantPrivate);

            string responseBody;
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "text/plain");
                using var response = await http.PostAsync(_options.PaymentAction.Url, content, ct);
                responseBody = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new PaymentGatewayException($"2C2P Payment Action {processType} returned HTTP {(int)response.StatusCode}: {Preview(responseBody)}");
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new PaymentGatewayException($"2C2P Payment Action {processType} timed out.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new PaymentGatewayException($"2C2P Payment Action {processType} request failed: {ex.Message}", ex);
            }

            // Only a signed answer is acted on; a plain error body can't be trusted as a refund result.
            string decrypted;
            try
            {
                decrypted = RsaJose.VerifyAndDecrypt(responseBody, keys.TwoC2PPublic, keys.MerchantPrivate);
            }
            catch (InvalidSignatureException ex)
            {
                throw new PaymentGatewayException(
                    $"2C2P Payment Action {processType} returned an unsigned or invalid response: {Preview(responseBody)}", ex);
            }

            XElement root;
            try
            {
                root = XElement.Parse(decrypted);
            }
            catch (XmlException ex)
            {
                throw new PaymentGatewayException($"2C2P Payment Action {processType} returned invalid XML.", ex);
            }
            return root;
        }

        /// <summary>Store Card Maintenance request, action D (delete). pan and panExpiry are optional for a delete.</summary>
        internal string BuildMaintenanceRequest(CardTokenRemovalRequest request) =>
            new XElement("MaintenanceRequest",
                new XElement("version", _options.PaymentAction.MaintenanceVersion),
                new XElement("timeStamp", DateTime.UtcNow.ToString("ddMMyyHHmmss", CultureInfo.InvariantCulture)),
                new XElement("merchantID", _options.MerchantId),
                new XElement("storeCardUniqueID", request.CustomerToken),
                new XElement("cardholderName", Truncate(request.CardholderName, 50)),
                request.CardholderEmail is null ? null : new XElement("cardholderEmail", Truncate(request.CardholderEmail, 50)),
                new XElement("action", _2C2PActionCodes.DeleteCardToken))
            .ToString(SaveOptions.DisableFormatting);

        private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

        internal string BuildRequest(string processType, string invoiceNo, decimal amount, string? idempotencyId) =>
            new XElement("PaymentProcessRequest",
                new XElement("version", _options.PaymentAction.Version),
                new XElement("timeStamp", DateTime.UtcNow.ToString("ddMMyyHHmmss", CultureInfo.InvariantCulture)),
                new XElement("merchantID", _options.MerchantId),
                new XElement("processType", processType),
                new XElement("invoiceNo", invoiceNo),
                new XElement("actionAmount", amount.ToString("0.00", CultureInfo.InvariantCulture)),
                idempotencyId is null ? null : new XElement("idempotencyID", idempotencyId))
            .ToString(SaveOptions.DisableFormatting);

        private static string Preview(string body) => body.Length <= 200 ? body : body[..200] + "...";
    }

    /// <summary>Payment Action processType, respCode and status values.</summary>
    internal static class _2C2PActionCodes
    {
        public const string Inquiry = "I";
        public const string Void = "V";
        public const string Refund = "R";
        public const string RefundStatus = "RS";

        /// <summary>Store Card Maintenance action: delete a card token.</summary>
        public const string DeleteCardToken = "D";

        public const string Success = "00";

        public static CaptureState ToCaptureState(string? status) => status switch
        {
            "A" or "RS" => CaptureState.Authorized,                                      // approved / ready for settlement
            "S" or "RF" or "RP" or "RFF" or "RR" or "RR1" or "RR2" or "RR3" => CaptureState.Settled, // settled, maybe partly refunded
            "V" => CaptureState.Voided,
            _ => CaptureState.Other
        };

        public static RefundOutcome ToVoidOutcome(string? status) => status switch
        {
            "V" => RefundOutcome.Succeeded,
            _ => RefundOutcome.Pending // VP, or no status: confirmed later by Payment Inquiry
        };

        public static RefundOutcome ToRefundOutcome(string? status) => status switch
        {
            "RF" => RefundOutcome.Succeeded,
            "RFF" or "RR" or "RR1" or "RR2" or "RR3" => RefundOutcome.Failed,
            _ => RefundOutcome.Pending // RP / REFUND_PENDING, or no status: confirmed later by Refund Status Inquiry
        };
    }

    internal static class XElementExtensions
    {
        /// <summary>Child element or attribute value (2C2P uses both, e.g. refundList/refund).</summary>
        public static string? Val(this XElement e, string name)
        {
            var value = e.Element(name)?.Value ?? e.Attribute(name)?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public static decimal? Decimal(this XElement e, string name) =>
            decimal.TryParse(e.Val(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
