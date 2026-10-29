using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

[Authorize(Policy = "AdminOnly")]
public class IntegrationsController : AppController
{
    private readonly FormConnectionService _forms;
    private readonly EventService _events;
    private readonly UserService _users;
    private readonly IEmailSender _email;
    private readonly EmailOptions _emailOptions;
    private readonly ReminderOptions _reminderOptions;
    private readonly NotificationOptions _notificationOptions;
    private readonly IConfiguration _cfg;
    private readonly ICurrentUser _me;
    private readonly IAuditService _audit;

    public IntegrationsController(FormConnectionService forms, EventService events, UserService users, IEmailSender email,
        IOptions<EmailOptions> emailOptions, IOptions<ReminderOptions> reminderOptions, IOptions<NotificationOptions> notificationOptions,
        IConfiguration cfg, ICurrentUser me, IAuditService audit)
    {
        _forms = forms; _events = events; _users = users; _email = email;
        _emailOptions = emailOptions.Value; _reminderOptions = reminderOptions.Value; _notificationOptions = notificationOptions.Value;
        _cfg = cfg; _me = me; _audit = audit;
    }

    private async Task LoadLookupsAsync()
    {
        ViewBag.Events = await _events.OptionsAsync();
        ViewBag.Users = (await _users.OptionsAsync()).Where(u => u.Role != UserRole.Staff).ToList();
    }

    public async Task<IActionResult> Index()
    {
        var r = await _forms.ListAsync();
        return r.Succeeded ? View(r.Value) : Denied(r) ?? NotFound();
    }

    public async Task<IActionResult> Details(int id)
    {
        var r = await _forms.GetAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        return View(r.Value);
    }

    [HttpGet]
    public async Task<IActionResult> Create() { await LoadLookupsAsync(); return View(new FormConnectionInput()); }

    [HttpPost]
    public async Task<IActionResult> Create(FormConnectionInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _forms.CreateAsync(input);
            if (r.Succeeded) { Flash("Form connected. Copy the webhook URL below into your form tool."); return RedirectToAction(nameof(Details), new { id = r.Value }); }
            AddErrors(r);
        }
        await LoadLookupsAsync();
        return View(input);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var r = await _forms.GetInputAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        var detail = await _forms.GetAsync(id);
        ViewBag.ConnectionId = id; ViewBag.HasSecret = detail.Value?.HasSigningSecret ?? false;
        await LoadLookupsAsync();
        return View(r.Value);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, FormConnectionInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _forms.UpdateAsync(id, input);
            if (r.Succeeded) { Flash("Connection updated."); return RedirectToAction(nameof(Details), new { id }); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        var detail = await _forms.GetAsync(id);
        ViewBag.ConnectionId = id; ViewBag.HasSecret = detail.Value?.HasSigningSecret ?? false;
        await LoadLookupsAsync();
        return View(input);
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _forms.DeleteAsync(id);
        Flash(r.Succeeded ? "Connection deleted. Leads it created were kept." : r.Error ?? "Could not delete.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Rotate(int id)
    {
        var r = await _forms.RotateTokenAsync(id);
        Flash(r.Succeeded ? "New webhook URL issued. Update it in your form tool: the old one no longer works." : r.Error ?? "Could not rotate.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------ email delivery check

    private EmailStatusVm BuildEmailStatus() => new()
    {
        Configured = _emailOptions.IsConfigured,
        UsesApi = _emailOptions.UsesBrevoApi, HasApiKey = !string.IsNullOrWhiteSpace(_emailOptions.ApiKey),
        Host = _emailOptions.Host, Port = _emailOptions.Port, UseSsl = _emailOptions.UseSsl,
        Username = _emailOptions.Username, HasPassword = !string.IsNullOrEmpty(_emailOptions.Password),
        FromAddress = _emailOptions.FromAddress, FromName = _emailOptions.FromName,
        PublicBaseUrl = _cfg["App:PublicBaseUrl"],
        ReminderEmailsOn = _reminderOptions.Enabled && _reminderOptions.EmailEnabled,
        NotificationEmailsOn = _notificationOptions.EmailEnabled,
        SuggestedTo = _me.Email
    };

    [HttpGet]
    public IActionResult Email() => View(BuildEmailStatus());

    /// <summary>Sends one real message right now and reports exactly what the mail server said, so a wrong setting is easy to find.</summary>
    [HttpPost]
    public async Task<IActionResult> SendTestEmail(string? to)
    {
        var target = string.IsNullOrWhiteSpace(to) ? _me.Email : to.Trim();
        if (target.Length > 200 || !new EmailAddressAttribute().IsValid(target))
        {
            Flash("Enter a valid email address to send the test to.", "error");
            return RedirectToAction(nameof(Email));
        }

        try
        {
            var (html, text) = NotificationEmail.Build(_me.Name, "This is a test email", "If you can read this, Uncovering Greatness CRM can send email.", null);
            await _email.SendAsync(target, "Test email from Uncovering Greatness CRM", html, text, HttpContext.RequestAborted);
            await _audit.LogAsync("TestEmailSent", "Email", null, _email.IsSimulated ? $"To {target} (simulated, nothing was sent)" : $"To {target}");
            if (_email.IsSimulated)
                Flash("Email is not set up yet, so nothing was really sent: the message was only written to the server log. Fill in the Email settings (see below), restart the app and try again.", "info");
            else
                Flash($"Test email sent to {target}. It should arrive within a minute (check the spam folder too).");
        }
        catch (Exception ex)
        {
            var detail = EmailDiagnostics.Describe(ex);
            if (detail.Length > 500) detail = detail[..500];
            var hint = EmailDiagnostics.Hint(detail, _emailOptions.Port);
            await _audit.LogAsync("TestEmailFailed", "Email", null, detail);
            Flash("The test email could not be sent: " + detail + (hint != null ? " Suggestion: " + hint : ""), "error");
        }
        return RedirectToAction(nameof(Email));
    }

    [HttpPost]
    public async Task<IActionResult> Preview(int id, FormPreviewInput input)
    {
        var detail = await _forms.GetAsync(id);
        if (!detail.Succeeded) return Denied(detail) ?? NotFound();
        if (ModelState.IsValid)
        {
            var p = await _forms.PreviewAsync(id, input.Payload);
            ViewBag.Preview = p.Value;
        }
        ViewBag.SamplePayload = input.Payload;
        return View("Details", detail.Value);
    }
}
