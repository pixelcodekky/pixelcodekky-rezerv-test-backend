using Microsoft.Extensions.Logging;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;

namespace Rezerv.Application.Services;

/// <summary>
/// Removes a saved card: first from the 2C2P vault (Store Card Maintenance, action D), then from MySQL and the Redis
/// card list. 2C2P goes first so a card is never hidden from the customer while its token can still be charged.
/// A Redis lock per card keeps a double click from sending two deletions.
/// </summary>
public sealed class CustomerCardService(ICustomerRepository customers, ICustomerCardRepository cards, IPaymentTransactionRepository transactions,
    IPaymentActionGateway gateway, ICacheService cache, CheckoutOptions options, ILogger<CustomerCardService> logger)
{
    private const string InProgressMessage = "This card is already being removed. Retry shortly.";

    public async Task RemoveAsync(int customerId, int customerCardId, CancellationToken ct = default)
    {
        var customer = await customers.GetByIdAsync(customerId, ct)
            ?? throw new NotFoundException($"Customer {customerId} not found.");
        await FindCardAsync(customerId, customerCardId, ct);

        await using var lease = await cache.AcquireLockAsync(CacheKeys.CardRemovalLock(customerCardId), options.LockTtl, ct)
            ?? throw new ConflictException(InProgressMessage);

        // Re-read under the lock: a concurrent request may have removed it in the meantime.
        var card = await FindCardAsync(customerId, customerCardId, ct);

        // Removing the token now would break a payment still waiting for 3DS or the inquiry.
        if (await transactions.HasPendingForCardAsync(card.Id, ct))
            throw new ConflictException("A payment with this card is still being processed. Try again once it has completed.");

        if (!gateway.IsConfigured)
            throw new NotConfiguredException("Removing cards needs the 2C2P Payment Action keys (_2C2P:PaymentAction settings).");

        // From here on, finish even if the client disconnects, so 2C2P and MySQL do not drift apart.
        var result = await gateway.RemoveCardTokenAsync(
            new CardTokenRemovalRequest(card.CustomerToken, $"{customer.FirstName} {customer.LastName}", customer.Email), CancellationToken.None);
        if (!result.Removed)
        {
            logger.LogWarning("2C2P refused to remove card {CardId} of customer {CustomerId}: {RespCode} {RespReason}",
                card.Id, customerId, result.RespCode, result.RespReason);
            throw new PaymentGatewayException($"2C2P did not remove the card ({result.RespCode}: {result.RespReason}). The card was kept.");
        }

        await cards.DeleteAsync(card.Id, customerId, CancellationToken.None);
        await cache.RemoveAsync(CacheKeys.CustomerCards(customerId), CancellationToken.None);
        logger.LogInformation("Removed card {CardId} ({MaskedCardNo}) of customer {CustomerId} at 2C2P and locally",
            card.Id, card.MaskedCardNo, customerId);
    }

    private async Task<Domain.Entities.CustomerCard> FindCardAsync(int customerId, int customerCardId, CancellationToken ct)
    {
        var card = await cards.GetByIdAsync(customerCardId, ct);
        if (card is null || card.CustomerId != customerId)
            throw new NotFoundException($"Saved card {customerCardId} not found for customer {customerId}.");
        return card;
    }
}
