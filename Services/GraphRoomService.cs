using System.Globalization;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using SalaReservas.Models;

namespace SalaReservas.Services;

/// <summary>Calendario real de Exchange Online vía Microsoft Graph (Client Credentials).</summary>
public class GraphRoomService : RoomServiceBase
{
    private readonly GraphServiceClient? _graph;
    private readonly string? _configError;
    private readonly DirectoryOptions _dir;

    /// <summary>
    /// Propiedad propia del evento con el nombre de quien reservó desde la tablet. Los eventos creados por la app
    /// tienen como organizador el buzón de la sala; así la agenda muestra a la persona real.
    /// </summary>
    private const string ReservedByProp = "String {8c3a6f2e-5b1d-4a7e-9f3c-2d4b6a8e1f07} Name SalaReservas.ReservadoPor";

    public GraphRoomService(IOptions<GraphOptions> g, IOptions<RoomOptions> r, IOptions<DirectoryOptions> dir, ILogger<GraphRoomService> log)
        : base(r.Value, log)
    {
        _dir = dir.Value;
        var o = g.Value;
        static bool Missing(string? s) => string.IsNullOrWhiteSpace(s) || s.StartsWith("PON-AQUI", StringComparison.OrdinalIgnoreCase);

        if (Missing(o.TenantId) || Missing(o.ClientId) || Missing(o.ClientSecret))
            _configError = "Falta configurar Microsoft Graph (TenantId, ClientId y ClientSecret).";
        else if (string.IsNullOrWhiteSpace(Room.Mailbox) || Room.Mailbox.Contains("tudominio", StringComparison.OrdinalIgnoreCase))
            _configError = "Falta configurar el buzón de la sala (Room:Mailbox).";
        else
        {
            var cred = new ClientSecretCredential(o.TenantId, o.ClientId, o.ClientSecret);
            _graph = new GraphServiceClient(cred, new[] { "https://graph.microsoft.com/.default" });
        }
        if (_configError != null) log.LogWarning("{Msg}", _configError);
    }

    protected override string? ConfigError => _configError;

    protected override string FriendlyError(Exception ex) => ex switch
    {
        ODataError { ResponseStatusCode: 401 or 403 } => "Sin permisos sobre el calendario de la sala (403).",
        ODataError { ResponseStatusCode: 404 } => "No se encuentra el buzón de la sala (404).",
        _ => "No se puede conectar con el calendario. Reintentando…"
    };

    protected override async Task<List<RawEvent>> QueryEventsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var result = await _graph!.Users[Room.Mailbox].CalendarView.GetAsync(rc =>
        {
            rc.QueryParameters.StartDateTime = fromUtc.ToString("yyyy-MM-ddTHH:mm:ss.0000000", CultureInfo.InvariantCulture);
            rc.QueryParameters.EndDateTime = toUtc.ToString("yyyy-MM-ddTHH:mm:ss.0000000", CultureInfo.InvariantCulture);
            rc.QueryParameters.Orderby = new[] { "start/dateTime" };
            rc.QueryParameters.Select = new[] { "id", "subject", "organizer", "start", "end", "isCancelled", "isAllDay", "showAs", "isOnlineMeeting" };
            rc.QueryParameters.Top = 100;
            rc.QueryParameters.Expand = new[] { $"singleValueExtendedProperties($filter=id eq '{ReservedByProp}')" };
            // Horas en la zona de la sala: los eventos de día completo salen de 00:00 a 00:00 locales.
            rc.Headers.Add("Prefer", $"outlook.timezone=\"{Room.TimeZone}\"");
        }, ct);

        var list = new List<RawEvent>();
        foreach (var e in result?.Value ?? new List<Event>())
        {
            if (e.IsCancelled == true || e.ShowAs == FreeBusyStatus.Free) continue;
            if (e.Start?.DateTime is null || e.End?.DateTime is null) continue;
            list.Add(new RawEvent(
                e.Id ?? Guid.NewGuid().ToString(),
                string.IsNullOrWhiteSpace(e.Subject) ? "(Sin título)" : e.Subject!,
                ReservedBy(e) ?? e.Organizer?.EmailAddress?.Name ?? "",
                LocalStringToUtc(e.Start.DateTime),
                LocalStringToUtc(e.End.DateTime),
                e.IsOnlineMeeting == true,
                e.IsAllDay == true));
        }
        return list;
    }

    private static string? ReservedBy(Event e) =>
        e.SingleValueExtendedProperties?.FirstOrDefault(p => string.Equals(p.Id, ReservedByProp, StringComparison.OrdinalIgnoreCase))?.Value is { Length: > 0 } v ? v : null;

    private DateTime LocalStringToUtc(string s) =>
        LocalToUtc(DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.None));

    protected override async Task<RawEvent> CreateEventAsync(string title, string organizer, string? organizerEmail, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var who = string.IsNullOrWhiteSpace(organizerEmail) ? organizer : $"{organizer} <{organizerEmail}>";
        var ev = new Event
        {
            Subject = title,
            Body = new ItemBody { ContentType = BodyType.Text, Content = $"Reservado desde la tablet de la sala por: {who}" },
            SingleValueExtendedProperties = new List<SingleValueLegacyExtendedProperty>
            {
                new() { Id = ReservedByProp, Value = organizer },
            },
            Start = new DateTimeTimeZone { DateTime = startUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), TimeZone = "UTC" },
            End = new DateTimeTimeZone { DateTime = endUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), TimeZone = "UTC" },
            ShowAs = FreeBusyStatus.Busy,
            Location = new Location { DisplayName = Room.DisplayName },
        };
        // Opcional: quien reserva recibe la invitación en su Outlook (Directory:AddAsAttendee).
        if (_dir.AddAsAttendee && !string.IsNullOrWhiteSpace(organizerEmail))
        {
            ev.Attendees = new List<Attendee>
            {
                new() { Type = AttendeeType.Required, EmailAddress = new EmailAddress { Address = organizerEmail, Name = organizer } },
            };
        }
        if (Room.CreateTeamsMeeting)
        {
            ev.IsOnlineMeeting = true;
            ev.OnlineMeetingProvider = OnlineMeetingProviderType.TeamsForBusiness;
        }

        var created = await _graph!.Users[Room.Mailbox].Events.PostAsync(ev, cancellationToken: ct);
        return new RawEvent(created?.Id ?? Guid.NewGuid().ToString(), title, organizer, startUtc, endUtc, Room.CreateTeamsMeeting, false);
    }
}
