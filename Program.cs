using System.Security.Cryptography;
using System.Text;
using SalaReservas.Models;
using SalaReservas.Services;

var builder = WebApplication.CreateBuilder(args);

// Secretos: appsettings < variables de entorno (Graph__ClientSecret) < user-secrets (desarrollo).
builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection("Graph"));
builder.Services.Configure<RoomOptions>(builder.Configuration.GetSection("Room"));

if (builder.Configuration.GetValue<bool>("Room:DemoMode"))
    builder.Services.AddSingleton<IRoomService, DemoRoomService>();
else
    builder.Services.AddSingleton<IRoomService, GraphRoomService>();

builder.Services.AddControllersWithViews()
    .ConfigureApiBehaviorOptions(o => o.SuppressModelStateInvalidFilter = true); // respondemos con nuestro JSON

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    o.Cookie.Name = "sala.af";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(365));

var app = builder.Build();

// En IIS la subcarpeta (/salareservas) la aporta el módulo ASP.NET Core automáticamente.
// "PathBase" solo hace falta para ejecutar fuera de IIS (p. ej. `dotnet run` con la misma URL).
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrWhiteSpace(pathBase)) app.UsePathBase(pathBase);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(a => a.Run(async c =>
    {
        c.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await c.Response.WriteAsync("Error interno.");
    }));
    app.UseHsts();
}

// HTTPS obligatorio (la redirección de UseHttpsRedirection no detecta el puerto en IIS in-process).
if (app.Configuration.GetValue("RequireHttps", !app.Environment.IsDevelopment()))
{
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.IsHttps)
        {
            ctx.Response.Redirect($"https://{ctx.Request.Host}{ctx.Request.PathBase}{ctx.Request.Path}{ctx.Request.QueryString}", permanent: false);
            return;
        }
        await next();
    });
}

// Clave de acceso de la tablet (opcional pero recomendada).
// Se abre una vez  https://servidor/salareservas/?key=CLAVE  y queda guardada en una cookie segura de 1 año.
var kioskKey = app.Configuration["Kiosk:AccessKey"];
if (!string.IsNullOrEmpty(kioskKey))
{
    var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kioskKey)));
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Query.TryGetValue("key", out var k) && k.ToString() == kioskKey)
        {
            ctx.Response.Cookies.Append("sala.kiosk", expected, new CookieOptions
            {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = ctx.Request.PathBase.HasValue ? ctx.Request.PathBase.Value : "/",
                Expires = DateTimeOffset.UtcNow.AddYears(1),
            });
            ctx.Response.Redirect(ctx.Request.PathBase + ctx.Request.Path);
            return;
        }
        var cookie = ctx.Request.Cookies["sala.kiosk"] ?? "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(expected)))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Acceso no autorizado.");
            return;
        }
        await next();
    });
}

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Fuentes y librerías casi nunca cambian; css/js llevan versión en la URL (asp-append-version).
        var p = ctx.Context.Request.Path.Value ?? "";
        if (p.Contains("/fonts/") || p.Contains("/lib/") || p.Contains("/img/"))
            ctx.Context.Response.Headers.CacheControl = "public,max-age=604800";
    }
});
app.UseRouting();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
