using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellySpot.Controllers;

public sealed class JellySpotExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        var status = context.Exception switch
        {
            UnauthorizedAccessException => 401,
            InvalidOperationException => 409,
            HttpRequestException => 502,
            _ => 0
        };
        if (status == 0) return;
        context.Result = new ObjectResult(new { Error = status == 401 ? "Sign in to Jellyfin again." : status == 409 ? "Spotify needs attention. Check your connection in Sync and try again." : "Spotify is temporarily unavailable. Try again later." }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
