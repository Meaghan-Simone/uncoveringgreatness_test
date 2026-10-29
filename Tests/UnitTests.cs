using System.Text;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Helpers;
using UncoveringGreatnessCRM.Security;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("short1!A")]            // too short
    [InlineData("alllowercase1!")]      // no uppercase
    [InlineData("ALLUPPERCASE1!")]      // no lowercase
    [InlineData("NoNumbersHere!!")]     // no digit
    [InlineData("NoSymbolsHere12")]     // no symbol
    public void Weak_passwords_are_rejected(string password) =>
        Assert.NotEmpty(PasswordService.Validate(password));

    [Fact]
    public void Strong_password_is_accepted() =>
        Assert.Empty(PasswordService.Validate("Zebra!Quartz#4821"));

    [Fact]
    public void Password_containing_the_email_name_is_rejected() =>
        Assert.NotEmpty(PasswordService.Validate("Jonathan!Smith2026", "jonathan@example.com"));

    [Fact]
    public void Generated_temporary_passwords_always_meet_the_policy()
    {
        for (var i = 0; i < 100; i++)
            Assert.Empty(PasswordService.Validate(PasswordService.GenerateTemporary()));
    }

    [Fact]
    public void Hash_verifies_only_the_right_password()
    {
        var svc = new PasswordService();
        var user = new User();
        user.PasswordHash = svc.Hash(user, "Zebra!Quartz#4821");
        Assert.True(svc.Verify(user, "Zebra!Quartz#4821", out _));
        Assert.False(svc.Verify(user, "Zebra!Quartz#0000", out _));
    }
}

public class CsvHelperTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void Formula_injection_is_neutralised(string input, string expected) =>
        Assert.Equal(expected, CsvHelper.Cell(input));

    [Fact]
    public void Commas_and_quotes_are_escaped()
    {
        Assert.Equal("\"a,b\"", CsvHelper.Cell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvHelper.Cell("say \"hi\""));
        Assert.Equal("", CsvHelper.Cell(null));
        Assert.Equal("1.50", CsvHelper.Cell(1.5m));
    }

    [Fact]
    public void Build_starts_with_a_utf8_bom_so_excel_reads_it_correctly()
    {
        var bytes = CsvHelper.Build(new[] { "A", "B" }, new[] { new object?[] { "x", "y" } });
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
    }

    [Fact]
    public void Parse_handles_quoted_fields_with_embedded_newlines()
    {
        var rows = CsvHelper.Parse(new StringReader("Name,Notes\r\nAnn,\"line1\nline2\"\r\n"));
        Assert.Equal(2, rows.Count);
        Assert.Equal("Ann", rows[1][0]);
        Assert.Equal("line1\nline2", rows[1][1]);
    }
}

public class ResultTests
{
    [Fact]
    public void Failures_carry_status_and_field_errors()
    {
        var r = Result.Invalid("Email", "Bad email.");
        Assert.False(r.Succeeded);
        Assert.Equal(ResultStatus.Invalid, r.Status);
        Assert.Equal("Bad email.", r.Errors!["Email"][0]);
        Assert.Equal(ResultStatus.Forbidden, Result.Forbidden().Status);
        Assert.Equal(ResultStatus.NotFound, Result.NotFound("Lead").Status);
    }

    [Fact]
    public void Generic_result_keeps_the_status_of_the_result_it_was_built_from()
    {
        var r = Result<int>.From(Result.Conflict("Duplicate."));
        Assert.Equal(ResultStatus.Conflict, r.Status);
        Assert.True(Result<int>.Success(7).Succeeded);
    }

    [Fact]
    public void Paging_maths_is_correct()
    {
        var p = new PagedResult<int>(Array.Empty<int>(), 1, 25, 51);
        Assert.Equal(3, p.TotalPages);
        Assert.True(p.HasNext);
        Assert.False(p.HasPrevious);
        Assert.Equal(1, new PagedResult<int>(Array.Empty<int>(), 1, 25, 0).TotalPages);
    }
}

public class LeadVisibilityTests
{
    private sealed class FakeUser : ICurrentUser
    {
        public FakeUser(int id, UserRole role) { Id = id; Role = role; }
        public bool IsAuthenticated => true;
        public int Id { get; }
        public string Name => "Test";
        public string Email => "test@example.com";
        public UserRole Role { get; }
        public bool IsAdmin => Role == UserRole.Admin;
        public bool CanWrite => Role != UserRole.Staff;
        public string? IpAddress => null;
    }

