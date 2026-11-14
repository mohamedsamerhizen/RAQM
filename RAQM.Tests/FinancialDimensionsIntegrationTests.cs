using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.Organizations;
using Xunit;

namespace RAQM.Tests;

[Collection("PostgreSQL"), Trait("Category", "Integration")]
public sealed partial class FinancialDimensionsIntegrationTests(PostgreSqlFixture fixture)
{
    private sealed record User(HttpClient Client, Guid Id, Guid Org) : IDisposable { public void Dispose() => Client.Dispose(); }
    private sealed record Setup(User Owner, User Maker, Company Company, FiscalYearResponse Year, Dictionary<string, Guid> Chart) : IDisposable
    {
        public string Root => $"/api/v1/organizations/{Owner.Org}/companies/{Company.Id}";
        public string Core => Root + "/accounting";
        public string Dimensions => Root + "/dimensions";
        public DateOnly Date => Year.StartDate;
        public void Dispose() { Owner.Dispose(); Maker.Dispose(); }
    }
    private static async Task<T> Send<T>(HttpClient client, string route, object input, HttpStatusCode status = HttpStatusCode.OK, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, route) { Content = JsonContent.Create(input) };
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == status, $"{route}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private async Task<User> Onboard()
    {
        var client = fixture.Factory.CreateClient();
        var token = await Send<TokenResponse>(client, "/api/v1/auth/onboard",
            new OnboardRequest($"{Guid.NewGuid():N}@dimensions.example", "Dimensions_Test-Password1!", "Analytical Org", ApiFactory.OnboardingKey));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        return new(client, me.GetProperty("userId").GetGuid(), me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid());
    }
    private async Task<Setup> Arrange()
    {
        var owner = await Onboard();
        var maker = await Onboard();
        var company = await Send<Company>(owner.Client, $"/api/v1/organizations/{owner.Org}/companies",
            new CompanyRequest("Analytical Company", Guid.NewGuid().ToString("N"), "USD", 1, 1), HttpStatusCode.Created);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.OrganizationMembers.Add(new() { TenantId = company.TenantId, OrganizationId = owner.Org, UserId = maker.Id, Role = OrganizationRole.Member });
            db.CompanyMembers.Add(new() { TenantId = company.TenantId, OrganizationId = owner.Org, CompanyId = company.Id,
                UserId = maker.Id, Role = CompanyRole.Manager });
            await db.SaveChangesAsync();
        }
        var core = $"/api/v1/organizations/{owner.Org}/companies/{company.Id}/accounting";
        var chart = (await Send<List<AccountResponse>>(owner.Client, core + "/accounts/initialize", new InitializeAccountsRequest())).ToDictionary(x => x.Code, x => x.Id);
        var year = await Send<FiscalYearResponse>(owner.Client, core + "/fiscal-years", new FiscalYearRequest(DateTime.UtcNow.Year - 1, "Analytical year"),
            HttpStatusCode.Created);
        return new(owner, maker, company, year, chart);
    }
    private static Task<DimensionDefinition> Definition(Setup s, DimensionKind kind) => Send<DimensionDefinition>(s.Owner.Client, s.Dimensions + "/definitions",
        new DefinitionRequest(kind, kind.ToString(), kind.ToString(), "Financial dimension"));
    private static Task<DimensionValue> Value(Setup s, DimensionDefinition definition, string code, Guid? parent = null, Guid? department = null) =>
        Send<DimensionValue>(s.Owner.Client, s.Dimensions + "/values", new ValueRequest(definition.Id, code, code, "Operating", s.Date, parent, department));
    private static Task<JournalResponse> Journal(Setup s, decimal amount, DateOnly? at = null) => Send<JournalResponse>(s.Owner.Client, s.Core + "/journals",
        new JournalRequest(at ?? s.Date, at ?? s.Date, "Direct expense", null, [new(s.Chart["5100"], amount, 0, null), new(s.Chart["1100"], 0, amount, null)]),
        HttpStatusCode.Created);
    private static Task<DimensionBinding> Attach(Setup s, DimensionSource source, Guid id, Guid version, IReadOnlyList<DimensionLine> lines, int revision = 0) =>
        Send<DimensionBinding>(s.Maker.Client, $"{s.Dimensions}/sources/{source}/{id}", new DimensionBindingRequest(version, revision, lines, "Trusted source coding"));
    private static Task<PostingReceipt> Post(Setup s, JournalResponse draft, string key) => Send<PostingReceipt>(s.Owner.Client,
        $"{s.Core}/journals/{draft.Id}/post", new PostJournalRequest(key, draft.Version, true, "Authorized accounting date"));
    private static async Task<JsonElement> Report(Setup s, Guid definition, DateOnly? to = null, string extra = "") =>
        await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Dimensions}/report?definition={definition}&from={s.Date:yyyy-MM-dd}&to={to ?? s.Date:yyyy-MM-dd}{extra}");
    private static JsonElement Group(JsonElement report, Guid value) => Assert.Single(report.GetProperty("groups").EnumerateArray(),
        x => !x.GetProperty("isRollup").GetBoolean() && x.GetProperty("valueId").ValueKind == JsonValueKind.String && x.GetProperty("valueId").GetGuid() == value);

    [Fact]
    public async Task RequiredDimensionGuardsNewPostingAndLeavesHistoricalUnassignedAndEvidenceImmutable()
    {
        using var s = await Arrange();
        await Post(s, await Journal(s, 20), "old");
        var definition = await Definition(s, DimensionKind.Department);
        var department = await Value(s, definition, "OPS");
        await Send<DimensionRule>(s.Owner.Client, s.Dimensions + "/rules", new DimensionRuleRequest(definition.Id, s.Chart["5100"],
            DimensionMode.Required, null, s.Date, null, "Department required"));
        var draft = await Journal(s, 70);
        using var denied = await s.Owner.Client.PostAsJsonAsync($"{s.Core}/journals/{draft.Id}/post", new PostJournalRequest("new", draft.Version, true, "Test"));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var binding = await Attach(s, DimensionSource.Manual, draft.Id, draft.Version, [new(1, [new(definition.Id, department.Id)])]);
        var posted = await Post(s, draft, "new");
        Assert.Equal(posted.JournalId, (await Post(s, draft, "new")).JournalId);
        var report = await Report(s, definition.Id);
        Assert.Equal(70m, Group(report, department.Id).GetProperty("expense").GetDecimal());
        Assert.Equal(20m, report.GetProperty("reconciliation").GetProperty("unassigned").GetDecimal());
        var entry = Assert.Single(report.GetProperty("drill").GetProperty("items").EnumerateArray(),
            x => x.GetProperty("evidence").GetArrayLength() == 1);
        Assert.Equal(binding.Id, entry.GetProperty("evidence")[0].GetProperty("bindingId").GetGuid());
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""DELETE FROM analytics."PostedDimensions" WHERE "BindingId"=@id""", connection);
        command.Parameters.AddWithValue("id", binding.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task HierarchyRollsUpChildrenOnceAndCrossCompanyParentIsDenied()
    {
        using var s = await Arrange();
        using var foreign = await Arrange();
        var definition = await Definition(s, DimensionKind.CostCenter);
        var other = await Definition(foreign, DimensionKind.CostCenter);
        var parent = await Value(s, definition, "ROOT");
        var child = await Value(s, definition, "CHILD", parent.Id);
        var external = await Value(foreign, other, "EXTERNAL");
        using var wrong = await s.Owner.Client.PostAsJsonAsync(s.Dimensions + "/values", new ValueRequest(definition.Id, "BAD", "Bad", "Scope", s.Date, external.Id));
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        using var duplicate = await s.Owner.Client.PostAsJsonAsync(s.Dimensions + "/values", new ValueRequest(definition.Id, "CHILD", "Duplicate", "Scope", s.Date));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var draft = await Journal(s, 40);
        await Attach(s, DimensionSource.Manual, draft.Id, draft.Version, [new(1, [new(definition.Id, child.Id)])]);
        await Post(s, draft, "child");
        var report = await Report(s, definition.Id);
        Assert.Equal(40m, Group(report, child.Id).GetProperty("expense").GetDecimal());
        Assert.Equal(40m, Assert.Single(report.GetProperty("groups").EnumerateArray(),
            x => x.GetProperty("isRollup").GetBoolean()).GetProperty("expense").GetDecimal());
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""UPDATE analytics."DimensionValues" SET "ParentId"=@child WHERE "Id"=@parent""", connection);
        command.Parameters.AddWithValue("child", child.Id);
        command.Parameters.AddWithValue("parent", parent.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task ConflictingDepartmentAndCostCenterCombinationIsRejectedBeforePosting()
    {
        using var s = await Arrange();
        var department = await Definition(s, DimensionKind.Department);
        var ops = await Value(s, department, "OPS");
        var admin = await Value(s, department, "ADMIN");
        var center = await Definition(s, DimensionKind.CostCenter);
        var logistics = await Value(s, center, "LOG", department: ops.Id);
        var draft = await Journal(s, 50);
        await Attach(s, DimensionSource.Manual, draft.Id, draft.Version, [new(1, [new(department.Id, admin.Id), new(center.Id, logistics.Id)])]);
        using var invalid = await s.Owner.Client.PostAsJsonAsync($"{s.Core}/journals/{draft.Id}/post", new PostJournalRequest("invalid", draft.Version, true, "Test"));
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
        await Attach(s, DimensionSource.Manual, draft.Id, draft.Version, [new(1, [new(department.Id, ops.Id), new(center.Id, logistics.Id)])], 1);
        await Post(s, draft, "valid");
        Assert.Equal(50m, Group(await Report(s, department.Id), ops.Id).GetProperty("expense").GetDecimal());
    }
}
