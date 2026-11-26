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
/// Directorio de la empresa (Active Directory / Microsoft Entra ID) para elegir quién reserva.
/// </summary>
public class DirectoryOptions
{
    /// <summary>Activa el buscador de personas. Requiere el permiso de Graph User.ReadBasic.All (o User.Read.All).</summary>
    public bool Enabled { get; set; }
    /// <summary>true = permiso User.Read.All: muestra el departamento y oculta cuentas deshabilitadas e invitados.</summary>
    public bool FullProfile { get; set; }
    /// <summary>Solo personas con correo de este dominio (vacío = cualquiera).</summary>
    public string EmailDomain { get; set; } = "";
    /// <summary>Añade a quien reserva como asistente: recibe la invitación en su Outlook.</summary>
    public bool AddAsAttendee { get; set; }
    /// <summary>Máximo de resultados en la lista.</summary>
    public int MaxResults { get; set; } = 6;
}

/// <summary>Persona del directorio tal como la ve la tablet.</summary>
public record PersonDto(string Name, string Email, string? Dept);

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

    /// <summary>Nombre de quien reserva (obligatorio).</summary>
    [Required, StringLength(80, MinimumLength = 2)]
    public string Organizer { get; set; } = "";

    /// <summary>Correo de quien reserva, elegido del directorio (obligatorio si el directorio está activo).</summary>
    [EmailAddress, StringLength(254)]
    public string? OrganizerEmail { get; set; }

    /// <summary>Día en hora de la sala, yyyy-MM-dd.</summary>
    [Required]
    public string Date { get; set; } = "";

    /// <summary>Hora de inicio HH:mm (hora de la sala) o "now" para empezar ahora.</summary>
    public string? Start { get; set; }

    [Range(5, 1440)]
    public int Minutes { get; set; }
}

public record BookingResult(bool Success, string Message, MeetingDto? Meeting = null);
