using System.Globalization;
using Microsoft.Extensions.Options;
using SalaReservas.Models;

namespace SalaReservas.Services;

public interface IRoomService
{
    Task<RoomStatusDto> GetStatusAsync(string? date, CancellationToken ct);
    Task<BookingResult> BookAsync(BookingRequest req, CancellationToken ct);
}

public record RawEvent(string Id, string Subject, string Organizer, DateTime StartUtc, DateTime EndUtc, bool IsTeams, bool IsAllDay);

/// <summary>
/// Lógica común: zona horaria, validaciones, estado y bloqueo anti-duplicados.
/// Las subclases solo saben consultar y crear eventos (Graph o memoria).
/// </summary>
public abstract class RoomServiceBase : IRoomService
{
    // Un único proceso IIS (maxProcesses=1): este semáforo serializa las reservas y
    // la comprobación de solape se repite DENTRO del bloqueo, contra el calendario real.
    private static readonly SemaphoreSlim BookingLock = new(1, 1);

    private static readonly string AppVersion = ComputeVersion();

    protected readonly RoomOptions Room;
    protected readonly TimeZoneInfo Tz;
    protected readonly ILogger Log;

    protected RoomServiceBase(RoomOptions room, ILogger log)
    {
        Room = room;
        Log = log;
        Tz = FindTz(room.TimeZone);
    }

    protected abstract Task<List<RawEvent>> QueryEventsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    protected abstract Task<RawEvent> CreateEventAsync(string title, string organizer, DateTime startUtc, DateTime endUtc, CancellationToken ct);
    /// <summary>Mensaje si falta configuración; null si todo está listo.</summary>
    protected virtual string? ConfigError => null;
    protected virtual string FriendlyError(Exception ex) => "No se puede leer el calendario. Reintentando…";

    // ---------- utilidades de tiempo ----------
    private static string ComputeVersion()
    {
        try { return File.GetLastWriteTimeUtc(typeof(RoomServiceBase).Assembly.Location).Ticks.ToString("x"); }
        catch { return "0"; }
    }

