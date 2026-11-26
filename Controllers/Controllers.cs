using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SalaReservas.Models;
using SalaReservas.Services;

namespace SalaReservas.Controllers;

public class HomeController : Controller
{
    private readonly RoomOptions _room;
    public HomeController(IOptions<RoomOptions> room) => _room = room.Value;

    private static int ToMin(string hhmm, int fallback) =>
        TimeSpan.TryParse(hhmm, out var t) ? (int)t.TotalMinutes : fallback;

    public IActionResult Index()
    {
        ViewBag.RoomName = _room.DisplayName;
        // Configuración para el cliente. Url.Content("~/") incluye la subcarpeta (/salareservas/).
        ViewBag.Cfg = JsonSerializer.Serialize(new
        {
            @base = Url.Content("~/"),
            min = _room.MinMinutes,
            max = _room.MaxMinutes,
            slot = Math.Max(5, _room.SlotMinutes),
            workStart = ToMin(_room.WorkdayStart, 420),
            workEnd = ToMin(_room.WorkdayEnd, 1260),
            maxDays = _room.MaxDaysAhead,
            refreshMs = 30_000,
            idleMs = 90_000,
        });
        return View();
    }
}

[ApiController]
[Route("api")]
public class ApiController : ControllerBase
{
    private readonly IRoomService _rooms;
    private readonly IAntiforgery _antiforgery;

    public ApiController(IRoomService rooms, IAntiforgery antiforgery)
    {
        _rooms = rooms;
        _antiforgery = antiforgery;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string? date, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await _rooms.GetStatusAsync(date, ct));
    }

    /// <summary>Token antiforgery nuevo antes de cada reserva (sobrevive a reinicios del servidor).</summary>
    [HttpGet("token")]
    public IActionResult Token()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { token = _antiforgery.GetAndStoreTokens(HttpContext).RequestToken });
    }

    [HttpPost("book")]
    public async Task<IActionResult> Book([FromBody] BookingRequest req, CancellationToken ct)
    {
        try { await _antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException)
        {
            return BadRequest(new BookingResult(false, "Sesión caducada. Vuelve a intentarlo."));
        }
        if (!ModelState.IsValid)
            return BadRequest(new BookingResult(false, "Datos no válidos: indica el título y tu nombre (mínimo 2 caracteres cada uno)."));

        var result = await _rooms.BookAsync(req, ct);
        return result.Success ? Ok(result) : Conflict(result);
    }
}
