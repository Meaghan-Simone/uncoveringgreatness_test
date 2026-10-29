using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UncoveringGreatnessCRM.Tests;

[Collection("crm")]
public class PlatformTests
{
    private readonly CrmFactory _f;
    public PlatformTests(CrmFactory f) => _f = f;

    [Fact]
    public async Task Health_endpoint_reports_ok_and_database_connectivity()
    {
        var r = await _f.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_page_is_public_and_sends_security_headers()
    {
        var r = await _f.CreateClient().GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(r.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Pages_require_sign_in()
    {
        var client = _f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await client.GetAsync("/Leads");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Api_rejects_requests_without_a_token()
    {
        var r = await _f.CreateClient().GetAsync("/api/v1/leads");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Api_rejects_unknown_credentials()
    {
        var r = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new { email = "nobody@example.com", password = "Wrong!Password1" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

[Collection("crm")]
public class LeadVisibilityApiTests
{
    private readonly CrmFactory _f;
    public LeadVisibilityApiTests(CrmFactory f) => _f = f;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Rep_sees_only_assigned_leads_while_admin_sees_all()
    {
        var admin = await _f.ClientForAsync(CrmFactory.AdminEmail);
        var rep = await _f.ClientForAsync(CrmFactory.RepEmail);
        var repId = (await _f.SessionAsync(CrmFactory.RepEmail)).UserId;

        var all = await JsonAsync(await admin.GetAsync("/api/v1/leads?pageSize=200"));
        var mine = await JsonAsync(await rep.GetAsync("/api/v1/leads?pageSize=200"));

        var allTotal = all.GetProperty("totalCount").GetInt32();
        var myTotal = mine.GetProperty("totalCount").GetInt32();
        Assert.True(myTotal > 0, "the seeded sales rep should own some sample leads");
        Assert.True(allTotal > myTotal, "the admin should see more leads than a single rep");
        foreach (var lead in mine.GetProperty("items").EnumerateArray())
            Assert.Equal(repId, lead.GetProperty("assignedToId").GetInt32());
    }

    [Fact]
    public async Task Rep_cannot_open_a_lead_assigned_to_someone_else()
    {
        var admin = await _f.ClientForAsync(CrmFactory.AdminEmail);
        var rep = await _f.ClientForAsync(CrmFactory.RepEmail);
        var repId = (await _f.SessionAsync(CrmFactory.RepEmail)).UserId;

        var all = await JsonAsync(await admin.GetAsync("/api/v1/leads?pageSize=200"));
        var foreign = all.GetProperty("items").EnumerateArray()
            .First(l => l.GetProperty("assignedToId").ValueKind == JsonValueKind.Number && l.GetProperty("assignedToId").GetInt32() != repId);

        var r = await rep.GetAsync($"/api/v1/leads/{foreign.GetProperty("id").GetInt32()}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Dashboard_and_export_are_scoped_to_the_signed_in_rep()
    {
        var rep = await _f.ClientForAsync(CrmFactory.RepEmail);
        var mine = await JsonAsync(await rep.GetAsync("/api/v1/leads?pageSize=200"));
        var myTotal = mine.GetProperty("totalCount").GetInt32();

        var dash = await JsonAsync(await rep.GetAsync("/api/v1/dashboard"));
        Assert.Equal(myTotal, dash.GetProperty("totalLeads").GetInt32());

        var csv = await (await rep.GetAsync("/api/v1/leads/export")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n').Count(l => l.Trim().Length > 0);
        Assert.Equal(myTotal + 1, lines); // header + one row per visible lead
    }

    [Fact]
    public async Task A_lead_created_by_a_rep_stays_visible_to_that_rep()
    {
        var rep = await _f.ClientForAsync(CrmFactory.RepEmail);
        var repId = (await _f.SessionAsync(CrmFactory.RepEmail)).UserId;
        var email = $"new.lead.{Guid.NewGuid():N}@example.com";

        var created = await rep.PostAsJsonAsync("/api/v1/leads", new { firstName = "Casey", surname = "Tester", email });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var detail = await JsonAsync(await rep.GetAsync($"/api/v1/leads/{id}"));
        Assert.Equal(repId, detail.GetProperty("assignedToId").GetInt32());
    }

    [Fact]
    public async Task Rep_is_forbidden_from_admin_endpoints()
    {
        var rep = await _f.ClientForAsync(CrmFactory.RepEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync("/api/v1/audit")).StatusCode);
    }

    [Fact]
    public async Task Admin_dashboard_breaks_results_down_by_person_and_reps_do_not_get_it()
    {
        var admin = await _f.ClientForAsync(CrmFactory.AdminEmail);
        var rep = await _f.ClientForAsync(CrmFactory.RepEmail);

        var adminDash = await JsonAsync(await admin.GetAsync("/api/v1/dashboard"));
        var team = adminDash.GetProperty("team");
        Assert.Equal(JsonValueKind.Object, team.ValueKind);

        var reps = team.GetProperty("reps");
        Assert.True(reps.GetArrayLength() >= 2, "the admin should see more than one person");
        var assignedAcrossTeam = reps.EnumerateArray().Sum(r => r.GetProperty("totalLeads").GetInt32());
        Assert.True(assignedAcrossTeam > 0);
        Assert.True(assignedAcrossTeam <= adminDash.GetProperty("totalLeads").GetInt32());
        Assert.All(reps.EnumerateArray(), r => Assert.False(string.IsNullOrEmpty(r.GetProperty("status").GetString())));

        var repDash = await JsonAsync(await rep.GetAsync("/api/v1/dashboard"));
        Assert.Equal(JsonValueKind.Null, repDash.GetProperty("team").ValueKind);
    }
}

[Collection("crm")]
public class UserManagementApiTests
{
    private readonly CrmFactory _f;
    public UserManagementApiTests(CrmFactory f) => _f = f;

    private static object NewUser(string email) => new
    {
        fullName = "Quality Checker",
        email,
        role = "SalesRep",
        temporaryPassword = "Zebra!Quartz#4821"
    };

    [Fact]
    public async Task Admin_can_create_then_permanently_delete_a_user_and_it_is_audited()
    {
        var admin = await _f.ClientForAsync(CrmFactory.AdminEmail);
        var adminId = (await _f.SessionAsync(CrmFactory.AdminEmail)).UserId;

        var created = await admin.PostAsJsonAsync("/api/v1/users", NewUser($"qa{Guid.NewGuid():N}@example.co.za"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // Deleting needs a new owner for the user's leads and tasks.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/v1/users/{id}")).StatusCode);

        var deleted = await admin.DeleteAsync($"/api/v1/users/{id}?transferToId={adminId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/users/{id}")).StatusCode);

        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/audit?action=UserDeleted");
        Assert.True(audit.GetProperty("totalCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Audit_log_filter_by_action_works_and_unfiltered_list_is_not_empty()
    {
        var admin = await _f.ClientForAsync(CrmFactory.AdminEmail); // signing in writes LoginSuccess entries
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/v1/audit");
        Assert.True(all.GetProperty("totalCount").GetInt32() >= 1);

        var filtered = await admin.GetFromJsonAsync<JsonElement>("/api/v1/audit?action=LoginSuccess");
        Assert.True(filtered.GetProperty("totalCount").GetInt32() >= 1);

        var none = await admin.GetFromJsonAsync<JsonElement>("/api/v1/audit?action=NoSuchAction");
        Assert.Equal(0, none.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Admin_cannot_delete_their_own_account()
    {
        var admin = await _f.ClientForAsync(CrmFactory.AdminEmail);
        var adminId = (await _f.SessionAsync(CrmFactory.AdminEmail)).UserId;
        var repId = (await _f.SessionAsync(CrmFactory.RepEmail)).UserId;

        var r = await admin.DeleteAsync($"/api/v1/users/{adminId}?transferToId={repId}");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Weak_temporary_passwords_are_rejected()
    {
        var admin = await _f.ClientForAsync(CrmFactory.AdminEmail);
        var r = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            fullName = "Weak Password", email = $"weak{Guid.NewGuid():N}@example.co.za", role = "SalesRep", temporaryPassword = "password"
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
