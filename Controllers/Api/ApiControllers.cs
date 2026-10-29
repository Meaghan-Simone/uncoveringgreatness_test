using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers.Api;

/// <summary>
/// JSON API. Authenticated with "Authorization: Bearer &lt;token&gt;" only (never cookies), which makes it immune to CSRF.
/// Obtain a token from POST /api/v1/auth/token. All authorisation rules live in the shared services, so the API and the
/// web UI enforce exactly the same permissions.
/// </summary>
[ApiController, IgnoreAntiforgeryToken]
[Authorize(AuthenticationSchemes = AuthSchemes.Bearer)]
public abstract class ApiBase : ControllerBase
{
    protected IActionResult Problem(Result r) => r.Status switch
    {
        ResultStatus.NotFound => NotFound(new { error = r.Error }),
        ResultStatus.Forbidden => StatusCode(403, new { error = r.Error }),
        ResultStatus.Conflict => Conflict(new { error = r.Error, errors = r.Errors }),
        _ => BadRequest(new { error = r.Error, errors = r.Errors })
    };

    protected IActionResult From<T>(Result<T> r) => r.Succeeded ? Ok(r.Value) : Problem(r);
    protected IActionResult From(Result r) => r.Succeeded ? NoContent() : Problem(r);
    protected IActionResult Page<T>(PagedResult<T> p)
    {
        Response.Headers["X-Total-Count"] = p.TotalCount.ToString();
        return Ok(new { items = p.Items, page = p.Page, pageSize = p.PageSize, totalCount = p.TotalCount, totalPages = p.TotalPages });
    }
}

[Route("api/v1/auth")]
public class AuthApiController : ApiBase
{
    private readonly AuthService _auth;
    private readonly ApiTokenService _tokens;
    private readonly ICurrentUser _me;
    public AuthApiController(AuthService auth, ApiTokenService tokens, ICurrentUser me) { _auth = auth; _tokens = tokens; _me = me; }

    public record TokenRequest(string Email, string Password);

    /// <summary>Exchange credentials for a bearer token (8 hours). Same lockout and rate limits as the login page.</summary>
    [HttpPost("token"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<IActionResult> Token([FromBody] TokenRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password) || req.Password.Length > 128)
            return BadRequest(new { error = "Email and password are required." });
        var r = await _auth.AuthenticateAsync(req.Email, req.Password);
        if (!r.Succeeded) return r.Status == ResultStatus.Forbidden ? StatusCode(423, new { error = r.Error }) : Unauthorized(new { error = r.Error });
        var (token, expires) = _tokens.Issue(r.Value!);
        return Ok(new
        {
            tokenType = "Bearer", accessToken = token, expiresUtc = expires,
            mustChangePassword = r.Value!.MustChangePassword,
            user = new { r.Value.Id, r.Value.FullName, r.Value.Email, role = r.Value.Role.ToString() }
        });
    }

    [HttpGet("me")]
    public IActionResult Me() => Ok(new { _me.Id, fullName = _me.Name, _me.Email, role = _me.Role.ToString(), _me.CanWrite, isAdmin = _me.IsAdmin });

