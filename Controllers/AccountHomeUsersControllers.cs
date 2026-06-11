using Microsoft.AspNetCore.Mvc;
using ShippingManagement.Web.Data;
using ShippingManagement.Web.Infrastructure;
using ShippingManagement.Web.Models;

namespace ShippingManagement.Web.Controllers
{
    /* ════════════════════ ACCOUNT (login / logout) ════════════════════ */
    public class AccountController : Controller
    {
        private readonly ShippingRepository _repo;
        public AccountController(ShippingRepository repo) => _repo = repo;

        [AllowAnonymousSession]
        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            if (HttpContext.Session.GetInt32(SessionKeys.UserID) is not null)
                return RedirectToAction("Index", "Home");
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        [AllowAnonymousSession]
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Login(string username, string password, string? returnUrl = null)
        {
            var user = _repo.GetUserByUsername(username?.Trim() ?? "");
            if (user is null || !PasswordHasher.Verify(password ?? "", user.PasswordHash))
            {
                ViewBag.Error = "Invalid username or password.";
                ViewBag.ReturnUrl = returnUrl;
                return View();
            }

            // ── Establish the user session used by every component ──
            HttpContext.Session.SetInt32(SessionKeys.UserID, user.UserID);
            HttpContext.Session.SetString(SessionKeys.Username, user.Username);
            HttpContext.Session.SetString(SessionKeys.FullName, user.FullName);
            HttpContext.Session.SetString(SessionKeys.Role, user.Role);
            HttpContext.Session.SetString(SessionKeys.LoginAt, DateTime.UtcNow.ToString("o"));

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);
            return RedirectToAction("Index", "Home");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Login");
        }
    }

    /* ════════════════════ HOME (dashboard tiles) ════════════════════ */
    public class HomeController : Controller
    {
        private readonly ShippingRepository _repo;
        public HomeController(ShippingRepository repo) => _repo = repo;

        public IActionResult Index()
        {
            var (vessels, companies, regulars, today) = _repo.GetDashboardCounts();
            ViewBag.Vessels = vessels; ViewBag.Companies = companies;
            ViewBag.Regulars = regulars; ViewBag.TodayArrivals = today;
            return View();
        }

        [AllowAnonymousSession]
        public IActionResult Error() => View();
    }

    /* ════════════════════ USERS (Admin only) ════════════════════ */
    [RequireAdmin]
    public class UsersController : Controller
    {
        private readonly ShippingRepository _repo;
        public UsersController(ShippingRepository repo) => _repo = repo;

        public IActionResult Index() => View(_repo.GetAllUsers().ToList());

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Create(string username, string password, string fullName, string role)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                TempData["Error"] = "Username and password are required.";
            else if (_repo.GetUserByUsername(username.Trim()) is not null)
                TempData["Error"] = $"Username '{username}' already exists.";
            else
            {
                _repo.CreateUser(new User
                {
                    Username = username.Trim(),
                    PasswordHash = PasswordHasher.Hash(password),
                    FullName = string.IsNullOrWhiteSpace(fullName) ? username.Trim() : fullName.Trim(),
                    Role = role == "Admin" ? "Admin" : "User",
                    IsActive = true
                });
                TempData["Ok"] = $"User '{username}' created.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult ToggleActive(int id, bool active)
        {
            if (id == HttpContext.CurrentUserId())
                TempData["Error"] = "You cannot deactivate your own account.";
            else { _repo.SetUserActive(id, active); TempData["Ok"] = "User updated."; }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult ResetPassword(int id, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(newPassword))
                TempData["Error"] = "Password cannot be empty.";
            else { _repo.ResetPassword(id, PasswordHasher.Hash(newPassword)); TempData["Ok"] = "Password reset."; }
            return RedirectToAction(nameof(Index));
        }
    }
}
