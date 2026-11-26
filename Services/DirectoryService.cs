using System.Globalization;
using System.Text;
using Azure.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using SalaReservas.Models;

namespace SalaReservas.Services;

/// <summary>
/// Directorio de personas de la empresa para elegir quién reserva la sala.
/// Con Microsoft 365 se consulta Entra ID (Active Directory) mediante Microsoft Graph.
/// </summary>
public interface IDirectoryService
{
    /// <summary>true si la tablet debe pedir el nombre desde el directorio.</summary>
    bool Enabled { get; }
    /// <summary>Busca personas por nombre o apellido (mín. 2 letras).</summary>
    Task<IReadOnlyList<PersonDto>> SearchAsync(string query, CancellationToken ct);
    /// <summary>Comprueba que un correo pertenece al directorio y devuelve su ficha.</summary>
    Task<PersonDto?> FindByEmailAsync(string email, CancellationToken ct);
}

internal static class DirectoryText
{
    /// <summary>Minúsculas y sin tildes: «Íñigo» → «inigo».</summary>
    public static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>Palabras buscadas, solo con letras, números, punto y guion (seguras para la consulta a Graph).</summary>
    public static string[] Tokens(string q) =>
        new string(q.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(4).ToArray();

    /// <summary>Cada palabra buscada debe ser el principio de alguna palabra del nombre (o del correo).</summary>
    public static bool Matches(PersonDto p, string[] tokens)
    {
        var words = Fold(p.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var mail = Fold(p.Email);
        return tokens.All(t => { var f = Fold(t); return words.Any(w => w.StartsWith(f)) || mail.StartsWith(f); });
    }

    /// <summary>Primero quien empieza por lo buscado; después, por orden alfabético.</summary>
    public static IEnumerable<PersonDto> Rank(IEnumerable<PersonDto> people, string[] tokens)
    {
        var first = tokens.Length > 0 ? Fold(tokens[0]) : "";
        return people.OrderBy(p => Fold(p.Name).StartsWith(first) ? 0 : 1)
                     .ThenBy(p => p.Name, StringComparer.Create(new CultureInfo("es-ES"), true));
    }
}

/// <summary>Modo demo: directorio de ejemplo con nombres ficticios.</summary>
public class DemoDirectoryService : IDirectoryService
{
    private readonly int _max;
    private static readonly PersonDto[] People = new (string Name, string Dept)[]
    {
        ("Ana López Martín", "Arquitectura"), ("Andrés Navarro Gil", "Ingeniería"), ("Alicia Romero Díaz", "Interiorismo"),
        ("Antonio Serrano Vidal", "Dirección"), ("Beatriz Castro Moreno", "Administración"), ("Carlos Ortega Ruiz", "BIM"),
        ("Carmen Delgado Sáez", "Urbanismo"), ("Daniel Molina Prieto", "Arquitectura"), ("Elena Vázquez Iglesias", "Landscape"),
        ("Fernando Rubio Cano", "Ingeniería"), ("Irene Herrera Peña", "Interiorismo"), ("Íñigo Vega Lozano", "Landscape"),
        ("Javier Muñoz Garrido", "Arquitectura"), ("Laura Gómez Fuentes", "Arquitectura"), ("Lucía Marín Cabrera", "RR. HH."),
        ("Manuel Santos Reyes", "Obra"), ("Marta Sanz Herrero", "Dirección de proyectos"), ("Miguel Ángel Torres León", "BIM"),
        ("Nuria Calvo Pascual", "Comunicación"), ("Pablo Ruiz Medina", "Ingeniería"), ("Paula Ibáñez Soler", "Urbanismo"),
        ("Raquel Domínguez Mora", "Administración"), ("Sergio Alonso Crespo", "Sistemas"), ("Sofía Jiménez Ramos", "Arquitectura"),
    }.Select(p =>
    {
        var w = DirectoryText.Fold(p.Name).Split(' ');
        return new PersonDto(p.Name, $"{w[0]}.{w[^2]}@hcparquitectos.com", p.Dept);
    }).ToArray();

    public DemoDirectoryService(IOptions<DirectoryOptions> o) => _max = Math.Clamp(o.Value.MaxResults, 1, 20);

    public bool Enabled => true;

    public Task<IReadOnlyList<PersonDto>> SearchAsync(string query, CancellationToken ct)
    {
        var t = DirectoryText.Tokens(query);
        IReadOnlyList<PersonDto> r = t.Length == 0 ? Array.Empty<PersonDto>()
            : DirectoryText.Rank(People.Where(p => DirectoryText.Matches(p, t)), t).Take(_max).ToList();
        return Task.FromResult(r);
    }

    public Task<PersonDto?> FindByEmailAsync(string email, CancellationToken ct) =>
        Task.FromResult(People.FirstOrDefault(p => string.Equals(p.Email, email.Trim(), StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// Directorio real: usuarios de Microsoft Entra ID (Active Directory) mediante Microsoft Graph.
/// Permiso de aplicación necesario (con consentimiento de administrador):
///   · User.ReadBasic.All → nombre y correo.
///   · User.Read.All      → además departamento, y oculta cuentas deshabilitadas e invitados (Directory:FullProfile=true).
/// Los resultados se guardan 5 minutos en memoria para no consultar Graph en cada letra.
/// </summary>
public class GraphDirectoryService : IDirectoryService
{
    private readonly DirectoryOptions _opt;
    private readonly RoomOptions _room;
    private readonly GraphServiceClient? _graph;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GraphDirectoryService> _log;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    public GraphDirectoryService(IOptions<DirectoryOptions> dir, IOptions<GraphOptions> g, IOptions<RoomOptions> room,
                                 IMemoryCache cache, ILogger<GraphDirectoryService> log)
    {
        _opt = dir.Value; _room = room.Value; _cache = cache; _log = log;
        var o = g.Value;
        static bool Missing(string? s) => string.IsNullOrWhiteSpace(s) || s.StartsWith("PON-AQUI", StringComparison.OrdinalIgnoreCase);
        if (_opt.Enabled && !Missing(o.TenantId) && !Missing(o.ClientId) && !Missing(o.ClientSecret))
            _graph = new GraphServiceClient(new ClientSecretCredential(o.TenantId, o.ClientId, o.ClientSecret),
                                            new[] { "https://graph.microsoft.com/.default" });
        else if (_opt.Enabled)
            log.LogWarning("Directory:Enabled=true pero falta configurar Microsoft Graph: el buscador de personas queda desactivado.");
    }

    public bool Enabled => _graph != null;

    private string[] Select => _opt.FullProfile
        ? new[] { "displayName", "mail", "userPrincipalName", "department", "jobTitle" }
        : new[] { "displayName", "mail", "userPrincipalName" };

    private PersonDto? Map(Microsoft.Graph.Models.User u)
    {
        var mail = u.Mail;
        if (string.IsNullOrWhiteSpace(mail) || string.IsNullOrWhiteSpace(u.DisplayName)) return null;
        if (!string.IsNullOrWhiteSpace(_opt.EmailDomain) &&
            !mail.EndsWith("@" + _opt.EmailDomain.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(mail, _room.Mailbox, StringComparison.OrdinalIgnoreCase)) return null; // el buzón de la propia sala
        var dept = _opt.FullProfile ? (u.Department ?? u.JobTitle) : null;
        return new PersonDto(u.DisplayName!, mail, string.IsNullOrWhiteSpace(dept) ? null : dept);
    }

    public async Task<IReadOnlyList<PersonDto>> SearchAsync(string query, CancellationToken ct)
    {
        var tokens = DirectoryText.Tokens(query);
        if (_graph is null || tokens.Length == 0) return Array.Empty<PersonDto>();
        var key = "dir:q:" + string.Join(' ', tokens.Select(DirectoryText.Fold));
        if (_cache.TryGetValue(key, out IReadOnlyList<PersonDto>? hit) && hit != null) return hit;

        // $search busca palabras que EMPIEZAN por lo escrito: "displayName:ana" AND "displayName:lop"
        var search = string.Join(" AND ", tokens.Select(t => $"\"displayName:{t}\""));
        var res = await _graph.Users.GetAsync(rc =>
        {
            rc.QueryParameters.Search = search;
            rc.QueryParameters.Select = Select;
            rc.QueryParameters.Top = 25;
            rc.QueryParameters.Count = true;
            if (_opt.FullProfile) rc.QueryParameters.Filter = "accountEnabled eq true and userType eq 'Member'";
            rc.Headers.Add("ConsistencyLevel", "eventual");
        }, ct);

        var people = (res?.Value ?? new()).Select(Map).OfType<PersonDto>()
            .Where(p => DirectoryText.Matches(p, tokens));   // también acepta búsquedas sin tildes
        IReadOnlyList<PersonDto> list = DirectoryText.Rank(people, tokens).Take(Math.Clamp(_opt.MaxResults, 1, 20)).ToList();
        _cache.Set(key, list, CacheFor);
        return list;
    }

    public async Task<PersonDto?> FindByEmailAsync(string email, CancellationToken ct)
    {
        email = email.Trim();
        if (_graph is null || email.Length == 0 || email.Contains('\'')) return null;
        var key = "dir:e:" + email.ToLowerInvariant();
        if (_cache.TryGetValue(key, out PersonDto? hit)) return hit;
        try
        {
            var res = await _graph.Users.GetAsync(rc =>
            {
                rc.QueryParameters.Filter = $"mail eq '{email}' or userPrincipalName eq '{email}'";
                rc.QueryParameters.Select = Select;
                rc.QueryParameters.Top = 1;
            }, ct);
            var p = res?.Value?.Select(Map).OfType<PersonDto>().FirstOrDefault();
            _cache.Set(key, p, CacheFor);
            return p;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error comprobando {Email} en el directorio", email);
            throw;
        }
    }
}
