using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Rezerv.API.Infrastructure;
using Rezerv.Application.Common;

namespace Rezerv.Tests.Api;

public class AppExceptionHandlerTests
{
    private readonly Mock<IProblemDetailsService> _problemDetails = new();
    private ProblemDetailsContext? _written;

    public AppExceptionHandlerTests()
    {
        _problemDetails.Setup(p => p.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .Callback<ProblemDetailsContext>(c => _written = c)
            .ReturnsAsync(true);
    }

    private async Task<HttpContext> Handle(Exception exception)
    {
        var context = new DefaultHttpContext();
        var handled = await new AppExceptionHandler(_problemDetails.Object, NullLogger<AppExceptionHandler>.Instance)
            .TryHandleAsync(context, exception, default);
        Assert.True(handled);
        return context;
    }

    public static TheoryData<Exception, int> Mappings => new()
    {
        { new NotFoundException("x"), StatusCodes.Status404NotFound },
        { new ValidationException("x"), StatusCodes.Status400BadRequest },
        { new InvalidSignatureException("x"), StatusCodes.Status400BadRequest },
        { new IdempotencyKeyMismatchException("x"), StatusCodes.Status422UnprocessableEntity },
        { new ConflictException("x"), StatusCodes.Status409Conflict },
        { new PaymentGatewayException("x"), StatusCodes.Status502BadGateway },
        { new NotConfiguredException("x"), StatusCodes.Status503ServiceUnavailable },
        { new InvalidOperationException("x"), StatusCodes.Status500InternalServerError }
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public async Task Maps_exceptions_to_status_codes(Exception exception, int status)
    {
        var context = await Handle(exception);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(status, _written!.ProblemDetails.Status);
    }

    [Fact]
    public async Task Application_exception_message_is_returned_as_detail()
    {
        await Handle(new NotFoundException("Transaction INV1 not found."));

        Assert.Equal("Transaction INV1 not found.", _written!.ProblemDetails.Detail);
    }

    [Fact]
    public async Task Unexpected_exception_details_are_not_leaked()
    {
        await Handle(new InvalidOperationException("connection string password=secret"));

        Assert.Null(_written!.ProblemDetails.Detail);
    }

    [Fact]
    public async Task Conflict_tells_the_client_when_to_retry()
    {
        var context = await Handle(new ConflictException("in progress"));

        Assert.Equal("1", context.Response.Headers.RetryAfter.ToString());
    }
}
