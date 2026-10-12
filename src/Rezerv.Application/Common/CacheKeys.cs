namespace Rezerv.Application.Common;

public static class CacheKeys
{
    public const string Customers = "rezerv:customers";
    public const string Products = "rezerv:products";

    public static string CustomerCards(int customerId) => $"rezerv:customers:{customerId}:cards";

    /// <summary>Distributed lock held while a checkout for this key creates the transaction and calls 2C2P.</summary>
    public static string CheckoutLock(string idempotencyKey) => $"rezerv:checkout:{idempotencyKey}:lock";

    /// <summary>Stored checkout response, replayed for a repeated Idempotency-Key.</summary>
    public static string CheckoutResponse(string idempotencyKey) => $"rezerv:checkout:{idempotencyKey}:response";

    /// <summary>Throttles Payment Inquiry calls made while a result page polls a pending transaction.</summary>
    public static string InquiryThrottle(string invoiceNo) => $"rezerv:inquiry:{invoiceNo}";

    /// <summary>Distributed lock held while a refund for this key reserves the amount and calls 2C2P.</summary>
    public static string RefundLock(string idempotencyKey) => $"rezerv:refund:{idempotencyKey}:lock";

    /// <summary>Distributed lock held while a saved card is removed at 2C2P and from MySQL.</summary>
    public static string CardRemovalLock(int customerCardId) => $"rezerv:card:{customerCardId}:remove:lock";

    /// <summary>Throttles refund status checks made while a client polls pending refunds.</summary>
    public static string RefundStatusThrottle(string invoiceNo) => $"rezerv:refund-status:{invoiceNo}";
}
