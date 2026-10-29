using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Tests;

public class NotificationEmailTests
{
    [Fact]
    public void Title_body_and_link_appear_in_both_versions()
    {
        var (html, text) = NotificationEmail.Build("Michaela Smith", "New lead assigned to you", "Naledi Khumalo", "https://crm.example.com/Leads/Details/5");
        Assert.Contains("Hi Michaela,", text);
        Assert.Contains("New lead assigned to you", text);
        Assert.Contains("Naledi Khumalo", html);
        Assert.Contains("https://crm.example.com/Leads/Details/5", html);
    }

    [Fact]
    public void Html_in_titles_and_bodies_is_encoded()
    {
        var (html, _) = NotificationEmail.Build("Barry", "<script>alert(1)</script>", "<img src=x onerror=alert(1)>", null);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void A_missing_link_leaves_out_the_open_link()
    {
        var (html, text) = NotificationEmail.Build("Sule", "Something happened", null, null);
        Assert.DoesNotContain("Open in the CRM", html);
        Assert.DoesNotContain("Open it here", text);
    }

    [Fact]
    public void Subjects_never_contain_line_breaks()
    {
        var subject = NotificationEmail.CleanSubject("Line one\r\nBcc: someone@example.com");
        Assert.DoesNotContain('\r', subject);
        Assert.DoesNotContain('\n', subject);
    }
}

public class EmailDiagnosticsTests
{
    [Theory]
    [InlineData("535 5.7.8 Username and Password not accepted", "password")]
    [InlineData("The operation has timed out.", "could not be reached")]
    [InlineData("No such host is known", "name could not be found")]
    [InlineData("5.7.0 Must issue a STARTTLS command first", "UseSsl")]
    public void Common_failures_get_a_plain_english_hint(string error, string expected) =>
        Assert.Contains(expected, EmailDiagnostics.Hint(error, 587)!, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Port_465_always_points_to_587() =>
        Assert.Contains("587", EmailDiagnostics.Hint("anything at all", 465)!);

    [Fact]
    public void Unknown_failures_have_no_hint() =>
        Assert.Null(EmailDiagnostics.Hint("something odd happened", 587));

    [Fact]
    public void Describe_includes_the_inner_messages()
    {
        var text = EmailDiagnostics.Describe(new InvalidOperationException("outer problem", new IOException("inner cause")));
        Assert.Contains("outer problem", text);
        Assert.Contains("inner cause", text);
    }
}

public class EmailDispatchWorkerTests
{
    private sealed class FakeSender : IEmailSender
    {
        public bool IsSimulated => false;
        public List<string> Sent { get; } = new();
        public Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add($"{to}|{subject}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Queued_messages_are_delivered_in_the_background()
    {
        var sender = new FakeSender();
        using var provider = new ServiceCollection().AddSingleton<IEmailSender>(sender).BuildServiceProvider();
        var queue = new EmailQueue();
        var worker = new EmailDispatchWorker(queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EmailDispatchWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        queue.Enqueue(new QueuedEmail("ann@example.com", "First", "<p>1</p>", "1"));
        queue.Enqueue(new QueuedEmail("bob@example.com", "Second", "<p>2</p>", "2"));

        for (var i = 0; i < 50; i++)
        {
            lock (sender.Sent) { if (sender.Sent.Count >= 2) break; }
            await Task.Delay(100);
        }
        await worker.StopAsync(CancellationToken.None);

        lock (sender.Sent)
        {
            Assert.Equal(new[] { "ann@example.com|First", "bob@example.com|Second" }, sender.Sent);
        }
    }
}

/// <summary>
/// The whole form-to-lead path against the real application: create a connection as the admin, then post to its public
/// webhook the way Tally (or any form tool) would, with no sign-in.
/// </summary>
[Collection("crm")]
public class FormWebhookTests
{
    private readonly CrmFactory _f;
    public FormWebhookTests(CrmFactory f) => _f = f;

    private static string UniqueEmail() => $"webhook.{Guid.NewGuid():N}@example.com";

    private static string TallyPayload(string responseId, string email, string name) => JsonSerializer.Serialize(new
    {
        eventId = Guid.NewGuid().ToString(),
        eventType = "FORM_RESPONSE",
        data = new
        {
            responseId,
            formName = "Contact us",
            fields = new object[]
            {
                new { key = "question_a", label = "Name", type = "INPUT_TEXT", value = name },
                new { key = "question_b", label = "Email", type = "INPUT_EMAIL", value = email },
                new { key = "question_c", label = "Company", type = "INPUT_TEXT", value = "Acme Test (Pty) Ltd" }
            }
        }
    });

    private async Task<(int Id, string Path)> CreateConnectionAsync(string? signingSecret = null)
    {
        var admin = await _f.ClientForAsync(CrmFactory.AdminEmail);
        var created = await admin.PostAsJsonAsync("/api/v1/integrations/forms",
            new { name = "Test form " + Guid.NewGuid().ToString("N")[..6], provider = "Tally", assignment = "RoundRobin", signingSecret });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/forms/{id}");
        return (id, new Uri(detail.GetProperty("webhookUrl").GetString()!).AbsolutePath);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, string json, string? signatureHeader = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (signatureHeader != null) request.Headers.Add("Tally-Signature", signatureHeader);
        var response = await _f.CreateClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var body = text.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(text) : default;
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task A_submission_creates_a_lead_and_a_retry_does_not_create_another()
    {
        var (id, path) = await CreateConnectionAsync();
        var email = UniqueEmail();
        var responseId = Guid.NewGuid().ToString("N");
        var payload = TallyPayload(responseId, email, "Test Person");

        var first = await PostAsync(path, payload);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal("created", first.Body.GetProperty("status").GetString());
        var leadId = first.Body.GetProperty("leadId").GetInt32();

        // The form tool retries the same delivery: acknowledged, nothing new created.
        var retry = await PostAsync(path, payload);
        Assert.Equal(HttpStatusCode.OK, retry.Status);
        Assert.Equal("duplicate", retry.Body.GetProperty("status").GetString());

        // The same person submits again later (new response id): added to the existing lead.
        var again = await PostAsync(path, TallyPayload(Guid.NewGuid().ToString("N"), email, "Test Person"));
        Assert.Equal("duplicate", again.Body.GetProperty("status").GetString());
        Assert.Equal(leadId, again.Body.GetProperty("leadId").GetInt32());

        var admin = await _f.ClientForAsync(CrmFactory.AdminEmail);
        var found = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/leads?q={Uri.EscapeDataString(email)}");
        Assert.Equal(1, found.GetProperty("totalCount").GetInt32());
        Assert.Equal("Test Person", found.GetProperty("items")[0].GetProperty("fullName").GetString());

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/forms/{id}");
        Assert.Equal(1, detail.GetProperty("totalCreated").GetInt32());
    }

    [Fact]
    public async Task A_submission_without_an_email_is_rejected_not_turned_into_a_lead()
    {
        var (_, path) = await CreateConnectionAsync();
        var json = JsonSerializer.Serialize(new { data = new { responseId = Guid.NewGuid().ToString("N"), fields = new[] { new { label = "Name", type = "INPUT_TEXT", value = "No Email" } } } });

        var result = await PostAsync(path, json);
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal("rejected", result.Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unknown_webhook_addresses_and_broken_bodies_are_refused()
    {
        var unknown = await PostAsync("/integrations/forms/" + new string('a', 43), TallyPayload("x", UniqueEmail(), "Nobody"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);

        var (_, path) = await CreateConnectionAsync();
        var broken = await PostAsync(path, "{this is not json");
        Assert.Equal(HttpStatusCode.BadRequest, broken.Status);
    }

    [Fact]
    public async Task The_webhook_address_answers_a_browser_check()
    {
        var (_, path) = await CreateConnectionAsync();
        var response = await _f.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task With_a_signing_secret_only_correctly_signed_requests_are_accepted()
    {
        const string secret = "a-long-shared-secret-123";
        var (_, path) = await CreateConnectionAsync(secret);
        var json = TallyPayload(Guid.NewGuid().ToString("N"), UniqueEmail(), "Signed Person");

        var unsigned = await PostAsync(path, json);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.Status);

        var wrong = await PostAsync(path, json, "not-the-right-signature");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(json)));
        var signed = await PostAsync(path, json, signature);
        Assert.Equal(HttpStatusCode.OK, signed.Status);
        Assert.Equal("created", signed.Body.GetProperty("status").GetString());
    }
}

[Collection("crm")]
public class EmailDeliveryPageTests
{
    private readonly CrmFactory _f;
    public EmailDeliveryPageTests(CrmFactory f) => _f = f;

    [Fact]
    public async Task The_admin_can_open_the_email_delivery_page()
    {
        var admin = await _f.ClientForAsync(CrmFactory.AdminEmail);
        var response = await admin.GetAsync("/Integrations/Email");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Send test email", html);
    }
}

public class EmailOptionsTests
{
    [Fact]
    public void Nothing_set_means_not_configured()
    {
        var o = new EmailOptions();
        Assert.False(o.IsConfigured);
        Assert.False(o.UsesBrevoApi);
    }

    [Fact]
    public void A_host_means_smtp()
    {
        var o = new EmailOptions { Host = "smtp.example.com" };
        Assert.True(o.IsConfigured);
        Assert.False(o.UsesBrevoApi);
    }

    [Fact]
    public void An_api_key_without_a_host_means_brevo()
    {
        var o = new EmailOptions { ApiKey = "xkeysib-test" };
        Assert.True(o.UsesBrevoApi);
        Assert.True(o.IsConfigured);
    }

    [Fact]
    public void Brevo_chosen_by_name_still_needs_a_key()
    {
        var o = new EmailOptions { Provider = "brevo" };
        Assert.True(o.UsesBrevoApi);
        Assert.False(o.IsConfigured);
    }

    [Fact]
    public void A_host_wins_over_a_stray_api_key()
    {
        var o = new EmailOptions { Host = "smtp.example.com", ApiKey = "xkeysib-test" };
        Assert.False(o.UsesBrevoApi);
    }
}

public class BrevoEmailSenderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;
        public string Reply { get; set; } = "{\"messageId\":\"<abc@smtp-relay.mailin.fr>\"}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent(Reply) };
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static BrevoEmailSender Sender(StubHandler handler) => new(
        Options.Create(new EmailOptions { ApiKey = "xkeysib-test", FromAddress = "sales@example.co.za", FromName = "Uncovering Greatness" }),
        new StubFactory(handler));

    [Fact]
    public async Task A_message_is_posted_to_brevo_with_the_key_sender_and_recipient()
    {
        var handler = new StubHandler();
        await Sender(handler).SendAsync("jane@acme.co.za", "Hello there", "<p>Hi</p>", "Hi");

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(BrevoEmailSender.Endpoint, handler.Request.RequestUri!.ToString());
        Assert.Equal("xkeysib-test", handler.Request.Headers.GetValues("api-key").Single());

        using var doc = JsonDocument.Parse(handler.Body);
        var root = doc.RootElement;
        Assert.Equal("sales@example.co.za", root.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("jane@acme.co.za", root.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("Hello there", root.GetProperty("subject").GetString());
        Assert.Equal("<p>Hi</p>", root.GetProperty("htmlContent").GetString());
        Assert.Equal("Hi", root.GetProperty("textContent").GetString());
    }

    [Fact]
    public async Task A_refusal_from_brevo_becomes_an_error_that_says_why()
    {
        var handler = new StubHandler { Status = HttpStatusCode.Unauthorized, Reply = "{\"code\":\"unauthorized\",\"message\":\"Key not found\"}" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Sender(handler).SendAsync("jane@acme.co.za", "Hi", "<p>Hi</p>", "Hi"));
        Assert.Contains("Brevo refused", error.Message);
        Assert.Contains("401", error.Message);
        Assert.Contains("Key not found", error.Message);
        Assert.Contains("API key", EmailDiagnostics.Hint(EmailDiagnostics.Describe(error), 587)!);
    }

    [Fact]
    public void An_unverified_sender_gets_a_plain_english_hint()
    {
        var hint = EmailDiagnostics.Hint("Brevo refused the message (400 Bad Request): {\"message\":\"Sender is not valid\"}", 587);
        Assert.Contains("verified sender", hint!);
    }
}
