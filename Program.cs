using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Security;
using UncoveringGreatnessCRM.Services;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var isDev = builder.Environment.IsDevelopment();

// ---------------------------------------------------------------- database
var provider = cfg["Database:Provider"] ?? "Sqlite";
builder.Services.AddDbContext<AppDbContext>(o =>
{
    var cs = cfg.GetConnectionString("Default") ?? "Data Source=App_Data/crm.db";
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)) o.UseSqlServer(cs);
    else
    {
        Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
        o.UseSqlite(cs);
    }
});

// ---------------------------------------------------------------- data protection (keys must survive restarts)
var keyDir = cfg["DataProtection:KeyPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
Directory.CreateDirectory(keyDir);
builder.Services.AddDataProtection().SetApplicationName("UncoveringGreatnessCRM").PersistKeysToFileSystem(new DirectoryInfo(keyDir));

// ---------------------------------------------------------------- services
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.Configure<EmailOptions>(cfg.GetSection("Email"));
builder.Services.Configure<ReminderOptions>(cfg.GetSection("Reminders"));
builder.Services.Configure<NotificationOptions>(cfg.GetSection("Notifications"));
var emailSettings = cfg.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();
if (!emailSettings.IsConfigured) builder.Services.AddScoped<IEmailSender, LoggingEmailSender>();
else if (emailSettings.UsesBrevoApi)
{
    builder.Services.AddHttpClient(BrevoEmailSender.ClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddScoped<IEmailSender, BrevoEmailSender>();
}
else builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();

builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<ApiTokenService>();
builder.Services.AddSingleton<IUserStateCache, UserStateCache>();
builder.Services.AddSingleton<ICampaignQueue, CampaignQueue>();
builder.Services.AddSingleton<IEmailQueue, EmailQueue>();
builder.Services.AddSingleton<IAuthorizationHandler, ActiveUserHandler>();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<AppUrls>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<LeadService>();
builder.Services.AddScoped<CompanyService>();
builder.Services.AddScoped<EventService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<CalendarService>();
builder.Services.AddScoped<CampaignService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<FormConnectionService>();
builder.Services.AddScoped<FormIngestService>();
builder.Services.AddHostedService<CampaignSenderWorker>();
builder.Services.AddHostedService<EmailDispatchWorker>();
builder.Services.AddHostedService<ReminderWorker>();

// ---------------------------------------------------------------- authentication
builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = AuthSchemes.Selector;
        o.DefaultChallengeScheme = AuthSchemes.Selector;
    })
    .AddPolicyScheme(AuthSchemes.Selector, "Cookie or Bearer", o =>
        o.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? AuthSchemes.Bearer : AuthSchemes.Cookie)
    .AddCookie(AuthSchemes.Cookie, o =>
    {
        o.Cookie.Name = isDev ? "ug.auth" : "__Host-ug.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = isDev ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.LogoutPath = "/Account/Logout";
        o.ExpireTimeSpan = TimeSpan.FromMinutes(cfg.GetValue("Security:SessionMinutes", 60));
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var id = int.TryParse(ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var i) ? i : 0;
            var state = id == 0 ? null : await ctx.HttpContext.RequestServices.GetRequiredService<IUserStateCache>().GetAsync(id);
            if (state is not { IsActive: true } || state.Stamp != ctx.Principal?.FindFirst(ClaimsFactory.StampClaim)?.Value)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(AuthSchemes.Cookie);
            }
        };
    })
    .AddScheme<AuthenticationSchemeOptions, BearerTokenHandler>(AuthSchemes.Bearer, _ => { });

builder.Services.AddAuthorization(o =>
{
    var active = new ActiveUserRequirement();
    AuthorizationPolicy Build(params string[] roles)
    {
        var b = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(active);
        if (roles.Length > 0) b.RequireRole(roles);
        return b.Build();
    }
    o.DefaultPolicy = Build();
    o.FallbackPolicy = Build(); // every endpoint requires a signed-in, active user unless marked [AllowAnonymous]
    o.AddPolicy("AdminOnly", Build(Roles.Admin));
    o.AddPolicy("CanWrite", Build(Roles.Admin, Roles.SalesRep));
});

