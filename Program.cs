using Dapper;
using Microsoft.Data.SqlClient;
using ShippingManagement.Web.Data;
using ShippingManagement.Web.Infrastructure;
using ShippingManagement.Web.Services;

        var builder = WebApplication.CreateBuilder(args);
        var server = $"{Environment.MachineName}";
        var connectionString =
            $"Server={server};Database=ShippingDB;Trusted_Connection=True;TrustServerCertificate=True";

// MVC + GLOBAL session-authorization filter → user session logic runs on EVERY component.
builder.Services.AddControllersWithViews(options =>
        {
            options.Filters.Add<SessionAuthorizeFilter>();
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
            //var cs = builder.Services.AddScoped<SqlConnection>(_ =>new SqlConnection(connectionString)); //app.Configuration.GetConnectionString("ShippingDB");
            using var conn = new SqlConnection(connectionString);
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
