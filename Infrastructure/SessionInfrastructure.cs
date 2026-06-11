using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ShippingManagement.Web.Infrastructure
{
    /// <summary>Well-known session keys used across every component.</summary>
    public static class SessionKeys
    {
        public const string UserID   = "UserID";
        public const string Username = "Username";
        public const string FullName = "FullName";
        public const string Role     = "Role";
        public const string LoginAt  = "LoginAt";
    }

    /// <summary>Marks an action/controller as reachable without a session (Login only).</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class AllowAnonymousSessionAttribute : Attribute { }

    /// <summary>Marks a controller/action as Admin-only (checked against the session role).</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class RequireAdminAttribute : Attribute { }

    /// <summary>
    /// GLOBAL session guard — registered for every controller and action, so user
    /// session logic is enforced on each component automatically:
    ///   1. No session UserID  → redirect to /Account/Login (or 401 JSON for AJAX).
    ///   2. [RequireAdmin]     → session Role must be 'Admin', otherwise 403.
    ///   3. Each request slides the session expiry (handled by the session middleware).
    /// </summary>
    public sealed class SessionAuthorizeFilter : IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            bool anonymousAllowed =
                context.ActionDescriptor.EndpointMetadata.OfType<AllowAnonymousSessionAttribute>().Any() ||
                context.Controller.GetType().GetCustomAttributes(typeof(AllowAnonymousSessionAttribute), true).Any();

            var session = context.HttpContext.Session;
            int? userId = session.GetInt32(SessionKeys.UserID);

            if (!anonymousAllowed && userId is null)
            {
                bool isAjax = context.HttpContext.Request.Headers.XRequestedWith == "XMLHttpRequest" ||
                              context.HttpContext.Request.Headers.Accept.ToString().Contains("application/json");
                if (isAjax)
                {
                    context.Result = new JsonResult(new { ok = false, error = "Session expired. Please log in again." })
                    { StatusCode = StatusCodes.Status401Unauthorized };
                }
                else
                {
                    var returnUrl = context.HttpContext.Request.Path + context.HttpContext.Request.QueryString;
                    context.Result = new RedirectToActionResult("Login", "Account", new { returnUrl });
                }
                return;
            }

            bool adminRequired =
                context.ActionDescriptor.EndpointMetadata.OfType<RequireAdminAttribute>().Any() ||
                context.Controller.GetType().GetCustomAttributes(typeof(RequireAdminAttribute), true).Any();

            if (adminRequired && session.GetString(SessionKeys.Role) != "Admin")
            {
                context.Result = new ViewResult { ViewName = "AccessDenied", StatusCode = StatusCodes.Status403Forbidden };
                return;
            }

            await next();
        }
    }

    /// <summary>Convenience extensions to read the current session user anywhere.</summary>
    public static class SessionExtensions
    {
        public static int CurrentUserId(this HttpContext ctx)  => ctx.Session.GetInt32(SessionKeys.UserID) ?? 0;
        public static string CurrentUserName(this HttpContext ctx) => ctx.Session.GetString(SessionKeys.FullName) ?? "";
        public static bool IsAdmin(this HttpContext ctx)       => ctx.Session.GetString(SessionKeys.Role) == "Admin";
    }

    /// <summary>PBKDF2 password hashing (no external packages). Format: iterations.saltB64.hashB64</summary>
    public static class PasswordHasher
    {
        private const int Iterations = 100_000;

        public static string Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
            return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        }

        public static bool Verify(string password, string stored)
        {
            try
            {
                var parts = stored.Split('.');
                int iters = int.Parse(parts[0]);
                byte[] salt = Convert.FromBase64String(parts[1]);
                byte[] expected = Convert.FromBase64String(parts[2]);
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch { return false; }
        }
    }
}
