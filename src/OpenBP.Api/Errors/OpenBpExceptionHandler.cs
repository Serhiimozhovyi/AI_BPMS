using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpenBP.Application.Errors;

namespace OpenBP.Api.Errors;

/// <summary>
/// Перетворює помилки application-шару на RFC 9457 <c>application/problem+json</c> з полем <c>code</c>
/// (docs/architecture.md §10.3). Помилка аргументів → 400.
/// </summary>
public sealed class OpenBpExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail) = exception switch
        {
            OpenBpException e => (e.StatusCode, e.Code, e.Message),
            BadHttpRequestException e => (e.StatusCode, "BAD_REQUEST", "Некоректний запит."),
            ArgumentException e => (StatusCodes.Status400BadRequest, "BAD_REQUEST", e.Message),
            _ => (0, null, null),
        };
        if (code is null)
        {
            return false;
        }

        var problem = new ProblemDetails { Status = status, Detail = detail };
        problem.Extensions["code"] = code;
        if (exception is WorkflowValidationException validation)
        {
            problem.Extensions["errors"] = validation.Errors;
        }
        else if (exception is CredentialValidationException credential)
        {
            problem.Extensions["errors"] = credential.Errors;
        }
        else if (exception is ImportUnmappedException unmapped)
        {
            problem.Extensions["unmapped"] = unmapped.Unmapped;
        }

        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
