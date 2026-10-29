using System.Net;
using System.Text;
using System.Threading.Channels;

namespace UncoveringGreatnessCRM.Services;

/// <summary>Settings under "Notifications" in configuration.</summary>
public class NotificationOptions
{
    /// <summary>
    /// Also email people when something happens that concerns them (a lead is assigned to them, a form lead arrives, a deal is won,
    /// a task is assigned, a calendar entry is shared). Scheduled reminders are emailed separately by the reminder digest.
    /// Needs the Email settings; without them the messages are only written to the log.
    /// </summary>
    public bool EmailEnabled { get; set; } = true;
}

public sealed record QueuedEmail(string To, string Subject, string Html, string Text);

/// <summary>An in-memory outbox. Callers hand a message over instantly; <see cref="EmailDispatchWorker"/> delivers it.</summary>
public interface IEmailQueue
{
    void Enqueue(QueuedEmail message);
    ChannelReader<QueuedEmail> Reader { get; }
}

public class EmailQueue : IEmailQueue
{
    // Bounded so a mail outage can never grow memory without limit; the oldest unsent message is dropped first.
    private readonly Channel<QueuedEmail> _channel = Channel.CreateBounded<QueuedEmail>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Enqueue(QueuedEmail message) => _channel.Writer.TryWrite(message);
    public ChannelReader<QueuedEmail> Reader => _channel.Reader;
}

/// <summary>
/// Sends queued notification emails in the background, so a slow or unreachable mail server never delays a web request
/// (SMTP can take up to 30 seconds to fail). Each message is tried up to three times.
/// </summary>
public class EmailDispatchWorker : BackgroundService
{
    private const int MaxAttempts = 3;
    private readonly IEmailQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EmailDispatchWorker> _log;

    public EmailDispatchWorker(IEmailQueue queue, IServiceScopeFactory scopes, ILogger<EmailDispatchWorker> log)
    { _queue = queue; _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                    await sender.SendAsync(message.To, message.Subject, message.Html, message.Text, stoppingToken);
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (attempt == MaxAttempts)
                        _log.LogWarning("Could not email {To} (\"{Subject}\") after {Attempts} attempts: {Reason}", message.To, message.Subject, attempt, EmailDiagnostics.Describe(ex));
                    else
                        await Task.Delay(TimeSpan.FromSeconds(5 * attempt), stoppingToken);
                }
            }
        }
    }
}

/// <summary>The plain-text and HTML body for an "something happened" email.</summary>
public static class NotificationEmail
{
    public static string CleanSubject(string title)
    {
        var s = new string(title.Select(c => c is '\r' or '\n' ? ' ' : c).ToArray()).Trim();
        return s.Length == 0 ? "Notification from Uncovering Greatness CRM" : s.Length > 200 ? s[..200] : s;
    }

    public static (string Html, string Text) Build(string name, string title, string? body, string? absoluteUrl)
    {
        var first = string.IsNullOrWhiteSpace(name) ? "there" : name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        var text = new StringBuilder($"Hi {first},\n\n{title}\n");
        if (!string.IsNullOrWhiteSpace(body)) text.Append(body).Append('\n');
        if (absoluteUrl != null) text.Append('\n').Append("Open it here: ").Append(absoluteUrl).Append('\n');
        text.Append("\nYou receive this because you use Uncovering Greatness CRM.\n");

        var html = new StringBuilder("<div style=\"font-family:Segoe UI,Arial,sans-serif;color:#2F3A1B;max-width:560px\">");
        html.Append($"<p>Hi {WebUtility.HtmlEncode(first)},</p>");
        html.Append("<div style=\"border-left:4px solid #6B8E23;background:#F7F8F2;padding:10px 14px;margin:10px 0\">");
        html.Append($"<strong>{WebUtility.HtmlEncode(title)}</strong>");
        if (!string.IsNullOrWhiteSpace(body)) html.Append($"<br>{WebUtility.HtmlEncode(body)}");
        if (absoluteUrl != null) html.Append($"<br><a href=\"{WebUtility.HtmlEncode(absoluteUrl)}\" style=\"color:#4B5D2A\">Open in the CRM</a>");
        html.Append("</div><p style=\"font-size:13px;color:#5C6842\">You receive this because you use Uncovering Greatness CRM.</p></div>");
        return (html.ToString(), text.ToString());
    }
}

/// <summary>Turns a mail-server failure into something a person can act on.</summary>
public static class EmailDiagnostics
{
    /// <summary>The exception message plus every inner message, without repeats.</summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message)) parts.Add(e.Message.Trim());
        return string.Join(" → ", parts.Distinct());
    }

    /// <summary>A plain-English suggestion for the most common causes, or null when nothing obvious fits.</summary>
    public static string? Hint(string detail, int port)
    {
        if (detail.Contains("Brevo", StringComparison.OrdinalIgnoreCase))
        {
            var b = detail.ToLowerInvariant();
            if (b.Contains("401") || b.Contains("unauthorized") || b.Contains("key not found") || b.Contains("api key"))
                return "Brevo did not accept the API key. Create a new one in Brevo under SMTP & API → API keys and set Email:ApiKey to it (copy the whole key, no spaces).";
            if (b.Contains("sender") || b.Contains("not valid") || b.Contains("not validated") || b.Contains("not verified"))
                return "Brevo only sends from a verified sender. In Brevo open Senders, Domains & Dedicated IPs → Senders, add and verify the address, and set Email:FromAddress to exactly that address.";
            if (b.Contains("429") || b.Contains("too many"))
                return "Brevo says too many requests were sent. Wait a minute and try again.";
            return null;
        }
        if (port == 465)
            return "Port 465 isn't supported by the built-in mail client. Ask your provider for their port 587 (STARTTLS) settings and set Email:Port to 587 with Email:UseSsl true.";
        var d = detail.ToLowerInvariant();
        if (d.Contains("authentication required") || d.Contains("starttls") || d.Contains("5.7.0"))
            return "The server wants a secure, signed-in connection. Check that Email:UseSsl is true and that Email:Username and Email:Password are filled in.";
        if (d.Contains("535") || d.Contains("5.7.8") || d.Contains("username and password not accepted") || d.Contains("authentication failed") || d.Contains("invalid credentials"))
            return "The mail server rejected the username or password. Gmail and Microsoft 365 usually need an app password (not your normal password), and the username is normally the full email address.";
        if (d.Contains("no such host") || d.Contains("name or service not known") || d.Contains("could not resolve") || d.Contains("nodename nor servname"))
            return "The mail server name could not be found. Check Email:Host for typos (for example smtp.gmail.com).";
        if (d.Contains("timed out") || d.Contains("timeout") || d.Contains("connection refused") || d.Contains("actively refused") || d.Contains("unable to connect"))
            return "The mail server could not be reached. Check Email:Host and Email:Port, and that the network allows outbound port 587. Some hosts (including free hosting plans) block outgoing mail.";
        if (d.Contains("certificate") || d.Contains("ssl") || d.Contains("tls"))
            return "The secure connection failed. Make sure Email:Port is 587 and Email:UseSsl is true.";
        if (d.Contains("5.7.1") || d.Contains("relay") || d.Contains("sender address rejected") || d.Contains("not allowed to send as"))
            return "The server refused the From address. Set Email:FromAddress to the same address as Email:Username (or an address your provider lets you send from).";
        return null;
    }
}
