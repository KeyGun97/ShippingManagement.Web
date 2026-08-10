using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using ShippingManagement.Web.Data;
using ShippingManagement.Web.Infrastructure;
using ShippingManagement.Web.Services;

// ── DATE-BINDING FIX (Daily Report / Import Data filters) ────────────────────
// <input type="date"> ALWAYS submits ISO "yyyy-MM-dd", but MVC model binding
// parses DateTime? using the SERVER's current culture. On a Windows host whose
// regional short-date format is not ISO (dd/MM/yyyy, dd-MM-yy, a custom format…)
// the bind silently fails: the action parameter stays null and the controller
// falls back to DateTime.Today. That is exactly the reported symptom — "pick a
// date range, press Show Report, always get today's rows".
// Pinning the process to the invariant culture makes date parsing deterministic
// on every machine the app is deployed to.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// MVC + GLOBAL session-authorization filter → user session logic runs on EVERY component.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<SessionAuthorizeFilter>();

    // .NET 8 caps bound collections at 1024 items by default. "Save Filtered to
    // History" posts one selectedIds field per checked row (a full day can be 1400+),
    // so raise the cap or model binding throws before the action runs.
    options.MaxModelBindingCollectionSize = 100_000;
});

// A form with >1024 fields is rejected during form parsing (ValueCountLimit = 1024)
// before model binding — the same "Save Filtered" post trips this first. Raise it too.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.ValueCountLimit = 100_000;
});

// Server-side session (sliding expiry, HttpOnly cookie).
int timeout = builder.Configuration.GetValue("Session:TimeoutMinutes", 30);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o =>
{
    o.IdleTimeout = TimeSpan.FromMinutes(timeout);
    o.Cookie.Name = ".ShippingMgmt.Session";
    o.Cookie.HttpOnly = true;
    o.Cookie.IsEssential = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<ShippingRepository>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<ScrapeProgressService>();  // live "Fetch Data" progress/ETA
builder.Services.AddSingleton<ScraperService>();
builder.Services.AddSingleton<EmailService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();   // must precede endpoint execution

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// ── First-run seeding: create default admin if Users table is empty ──────────
try
{
    var cs = app.Configuration.GetConnectionString("ShippingDB");
    using var conn = new SqlConnection(cs);
    var count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Users");
    if (count == 0)
    {
        conn.Execute(@"INSERT INTO Users (Username, PasswordHash, FullName, Role, IsActive)
                       VALUES (@u, @p, @f, 'Admin', 1)",
            new { u = "admin", p = PasswordHasher.Hash("Admin@123"), f = "System Administrator" });
        app.Logger.LogWarning("Seeded default admin user: admin / Admin@123 — change this password immediately.");
    }
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Could not verify/seed Users table. Run Database/ShippingDB_Web.sql and check the connection string.");
}

app.Run();