    private static IQueryable<Lead> Leads() => new List<Lead>
    {
        new() { Id = 1, AssignedToId = 2 },
        new() { Id = 2, AssignedToId = 2 },
        new() { Id = 3, AssignedToId = 3 },
        new() { Id = 4, AssignedToId = null },
    }.AsQueryable();

    [Fact]
    public void Admin_sees_every_lead() =>
        Assert.Equal(4, Leads().VisibleTo(new FakeUser(1, UserRole.Admin)).Count());

    [Fact]
    public void Sales_rep_sees_only_their_own_leads() =>
        Assert.Equal(new[] { 1, 2 }, Leads().VisibleTo(new FakeUser(2, UserRole.SalesRep)).Select(l => l.Id).OrderBy(x => x).ToArray());

    [Fact]
    public void Unassigned_leads_are_hidden_from_non_admins() =>
        Assert.DoesNotContain(Leads().VisibleTo(new FakeUser(3, UserRole.SalesRep)), l => l.AssignedToId == null);

    [Fact]
    public void Staff_with_no_assigned_leads_sees_nothing() =>
        Assert.Empty(Leads().VisibleTo(new FakeUser(9, UserRole.Staff)));
}

public class FormPayloadParserTests
{
    [Fact]
    public void Flat_json_is_mapped_to_lead_fields()
    {
        var (parsed, mapped, error) = FormPayloadParser.TryProcess("{\"Email\":\"ann@example.com\",\"Name\":\"Ann Lee\"}", null);
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("ann@example.com", mapped!.Get("Email"));
    }

    [Fact]
    public void Invalid_json_is_reported_not_thrown()
    {
        var (_, _, error) = FormPayloadParser.TryProcess("{not json", null);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Payload_without_answers_is_rejected()
    {
        var (_, _, error) = FormPayloadParser.TryProcess("{}", null);
        Assert.False(string.IsNullOrEmpty(error));
    }
}

public class PerformanceRatingTests
{
    [Fact]
    public void Someone_with_no_leads_is_reported_as_having_none()
    {
        var (status, _) = PerformanceRating.Evaluate(0, 0, 0, 0, 0, null, false);
        Assert.Equal(PerformanceRating.NoLeads, status);
    }

    [Fact]
    public void Three_or_more_overdue_follow_ups_needs_attention()
    {
        var (status, reason) = PerformanceRating.Evaluate(10, 8, PerformanceRating.OverdueFollowUpLimit, 0, 2, 50, true);
        Assert.Equal(PerformanceRating.NeedsAttention, status); // even the month's best seller is flagged
        Assert.Contains("overdue", reason);
    }

    [Fact]
    public void Two_overdue_follow_ups_is_still_on_track()
    {
        var (status, _) = PerformanceRating.Evaluate(10, 8, PerformanceRating.OverdueFollowUpLimit - 1, 0, 2, 50, false);
        Assert.Equal(PerformanceRating.OnTrack, status);
    }

    [Fact]
    public void Half_the_open_leads_going_cold_needs_attention()
    {
        var (status, reason) = PerformanceRating.Evaluate(10, 6, 0, 3, 0, null, false);
        Assert.Equal(PerformanceRating.NeedsAttention, status);
        Assert.Contains("not contacted", reason);
    }

    [Fact]
    public void A_low_win_rate_only_counts_once_enough_deals_have_closed()
    {
        var early = PerformanceRating.Evaluate(5, 3, 0, 0, PerformanceRating.MinClosedForWinRate - 1, 0, false);
        Assert.Equal(PerformanceRating.OnTrack, early.Status);

        var enough = PerformanceRating.Evaluate(5, 2, 0, 0, PerformanceRating.MinClosedForWinRate, 10, false);
        Assert.Equal(PerformanceRating.NeedsAttention, enough.Status);
    }

    [Fact]
    public void Healthy_person_with_the_best_month_is_the_top_performer()
    {
        var (status, _) = PerformanceRating.Evaluate(12, 9, 0, 0, 4, 60, true);
        Assert.Equal(PerformanceRating.TopPerformer, status);
    }
}