    /// <summary>Exchange a still-valid token for a fresh 8-hour token.</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        var u = await _auth.GetActiveUserAsync(_me.Id, null);
        if (!u.Succeeded) return Unauthorized(new { error = u.Error });
        var (token, expires) = _tokens.Issue(u.Value!);
        return Ok(new { tokenType = "Bearer", accessToken = token, expiresUtc = expires });
    }

    /// <summary>Signs this user out everywhere: every token and browser session issued so far stops working.</summary>
    [HttpPost("revoke")]
    public async Task<IActionResult> Revoke() => From(await _auth.RevokeSessionsAsync(_me.Id));

    public record ForgotRequest(string Email);
    /// <summary>Emails a password-reset link. Always answers 202 so accounts cannot be enumerated.</summary>
    [HttpPost("forgot-password"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<IActionResult> Forgot([FromBody] ForgotRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Email) && req.Email.Length <= 200) await _auth.RequestPasswordResetAsync(req.Email);
        return Accepted(new { message = "If an account exists for that address, a reset link has been sent." });
    }

    public record ResetRequest(string Token, string NewPassword);
    /// <summary>Completes a reset using the token from the emailed link (?token=...).</summary>
    [HttpPost("reset-password"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<IActionResult> Reset([FromBody] ResetRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrEmpty(req.NewPassword)) return BadRequest(new { error = "Token and newPassword are required." });
        var r = await _auth.ResetPasswordAsync(req.Token, req.NewPassword);
        return r.Succeeded ? NoContent() : Problem(r);
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordInput input)
    {
        var r = await _auth.ChangePasswordAsync(_me.Id, input.CurrentPassword, input.NewPassword);
        if (!r.Succeeded) return Problem(r);
        var (token, expires) = _tokens.Issue(r.Value!); // the old token is now invalid (new security stamp)
        return Ok(new { tokenType = "Bearer", accessToken = token, expiresUtc = expires });
    }
}

[Route("api/v1/leads")]
public class LeadsApiController : ApiBase
{
    private readonly LeadService _leads;
    public LeadsApiController(LeadService leads) => _leads = leads;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] LeadQuery query) => Page(await _leads.SearchAsync(query));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id) => From(await _leads.GetAsync(id));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] LeadInput input)
    {
        var r = await _leads.CreateAsync(input);
        return r.Succeeded ? CreatedAtAction(nameof(Get), new { id = r.Value }, new { id = r.Value }) : Problem(r);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] LeadInput input) => From(await _leads.UpdateAsync(id, input));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id) => From(await _leads.DeleteAsync(id));

    [HttpPatch("{id:int}/stage")]
    public async Task<IActionResult> Stage(int id, [FromBody] StageInput input) => From(await _leads.ChangeStageAsync(id, input.Stage));

    [HttpPatch("{id:int}/assignee")]
    public async Task<IActionResult> Assign(int id, [FromBody] AssignInput input) => From(await _leads.AssignAsync(id, input.UserId));

    [HttpPost("{id:int}/notes")]
    public async Task<IActionResult> AddNote(int id, [FromBody] NoteInput input) => From(await _leads.AddNoteAsync(id, input.Body));

    [HttpPost("{id:int}/calls")]
    public async Task<IActionResult> LogCall(int id, [FromBody] CallInput input) => From(await _leads.LogCallAsync(id, input));

    [HttpPost("{id:int}/star")]
    public async Task<IActionResult> Star(int id)
    {
        var r = await _leads.ToggleStarAsync(id);
        return r.Succeeded ? Ok(new { starred = r.Value }) : Problem(r);
    }

    [HttpGet("pipeline")]
    public async Task<IActionResult> Pipeline(int? eventId, int? assignedToId, string? q) => Ok(await _leads.PipelineAsync(eventId, assignedToId, q));

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] LeadQuery query) => File(await _leads.ExportCsvAsync(query), "text/csv", "leads.csv");
}

[Route("api/v1/companies")]
public class CompaniesApiController : ApiBase
{
    private readonly CompanyService _companies;
    public CompaniesApiController(CompanyService companies) => _companies = companies;

    [HttpGet] public async Task<IActionResult> List([FromQuery] CompanyQuery query) => Page(await _companies.SearchAsync(query));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id) => From(await _companies.GetAsync(id));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CompanyInput input)
    {
        var r = await _companies.CreateAsync(input);
        return r.Succeeded ? CreatedAtAction(nameof(Get), new { id = r.Value }, new { id = r.Value }) : Problem(r);
    }

    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] CompanyInput input) => From(await _companies.UpdateAsync(id, input));
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id) => From(await _companies.DeleteAsync(id));
}

[Route("api/v1/events")]
public class EventsApiController : ApiBase
{
    private readonly EventService _events;
    public EventsApiController(EventService events) => _events = events;

