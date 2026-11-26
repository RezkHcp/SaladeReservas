using System.ComponentModel.DataAnnotations;

namespace SalaReservas.Models;

public class GraphOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public class RoomOptions
{
    public string Mailbox { get; set; } = "";
    public string DisplayName { get; set; } = "Sala de Reuniones";
    /// <summary>Zona horaria de la sala (nombre de Windows, p.ej. "Romance Standard Time" = Madrid).</summary>
    public string TimeZone { get; set; } = "Romance Standard Time";
    public int MinMinutes { get; set; } = 15;
    public int MaxMinutes { get; set; } = 240;
    public int SlotMinutes { get; set; } = 15;
    public string WorkdayStart { get; set; } = "07:00";
    public string WorkdayEnd { get; set; } = "21:00";
    /// <summary>Hasta cuántos días por delante se puede consultar y reservar.</summary>
    public int MaxDaysAhead { get; set; } = 90;
    public bool CreateTeamsMeeting { get; set; }
    /// <summary>Modo demo: datos de ejemplo en memoria, sin conectar con Microsoft 365.</summary>
    public bool DemoMode { get; set; }
}

/// <summary>
/// Reunión lista para pintar. Todas las horas van ya en la zona horaria de la sala,
/// así la tablet no depende de su propia zona horaria ni de su reloj.
/// StartMin/EndMin = minutos desde las 00:00 del día de referencia (0–1440).
/// </summary>
public record MeetingDto(
    string Id, string Subject, string Organizer,
    string StartLocal, string EndLocal,
    int StartMin, int EndMin,
    bool IsTeams, bool IsAllDay);

public class RoomStatusDto
{
    public string RoomName { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Instante actual del servidor (ms Unix, UTC).</summary>
    public long NowMs { get; set; }
    /// <summary>Desfase UTC de la zona de la sala ahora mismo (minutos).</summary>
    public int UtcOffsetMinutes { get; set; }
    public string Today { get; set; } = "";
    /// <summary>Día mostrado (yyyy-MM-dd).</summary>
    public string Date { get; set; } = "";
    public bool IsToday { get; set; }
    public bool IsBusy { get; set; }
    public MeetingDto? Current { get; set; }
    public MeetingDto? Next { get; set; }
    /// <summary>Si está libre: hora (HH:mm) de la próxima reunión de hoy; null = libre el resto del día.</summary>
    public string? FreeUntil { get; set; }
    /// <summary>Todas las reuniones del día mostrado, ordenadas.</summary>
    public List<MeetingDto> Meetings { get; set; } = new();
    public string? Error { get; set; }
}

public class BookingRequest
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string Title { get; set; } = "";

    [StringLength(80)]
    public string? Organizer { get; set; }

    /// <summary>Día en hora de la sala, yyyy-MM-dd.</summary>
    [Required]
    public string Date { get; set; } = "";

    /// <summary>Hora de inicio HH:mm (hora de la sala) o "now" para empezar ahora.</summary>
    public string? Start { get; set; }

    [Range(5, 1440)]
    public int Minutes { get; set; }
}

public record BookingResult(bool Success, string Message, MeetingDto? Meeting = null);
