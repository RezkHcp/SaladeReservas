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
    private readonly IDirectoryService _dir;
    public HomeController(IOptions<RoomOptions> room, IDirectoryService dir) { _room = room.Value; _dir = dir; }

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
            directory = _dir.Enabled,   // buscador de personas del directorio
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
    private readonly IDirectoryService _dir;
    private readonly ILogger<ApiController> _log;

    public ApiController(IRoomService rooms, IAntiforgery antiforgery, IDirectoryService dir, ILogger<ApiController> log)
    {
        _rooms = rooms;
        _antiforgery = antiforgery;
        _dir = dir;
        _log = log;
    }

    /// <summary>Busca personas en el directorio de la empresa (Active Directory). Protegido por la clave de la tablet.</summary>
    [HttpGet("people")]
    public async Task<IActionResult> People([FromQuery] string? q, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!_dir.Enabled) return NotFound();
        q = (q ?? "").Trim();
        if (q.Length < 2 || q.Length > 60) return Ok(Array.Empty<PersonDto>());
        try { return Ok(await _dir.SearchAsync(q, ct)); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error buscando en el directorio");
            return StatusCode(StatusCodes.Status502BadGateway, Array.Empty<PersonDto>());
        }
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

        // Con directorio: la persona debe existir en Active Directory; se usa su nombre oficial.
        if (_dir.Enabled)
        {
            PersonDto? person;
            try { person = string.IsNullOrWhiteSpace(req.OrganizerEmail) ? null : await _dir.FindByEmailAsync(req.OrganizerEmail, ct); }
            catch { return StatusCode(StatusCodes.Status502BadGateway, new BookingResult(false, "No se puede consultar el directorio. Inténtalo de nuevo.")); }
            if (person is null) return BadRequest(new BookingResult(false, "Elige tu nombre de la lista del directorio."));
            req.Organizer = person.Name;
            req.OrganizerEmail = person.Email;
        }
        else req.OrganizerEmail = null;

        var result = await _rooms.BookAsync(req, ct);
        return result.Success ? Ok(result) : Conflict(result);
    }
}
