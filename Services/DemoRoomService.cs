using Microsoft.Extensions.Options;
using SalaReservas.Models;

namespace SalaReservas.Services;

/// <summary>
/// Modo demo (Room:DemoMode=true): reuniones de ejemplo en memoria, sin Microsoft 365.
/// Sirve para probar el diseño y la tablet antes de registrar la aplicación en Entra ID.
/// </summary>
public class DemoRoomService : RoomServiceBase
{
    private readonly List<RawEvent> _events = new();
    private readonly object _gate = new();

    public DemoRoomService(IOptions<RoomOptions> r, ILogger<DemoRoomService> log) : base(r.Value, log)
    {
        var now = DateTime.UtcNow;
        var q = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute / 15 * 15, 0, DateTimeKind.Utc);
        void Add(string subject, string who, DateTime from, int minutes) =>
            _events.Add(new RawEvent(Guid.NewGuid().ToString(), subject, who, from, from.AddMinutes(minutes), true, false));

        Add("Revisión de proyecto Arquitectura", "Laura Gómez", q.AddMinutes(-15), 60);   // en curso
        Add("Coordinación Ingeniería", "Pablo Ruiz", q.AddMinutes(75), 45);
        Add("Presentación al cliente", "Marta Sanz", q.AddMinutes(150), 90);
        Add("Seguimiento Landscape", "Íñigo Vega", q.AddMinutes(285), 60);
        Add("Comité de dirección", "Dirección", q.AddDays(1).AddHours(-1), 120);
        Add("Entrevista candidatos", "RR. HH.", q.AddDays(1).AddHours(3), 60);
    }

    protected override Task<List<RawEvent>> QueryEventsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_events.Where(e => e.StartUtc < toUtc && e.EndUtc > fromUtc).ToList());
    }

    protected override Task<RawEvent> CreateEventAsync(string title, string organizer, string? organizerEmail, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var ev = new RawEvent(Guid.NewGuid().ToString(), title, organizer, startUtc, endUtc, Room.CreateTeamsMeeting, false);
        lock (_gate) _events.Add(ev);
        return Task.FromResult(ev);
    }
}