    [HttpGet] public async Task<IActionResult> List([FromQuery] EventQuery query) => Page(await _events.SearchAsync(query));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id) => From(await _events.GetAsync(id));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] EventInput input)
    {
        var r = await _events.CreateAsync(input);
        return r.Succeeded ? CreatedAtAction(nameof(Get), new { id = r.Value }, new { id = r.Value }) : Problem(r);
    }

    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] EventInput input) => From(await _events.UpdateAsync(id, input));
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id) => From(await _events.DeleteAsync(id));
}

[Route("api/v1/tasks")]
public class TasksApiController : ApiBase
{
    private readonly TaskService _tasks;
    public TasksApiController(TaskService tasks) => _tasks = tasks;

    [HttpGet] public async Task<IActionResult> Board(int? assignedToId) => From(await _tasks.BoardAsync(assignedToId));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] TaskInput input)
    {
        var r = await _tasks.CreateAsync(input);
        return r.Succeeded ? StatusCode(201, new { id = r.Value }) : Problem(r);
    }

    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] TaskInput input) => From(await _tasks.UpdateAsync(id, input));

    public record CompleteRequest(bool Completed);
    [HttpPatch("{id:int}/complete")]
    public async Task<IActionResult> Complete(int id, [FromBody] CompleteRequest req) => From(await _tasks.SetCompletedAsync(id, req.Completed));
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id) => From(await _tasks.DeleteAsync(id));
}

[Route("api/v1/calendar")]
public class CalendarApiController : ApiBase
{
    private readonly CalendarService _calendar;
    public CalendarApiController(CalendarService calendar) => _calendar = calendar;

    [HttpGet]
    public async Task<IActionResult> Month(int? year, int? month)
    {
        var t = Helpers.Clock.LocalToday;
        return Ok(await _calendar.MonthAsync(year is >= 2000 and <= 2100 ? year.Value : t.Year, month is >= 1 and <= 12 ? month.Value : t.Month));
    }

    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id) => From(await _calendar.GetAsync(id));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CalendarInput input)
    {
        var r = await _calendar.CreateAsync(input);
        return r.Succeeded ? StatusCode(201, new { id = r.Value }) : Problem(r);
    }

    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] CalendarInput input) => From(await _calendar.UpdateAsync(id, input));
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id) => From(await _calendar.DeleteAsync(id));
}

[Route("api/v1/campaigns"), Authorize(Policy = "AdminOnly", AuthenticationSchemes = AuthSchemes.Bearer)]
public class CampaignsApiController : ApiBase
{
    private readonly CampaignService _campaigns;
    public CampaignsApiController(CampaignService campaigns) => _campaigns = campaigns;

    [HttpGet]
    public async Task<IActionResult> List(string? q, CampaignStatus? status, int page = 1, int pageSize = 20)
    {
        var r = await _campaigns.ListAsync(q, status, page, pageSize);
        return r.Succeeded ? Page(r.Value!) : Problem(r);
    }

    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id) => From(await _campaigns.GetAsync(id));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CampaignInput input)
    {
        var r = await _campaigns.CreateAsync(input);
        return r.Succeeded ? CreatedAtAction(nameof(Get), new { id = r.Value }, new { id = r.Value }) : Problem(r);
    }

    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] CampaignInput input) => From(await _campaigns.UpdateAsync(id, input));
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id) => From(await _campaigns.DeleteAsync(id));

    [HttpPost("{id:int}/send")]
    public async Task<IActionResult> Send(int id)
    {
        var r = await _campaigns.SendAsync(id);
        return r.Succeeded ? Accepted(new { recipients = r.Value }) : Problem(r);
    }

    [HttpGet("audience")]
    public async Task<IActionResult> Audience(CampaignTarget target, int? year, int? eventId) => Ok(new { count = await _campaigns.AudienceSizeAsync(target, year, eventId) });
}

