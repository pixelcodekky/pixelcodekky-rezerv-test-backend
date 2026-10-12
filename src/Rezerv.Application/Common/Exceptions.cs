namespace Rezerv.Application.Common
{
    public abstract class AppException(string message, Exception? inner = null) : Exception(message, inner);

    public sealed class NotFoundException(string message) : AppException(message);

    public sealed class ValidationException(string message) : AppException(message);

    public sealed class ConflictException(string message, Exception? inner = null) : AppException(message, inner);

    /// <summary>An Idempotency-Key was replayed with a different request body.</summary>
    public sealed class IdempotencyKeyMismatchException(string message) : AppException(message);

    public sealed class InvalidSignatureException(string message) : AppException(message);

    /// <summary>A feature that needs configuration (e.g. 2C2P Payment Action keys) is not set up.</summary>
    public sealed class NotConfiguredException(string message) : AppException(message);

    public sealed class PaymentGatewayException(string message, Exception? inner = null) : AppException(message, inner);
}
