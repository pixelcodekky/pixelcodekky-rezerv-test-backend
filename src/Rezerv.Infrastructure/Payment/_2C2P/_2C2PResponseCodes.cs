using Rezerv.Application.Abstractions;
using Rezerv.Domain.Entities;

namespace Rezerv.Infrastructure.Payment._2C2P
{
    /// <summary>
    /// Translates 2C2P respCodes. When unsure, map towards "ask again" (Pending / Completed → inquiry) rather than Failed:
    /// wrongly failing a payment that was actually charged is the worst outcome.
    /// </summary>
    internal static class _2C2PResponseCodes
    {
        public const string Success = "0000";

        /// <summary>Backend notification: the result is final, 0000 = Succeeded, anything else = Failed.</summary>
        public static TransactionStatus NotificationStatus(string respCode) =>
            respCode == Success ? TransactionStatus.Succeeded : TransactionStatus.Failed;

        /// <summary>
        /// Payment Inquiry is treated like a notification: 0000 = Succeeded, anything else = Failed.
        /// Callers only apply a Failed result when the customer can no longer be paying (see ReconcileMode).
        /// </summary>
        public static TransactionStatus InquiryStatus(string respCode) => NotificationStatus(respCode);

        public const string ThreeDsRedirect = "1001";

        /// <summary>
        /// Do Payment: 1001 = send the browser to the 3DS URL; any other code goes straight to the result page,
        /// where Payment Inquiry decides Succeeded / Failed (a rejection is never guessed from the Do Payment code).
        /// </summary>
        public static DoPaymentOutcome ToDoPaymentOutcome(string respCode, string? redirectUrl) =>
            respCode == ThreeDsRedirect && !string.IsNullOrWhiteSpace(redirectUrl)
                ? DoPaymentOutcome.Redirect
                : DoPaymentOutcome.Completed;
    }
}