// ---------------------------------------------------------------- MVC / antiforgery / API behaviour
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    o.Cookie.Name = isDev ? "ug.csrf" : "__Host-ug.csrf";
    o.Cookie.SecurePolicy = isDev ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.Configure<ApiBehaviorOptions>(o =>
    o.InvalidModelStateResponseFactory = ctx => new BadRequestObjectResult(new
    {
        error = "Validation failed.",
        errors = ctx.ModelState.Where(e => e.Value?.Errors.Count > 0)
            .ToDictionary(e => e.Key, e => e.Value!.Errors.Select(x => x.ErrorMessage).ToArray())
    }));
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 5 * 1024 * 1024);

// ---------------------------------------------------------------- rate limiting
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    // Strict limit on anything that accepts credentials; the per-account lockout in AuthService is the second layer.
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("public", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Webhook senders (Google, Tally...) share IP ranges, so limit per connection token instead of per IP.
    o.AddPolicy("webhook", c => RateLimitPartition.GetFixedWindowLimiter("wh:" + (c.Request.RouteValues["token"]?.ToString() ?? Ip(c)),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        RateLimitPartition.GetSlidingWindowLimiter(c.User.Identity?.IsAuthenticated == true ? "u:" + c.User.Identity.Name : "ip:" + Ip(c),
            _ => new SlidingWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 }));
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        if (ctx.HttpContext.Request.Path.StartsWithSegments("/api"))
            await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests. Please slow down." }, ct);
        else
            await ctx.HttpContext.Response.WriteAsync("Too many requests. Please wait a minute and try again.", ct);
    };
});

// Behind a reverse proxy / IIS the real client address arrives in X-Forwarded-*.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear(); o.KnownProxies.Clear();
    if (cfg.GetValue("Security:TrustForwardedHeaders", false) == false) o.ForwardedHeaders = ForwardedHeaders.None;
});

builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; });

var app = builder.Build();

app.UseForwardedHeaders();
if (!isDev)
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStatusCodePagesWithReExecute("/Home/Status", "?code={0}");
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter(); // after authentication so signed-in users are limited per account rather than per IP
app.UseAuthorization();
app.UseMiddleware<MustChangePasswordMiddleware>();

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
// Lightweight liveness probe for the host (Render) and the deployment pipeline. Touches the database so a broken connection is visible.
app.MapGet("/healthz", async (UncoveringGreatnessCRM.Data.AppDbContext db) =>
    await db.Database.CanConnectAsync() ? Results.Text("ok") : Results.StatusCode(503)).AllowAnonymous();

await DbSeeder.InitialiseAsync(app.Services);

// Make the email mode obvious in the log at startup.
{
    var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var startupEmail = cfg.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();
    if (!startupEmail.IsConfigured)
        startupLog.LogWarning("Email is NOT configured: emails (password resets, campaigns) are only logged. Set the Email section to send for real.");
    else if (startupEmail.UsesBrevoApi)
        startupLog.LogInformation("Email is configured: sending through the Brevo API as {From}.", startupEmail.FromAddress);
    else
    {
        startupLog.LogInformation("Email is configured: sending via {Host}:{Port} as {From}.", cfg["Email:Host"], cfg["Email:Port"], cfg["Email:FromAddress"]);
        if (cfg.GetValue("Email:Port", 587) == 465)
            startupLog.LogWarning("Email:Port is 465, which the built-in mail client does not support. Use 587 with Email:UseSsl=true.");
        if (!string.IsNullOrWhiteSpace(cfg["Email:Username"]) && string.IsNullOrWhiteSpace(cfg["Email:Password"]))
            startupLog.LogWarning("Email:Username is set but Email:Password is empty, so the mail server will probably refuse to send.");
    }
    if (!isDev && string.IsNullOrWhiteSpace(cfg["App:PublicBaseUrl"]))
        startupLog.LogWarning("App:PublicBaseUrl is empty: links in emails and the form webhook URL will use the request host. Set it to your public HTTPS address.");
}
app.Run();

// Makes the entry point visible to the integration-test project (WebApplicationFactory<Program>).
public partial class Program { }
