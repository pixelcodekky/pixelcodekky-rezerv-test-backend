using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Rezerv.Application.Common;

namespace Rezerv.API.Infrastructure;

/// <summary>Maps application exceptions to RFC 7807 problem responses.</summary>
public sealed class AppExceptionHandler(IProblemDetailsService problemDetails, ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ValidationException => (StatusCodes.Status400BadRequest, "Invalid request"),
            InvalidSignatureException => (StatusCodes.Status400BadRequest, "Invalid signature"),
            IdempotencyKeyMismatchException => (StatusCodes.Status422UnprocessableEntity, "Idempotency-Key reused"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            PaymentGatewayException => (StatusCodes.Status502BadGateway, "Payment gateway error"),
            NotConfiguredException => (StatusCodes.Status503ServiceUnavailable, "Not configured"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
        };

        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception");
        else
            logger.LogInformation("{Status} {Title}: {Message}", status, title, exception.Message);

        if (exception is ConflictException)
            context.Response.Headers.RetryAfter = "1";

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = exception is AppException ? exception.Message : null
            }
        });
    }
}
