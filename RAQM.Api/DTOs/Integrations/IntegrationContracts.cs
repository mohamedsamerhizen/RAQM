namespace RAQM.Api.DTOs.Integrations;

public sealed record ApplicationRequest(string Name, string[] Scopes);
public sealed record CredentialIssue(Guid Id, string Key, DateTimeOffset ExpiresAt);
public sealed record CredentialRequest(int Days = 30, Guid? Replace = null, int OverlapMinutes = 0);
public sealed record WebhookRequest(string Endpoint, string[] Events);
public sealed record WebhookSecret(Guid Id, int Version, string Secret);
public sealed record ImportRequest(int Version, string Csv);
public sealed record ImportRow(int Line, string ExternalId, string Code, string Name);
public sealed record ImportPreview(ImportRow[] Rows, string[] Errors);
public sealed record IntegrationExportScheduleRequest(int IntervalHours, int LookbackDays, DateTimeOffset StartsAt);