    private static TimeZoneInfo FindTz(string id)
    {
        foreach (var candidate in new[] { id, "Romance Standard Time", "Europe/Madrid" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); } catch { }
        }
        return TimeZoneInfo.Utc;
    }

    protected DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz);

    protected DateTime LocalToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (Tz.IsInvalidTime(local)) local = local.AddHours(1); // hueco del cambio de hora
        return TimeZoneInfo.ConvertTimeToUtc(local, Tz);
    }

    private (DateTime FromUtc, DateTime ToUtc) DayRange(DateOnly day) =>
        (LocalToUtc(day.ToDateTime(TimeOnly.MinValue)), LocalToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue)));

    private static string Hm(DateTime local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);
    private static string Ymd(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private MeetingDto ToDto(RawEvent e, DateOnly refDay)
    {
        var ls = ToLocal(e.StartUtc);
        var le = ToLocal(e.EndUtc);
        var origin = refDay.ToDateTime(TimeOnly.MinValue);
        int Min(DateTime l) => (int)Math.Clamp((l - origin).TotalMinutes, 0, 1440);
        return new MeetingDto(e.Id, e.Subject, e.Organizer, Hm(ls), Hm(le), Min(ls), Min(le), e.IsTeams, e.IsAllDay);
    }

    private static bool TryParseDay(string? s, out DateOnly d) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    // ---------- estado ----------
    public async Task<RoomStatusDto> GetStatusAsync(string? date, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var localNow = ToLocal(nowUtc);
        var today = DateOnly.FromDateTime(localNow);
        var selected = TryParseDay(date, out var d) && d >= today && d <= today.AddDays(Room.MaxDaysAhead) ? d : today;

        var status = new RoomStatusDto
        {
            RoomName = Room.DisplayName,
            Version = AppVersion,
            NowMs = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            UtcOffsetMinutes = (int)Tz.GetUtcOffset(nowUtc).TotalMinutes,
            Today = Ymd(today),
            Date = Ymd(selected),
            IsToday = selected == today,
        };

        if (ConfigError is { } cfg) { status.Error = cfg; return status; }

        try
        {
            var (tFrom, tTo) = DayRange(today);
            var todayEvents = (await QueryEventsAsync(tFrom, tTo, ct)).OrderBy(e => e.StartUtc).ToList();

            var current = todayEvents.FirstOrDefault(e => e.StartUtc <= nowUtc && e.EndUtc > nowUtc);
            var next = todayEvents.FirstOrDefault(e => e.StartUtc > nowUtc);
            status.IsBusy = current != null;
            status.Current = current is null ? null : ToDto(current, today);
            status.Next = next is null ? null : ToDto(next, today);
            if (!status.IsBusy) status.FreeUntil = next is null ? null : Hm(ToLocal(next.StartUtc));

            List<RawEvent> dayEvents;
            if (selected == today) dayEvents = todayEvents;
            else
            {
                var (sFrom, sTo) = DayRange(selected);
                dayEvents = (await QueryEventsAsync(sFrom, sTo, ct)).OrderBy(e => e.StartUtc).ToList();
            }
            status.Meetings = dayEvents.Select(e => ToDto(e, selected)).ToList();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Error consultando el calendario");
            status.Error = FriendlyError(ex);
        }
        return status;
    }

    // ---------- reserva ----------
    public async Task<BookingResult> BookAsync(BookingRequest req, CancellationToken ct)
    {
        if (ConfigError is { } cfg) return new(false, cfg);

        var nowUtc = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(ToLocal(nowUtc));

        if (!TryParseDay(req.Date, out var day)) return new(false, "Fecha no válida.");
        if (day < today) return new(false, "No se puede reservar en una fecha pasada.");
        if (day > today.AddDays(Room.MaxDaysAhead))
            return new(false, $"Solo se puede reservar con un máximo de {Room.MaxDaysAhead} días de antelación.");

        if (req.Minutes < Room.MinMinutes || req.Minutes > Room.MaxMinutes)
            return new(false, $"La duración debe estar entre {Room.MinMinutes} y {Room.MaxMinutes} minutos.");

        DateTime startUtc;
        if (string.IsNullOrWhiteSpace(req.Start) || req.Start == "now")
        {
            if (day != today) return new(false, "«Ahora» solo se puede usar en el día de hoy.");
            startUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
        }
        else
        {
            if (!TimeSpan.TryParseExact(req.Start, @"hh\:mm", CultureInfo.InvariantCulture, out var t))
                return new(false, "Hora de inicio no válida.");
            startUtc = LocalToUtc(day.ToDateTime(TimeOnly.MinValue) + t);
        }
        if (startUtc < nowUtc.AddMinutes(-1)) return new(false, "La hora de inicio ya ha pasado.");
        var endUtc = startUtc.AddMinutes(req.Minutes);

        // Horario laborable (hora de la sala): mismo día y dentro de la franja.
        var ls = ToLocal(startUtc);
        var le = ToLocal(endUtc);
        if (TimeSpan.TryParse(Room.WorkdayStart, CultureInfo.InvariantCulture, out var ws) &&
            TimeSpan.TryParse(Room.WorkdayEnd, CultureInfo.InvariantCulture, out var we) &&
            (ls.TimeOfDay < ws || le.Date != ls.Date || le.TimeOfDay > we))
            return new(false, $"Solo se puede reservar entre las {Room.WorkdayStart} y las {Room.WorkdayEnd}.");

        var title = new string(req.Title.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (title.Length < 2) return new(false, "Escribe un título para la reunión.");
        var organizer = new string((req.Organizer ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (organizer.Length < 2) return new(false, "Escribe tu nombre para hacer la reserva.");

        await BookingLock.WaitAsync(ct);
        try
        {
            // Comprobación de disponibilidad dentro del bloqueo y contra el calendario real.
            var clash = (await QueryEventsAsync(startUtc, endUtc, ct))
                .Where(m => m.StartUtc < endUtc && m.EndUtc > startUtc)
                .OrderBy(m => m.StartUtc).FirstOrDefault();
            if (clash != null)
                return new(false, $"La sala ya está reservada de {Hm(ToLocal(clash.StartUtc))} a {Hm(ToLocal(clash.EndUtc))}.");

            var created = await CreateEventAsync(title, organizer, startUtc, endUtc, ct);
            Log.LogInformation("Reserva creada: {Title} {Start:u}–{End:u} por {Org}", title, startUtc, endUtc, organizer);
            return new(true, "Reserva confirmada.", ToDto(created, day));
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Error creando el evento");
            return new(false, "No se pudo crear la reserva. Inténtalo de nuevo.");
        }
        finally
        {
            BookingLock.Release();
        }
    }
}
