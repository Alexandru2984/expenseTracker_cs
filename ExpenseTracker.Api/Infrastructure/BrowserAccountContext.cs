using Microsoft.AspNetCore.Mvc;

namespace ExpenseTracker.Api.Infrastructure;

public static class BrowserAccountContext
{
    public static ProblemDetails Changed() => new()
    {
        Status = StatusCodes.Status409Conflict,
        Title = "Contul activ s-a schimbat. Reconectează interfața înainte de a continua.",
        Extensions = { ["code"] = "account_context_changed" }
    };
}