[Route("api/v1/users"), Authorize(Policy = "AdminOnly", AuthenticationSchemes = AuthSchemes.Bearer)]
public class UsersApiController : ApiBase
{
    private readonly UserService _users;
    private readonly IAuditService _audit;
    public UsersApiController(UserService users, IAuditService audit) { _users = users; _audit = audit; }

    [HttpGet] public async Task<IActionResult> List(string? q) => From(await _users.ListAsync(q));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id) => From(await _users.GetAsync(id));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserCreateInput input)
    {
        var r = await _users.CreateAsync(input);
        return r.Succeeded ? CreatedAtAction(nameof(Get), new { id = r.Value!.Id }, r.Value) : Problem(r);
    }

    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] UserUpdateInput input) => From(await _users.UpdateAsync(id, input));
    /// <summary>Permanently deletes a user. Their leads and tasks are transferred to <c>transferToId</c> (required).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] int? transferToId) => From(await _users.DeleteAsync(id, transferToId));
    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] AdminResetPasswordInput input) => From(await _users.ResetPasswordAsync(id, input.TemporaryPassword));
    [HttpPost("{id:int}/unlock")] public async Task<IActionResult> Unlock(int id) => From(await _users.UnlockAsync(id));

    [HttpGet("~/api/v1/audit")]
    public async Task<IActionResult> Audit(string? q, [FromQuery(Name = "action")] string? actionFilter, int page = 1, int pageSize = 50) => Page(await _audit.SearchAsync(q, actionFilter, page, pageSize));
}

[Route("api/v1")]
public class MiscApiController : ApiBase
{
    private readonly DashboardService _dash;
    private readonly SearchService _search;
    private readonly INotificationService _notifications;
    private readonly UserService _users;
    public MiscApiController(DashboardService dash, SearchService search, INotificationService notifications, UserService users)
    { _dash = dash; _search = search; _notifications = notifications; _users = users; }

    [HttpGet("dashboard")] public async Task<IActionResult> Dashboard() => Ok(await _dash.GetAsync());
    [HttpGet("search")] public async Task<IActionResult> Search(string? q) => Ok(await _search.SearchAsync(q));
    [HttpGet("team")] public async Task<IActionResult> Team() => Ok(await _users.OptionsAsync());

    [HttpGet("notifications")]
    public async Task<IActionResult> Notifications(string? filter, int page = 1, int pageSize = 25) => Page(await _notifications.ListAsync(filter, page, pageSize));
    [HttpPost("notifications/{id:int}/read")] public async Task<IActionResult> Read(int id) => From(await _notifications.MarkReadAsync(id));
    [HttpPost("notifications/read-all")] public async Task<IActionResult> ReadAll() => Ok(new { marked = await _notifications.MarkAllReadAsync() });
}

[Route("api/v1/integrations/forms"), Authorize(Policy = "AdminOnly", AuthenticationSchemes = AuthSchemes.Bearer)]
public class FormIntegrationsApiController : ApiBase
{
    private readonly FormConnectionService _forms;
    public FormIntegrationsApiController(FormConnectionService forms) => _forms = forms;

    [HttpGet] public async Task<IActionResult> List() => From(await _forms.ListAsync());
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id) => From(await _forms.GetAsync(id));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FormConnectionInput input)
    {
        var r = await _forms.CreateAsync(input);
        return r.Succeeded ? CreatedAtAction(nameof(Get), new { id = r.Value }, new { id = r.Value }) : Problem(r);
    }

    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] FormConnectionInput input) => From(await _forms.UpdateAsync(id, input));
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id) => From(await _forms.DeleteAsync(id));
    [HttpPost("{id:int}/rotate-token")] public async Task<IActionResult> Rotate(int id) => From(await _forms.RotateTokenAsync(id));

    /// <summary>Dry run: shows how a sample payload would be mapped, without creating anything.</summary>
    [HttpPost("{id:int}/preview")]
    public async Task<IActionResult> Preview(int id, [FromBody] System.Text.Json.JsonElement payload) => From(await _forms.PreviewAsync(id, payload.GetRawText()));
}
