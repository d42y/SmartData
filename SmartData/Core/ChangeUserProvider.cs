using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace SmartData.Core
{
    public interface IChangeUserProvider
    {
        string GetCurrentUser();
    }

    public sealed class NullChangeUserProvider : IChangeUserProvider
    {
        public string GetCurrentUser() => "System";
    }
    public sealed class HttpContextChangeUserProvider : IChangeUserProvider
    {
        private readonly IHttpContextAccessor _http;
        public HttpContextChangeUserProvider(IHttpContextAccessor http) => _http = http;

        public string GetCurrentUser()
        {
            var user = _http.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                // order of preference
                return user.FindFirst("preferred_username")?.Value
                    ?? user.FindFirst("name")?.Value
                    ?? user.FindFirst(ClaimTypes.Email)?.Value
                    ?? user.FindFirst(ClaimTypes.Upn)?.Value
                    ?? user.Identity!.Name
                    ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? "System";
            }
            // background jobs / no HTTP context
            return "System";
        }
    }
}
