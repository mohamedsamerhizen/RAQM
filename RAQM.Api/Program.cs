using System.Security.Claims;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using RAQM.Api.Services.ApiAccess;
using RAQM.Api.Services.Automation;
using RAQM.Api.Services.DataExchange;
using RAQM.Api.Services.EventProcessing;
using RAQM.Api.Services.Integrations;
using RAQM.Api.Services.Webhooks;
using RAQM.Api.Services.SaaS;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Configurations;
using RAQM.Api.Filters;
using RAQM.Api.Interfaces.Identity;
using RAQM.Api.Interfaces.Organizations;
using RAQM.Api.Middleware;
using RAQM.Api.Services.Identity;
using RAQM.Api.Services.Organizations;
using RAQM.Api.Services.Health;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.Trading;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
    c.ProblemDetails.Extensions["correlationId"] = c.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section).ValidateDataAnnotations()
    .Validate(x => Encoding.UTF8.GetByteCount(x.SigningKey) >= 32, "Signing key must be at least 256 bits.").ValidateOnStart();
builder.Services.AddOptions<DatabaseOptions>().BindConfiguration("ConnectionStrings").ValidateDataAnnotations().Validate(x => { try { return !string.IsNullOrWhiteSpace(new NpgsqlConnectionStringBuilder(x.Database).Host); } catch { return false; } }, "A valid PostgreSQL connection is required.").ValidateOnStart();


builder.Services.AddDbContext<RaqmDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Database));
builder.Services.AddIdentityCore<ApplicationUser>(o =>
{
    o.User.RequireUniqueEmail = true; o.Password.RequiredLength = 12;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15); o.Lockout.MaxFailedAccessAttempts = 5;
}).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<RaqmDbContext>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer()
    .AddScheme<AuthenticationSchemeOptions, MachineAuthentication>(MachineIdentity.Scheme, _ => { });
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>>((jwt, options) =>
    {
        var auth = options.Value;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = auth.Issuer,
            ValidateAudience = true,
            ValidAudience = auth.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(15),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        };
        jwt.Events = new JwtBearerEvents
        {
            OnTokenValidated = async c =>
            {
                if (!Guid.TryParse(c.Principal?.FindFirstValue("sid"), out var sessionId) ||
                    !Guid.TryParse(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) { c.Fail("Invalid session."); return; }
                var db = c.HttpContext.RequestServices.GetRequiredService<RaqmDbContext>();
                if (!await db.Sessions.AsNoTracking().AnyAsync(x => x.Id == sessionId && x.UserId == userId &&
                    x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, c.HttpContext.RequestAborted))
                    c.Fail("Session revoked.");
            }
        };
    });
builder.Services.AddAuthorization();
var trustedProxies = builder.Configuration.GetSection("Ingress:TrustedProxies").GetChildren()
    .Select(x => IPAddress.TryParse(x.Value, out var address) ? address :
        throw new InvalidOperationException("Ingress trusted proxies must be literal IP addresses.")).ToArray();
if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in trustedProxies) options.KnownProxies.Add(proxy);
    });
}
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(
        c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("Auth:RequestsPerMinute", 10), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddConcurrencyLimiter("financial-reports", options =>
    {
        options.PermitLimit = 4;
        options.QueueLimit = 0;
    });
    o.AddPolicy("integration", c => RateLimitPartition.GetFixedWindowLimiter(
        c.User.FindFirst("integration_app")?.Value ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Clamp(builder.Configuration.GetValue("Integrations:AppRequestsPerMinute", 60), 1, 10000),
            Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
    o.OnRejected = async (c, ct) =>
    {
        c.HttpContext.Response.Headers.RetryAfter = "60";
        var machine = c.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == "integration";
        await Results.Problem(statusCode: 429, title: machine ? "Integration rate limit exceeded." : "Authentication rate limit exceeded.").ExecuteAsync(c.HttpContext);
    };
});
builder.Services.AddScoped<IIdentityService, IdentityService>();
builder.Services.AddScoped<IOrganizationService, OrganizationService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PlatformAuthority>();
builder.Services.AddScoped<CommercialService>();
builder.Services.AddHostedService<CommercialWorker>();
var migrationOnly = args.Contains("--migrate", StringComparer.Ordinal);
WebhookKeyRing.Configure(builder.Services, builder.Configuration, !migrationOnly && !builder.Environment.IsDevelopment() &&
    (builder.Configuration.GetValue<bool>("Integrations:WorkerEnabled") ||
     builder.Configuration.GetSection("Integrations:WebhookHosts").GetChildren().Any()));
builder.Services.AddSingleton<IntegrationIngressLimits>();
builder.Services.AddSingleton<IntegrationWorkerStatus>();
builder.Services.AddHostedService<IntegrationWorker>();
builder.Services.AddScoped<IntegrationAdministration>();
builder.Services.AddScoped<IntegrationOperations>();
builder.Services.AddScoped<DeliveryDispatcher>();
builder.Services.AddScoped<WebhookTransport>();
builder.Services.AddScoped<LedgerExportJobs>();
builder.Services.AddScoped<MasterDataExchange>();
builder.Services.AddSingleton<IMasterDataAdapter, CustomerCsvAdapter>();
builder.Services.AddScoped<FinancialAccess>();
builder.Services.AddScoped<RAQM.Api.Services.ApprovalGovernance.GovernanceGate>();
builder.Services.AddScoped<RAQM.Api.Services.ApprovalGovernance.GovernanceSources>();
builder.Services.AddScoped<RAQM.Api.Services.ApprovalGovernance.GovernanceApprovalService>();
builder.Services.AddScoped<RAQM.Api.Services.Governance.GovernanceAdministration>();
builder.Services.AddScoped<RAQM.Api.Services.AuditEvidence.GovernanceAudit>();
builder.Services.AddScoped<RAQM.Api.Services.Governance.GovernanceReporting>();
builder.Services.AddScoped<RAQM.Api.Services.InternalControls.ControlService>();
builder.Services.AddScoped<RAQM.Api.Services.InternalAudit.InternalAuditService>();
builder.Services.AddScoped<FinancialTransaction>();
builder.Services.AddScoped<RAQM.Api.Services.CorporateGroups.CorporateGroupService>();
builder.Services.AddScoped<RAQM.Api.Services.Intercompany.IntercompanyService>();
builder.Services.AddScoped<RAQM.Api.Services.Intercompany.IntercompanyPostingPolicy>();
builder.Services.AddScoped<RAQM.Api.Services.CurrencyTranslation.GroupRateService>();
builder.Services.AddScoped<RAQM.Api.Services.Consolidation.ConsolidationService>();
builder.Services.AddScoped<RAQM.Api.Services.GroupReporting.GroupReportingService>();
builder.Services.AddScoped<RAQM.Api.Services.TaxJurisdictions.TaxConfigurationService>();
builder.Services.AddScoped<RAQM.Api.Services.TaxCalculation.TaxCalculationService>();
builder.Services.AddScoped<RAQM.Api.Services.TaxAccounting.TaxOperationService>();
builder.Services.AddScoped<RAQM.Api.Services.TaxAccounting.TaxPostingPolicy>();
builder.Services.AddScoped<RAQM.Api.Services.TaxReporting.TaxReportingService>();
builder.Services.AddScoped<RAQM.Api.Services.RegulatoryDocuments.TaxInvoiceService>();
builder.Services.AddScoped<RAQM.Api.Interfaces.RegulatoryDocuments.ITaxInvoiceSchema, RAQM.Api.Services.RegulatoryDocuments.RaqmInvoiceSchema>();
builder.Services.AddScoped<IFinancialTransaction>(sp=>sp.GetRequiredService<FinancialTransaction>());
builder.Services.AddScoped<SubledgerPostingPolicy>();
builder.Services.AddScoped<RAQM.Api.Services.FixedAssets.AssetPortfolio>();
builder.Services.AddScoped<RAQM.Api.Services.FixedAssets.AssetRegisterService>();
builder.Services.AddScoped<RAQM.Api.Interfaces.FixedAssets.IAssetRegister>(sp => sp.GetRequiredService<RAQM.Api.Services.FixedAssets.AssetRegisterService>());
builder.Services.AddScoped<RAQM.Api.Services.FixedAssets.AssetPlanningService>();
builder.Services.AddScoped<RAQM.Api.Services.FixedAssets.AssetCloseCheck>();
builder.Services.AddScoped<RAQM.Api.Services.FixedAssets.AssetPostingPolicy>();
builder.Services.AddScoped<RAQM.Api.Interfaces.FixedAssets.IAssetWorkflow, RAQM.Api.Services.FixedAssets.AssetWorkflow>();
builder.Services.AddScoped<RAQM.Api.Services.Inventory.InventoryRegister>();
builder.Services.AddScoped<RAQM.Api.Services.Inventory.ProcurementService>();
builder.Services.AddScoped<RAQM.Api.Services.Inventory.StockPlanning>();
builder.Services.AddScoped<RAQM.Api.Services.Inventory.StockWorkflow>();
builder.Services.AddScoped<RAQM.Api.Services.Inventory.InventoryPostingPolicy>();
builder.Services.AddScoped<RAQM.Api.Services.Inventory.InventoryReporting>();
builder.Services.AddScoped<RAQM.Api.Services.BudgetControl.BudgetControlService>();
builder.Services.AddScoped<RAQM.Api.Services.Budgeting.BudgetService>();
builder.Services.AddScoped<RAQM.Api.Services.FinancialAnalytics.BudgetReporting>();
builder.Services.AddScoped<RAQM.Api.Services.Forecasting.CashForecastSource>();
builder.Services.AddScoped<RAQM.Api.Services.Forecasting.ForecastService>();
builder.Services.AddScoped<RAQM.Api.Services.MultiCurrency.FxQueries>();
builder.Services.AddScoped<RAQM.Api.Services.MultiCurrency.FxSetup>();
builder.Services.AddScoped<RAQM.Api.Services.ExchangeRates.ExchangeRateService>();
builder.Services.AddScoped<RAQM.Api.Services.ForeignExchange.FxPlanning>();
builder.Services.AddScoped<RAQM.Api.Services.ForeignExchange.FxWorkflow>();
builder.Services.AddScoped<RAQM.Api.Services.ForeignExchange.FxPostingPolicy>();
builder.Services.AddScoped<RAQM.Api.Services.MultiCurrency.FxReporting>();
builder.Services.AddScoped<RAQM.Api.Services.FinancialDimensions.DimensionRegister>();
builder.Services.AddScoped<RAQM.Api.Services.FinancialDimensions.DimensionBindings>();
builder.Services.AddScoped<RAQM.Api.Services.FinancialDimensions.DimensionPosting>();
builder.Services.AddScoped<RAQM.Api.Services.FinancialDimensions.DimensionBudgetService>();
builder.Services.AddScoped<RAQM.Api.Services.ProjectAccounting.ProjectService>();
builder.Services.AddScoped<RAQM.Api.Services.CostAccounting.CostAllocationService>();
builder.Services.AddScoped<RAQM.Api.Services.AnalyticalReporting.DimensionReporting>();
builder.Services.AddScoped<RAQM.Api.Services.FinancialClose.ClosePostingPolicy>();
builder.Services.AddScoped<RAQM.Api.Services.FinancialReporting.FinancialLedger>();
builder.Services.AddScoped<RAQM.Api.Services.FinancialReporting.FinancialReportService>();
builder.Services.AddScoped<RAQM.Api.Interfaces.FinancialReporting.IFinancialReportService>(sp =>
    sp.GetRequiredService<RAQM.Api.Services.FinancialReporting.FinancialReportService>());
builder.Services.AddScoped<RAQM.Api.Services.FinancialClose.CloseReadinessService>();
builder.Services.AddScoped<RAQM.Api.Interfaces.FinancialClose.IFinancialCloseService, RAQM.Api.Services.FinancialClose.FinancialCloseService>();
builder.Services.AddScoped<RAQM.Api.Interfaces.Accruals.IRecognitionService, RAQM.Api.Services.Accruals.RecognitionService>();
builder.Services.AddScoped<RAQM.Api.Services.CashManagement.TreasuryPostingPolicy>();
builder.Services.AddScoped<RAQM.Api.Services.Banking.BankingPolicy>();
builder.Services.AddScoped<RAQM.Api.Services.Treasury.TreasuryLedger>();
builder.Services.AddScoped<RAQM.Api.Interfaces.Banking.IBankAccountService, RAQM.Api.Services.Banking.BankAccountService>();
builder.Services.AddScoped<RAQM.Api.Interfaces.Banking.IStatementService, RAQM.Api.Services.Banking.StatementService>();
builder.Services.AddScoped<RAQM.Api.Interfaces.Banking.IReconciliationService, RAQM.Api.Services.Banking.ReconciliationService>();
builder.Services.AddScoped<RAQM.Api.Interfaces.CashManagement.ICashManagementService, RAQM.Api.Services.CashManagement.CashManagementService>();
builder.Services.AddScoped<RAQM.Api.Interfaces.Treasury.ITreasuryReportService, RAQM.Api.Services.Treasury.TreasuryReportService>();
builder.Services.AddScoped<TradingPolicy>();
builder.Services.AddScoped<ITradingSetupService,TradingSetupService>();
builder.Services.AddScoped<ISettlementService,SettlementService>();
builder.Services.AddScoped<ITradeDocumentService,TradeDocumentService>();
builder.Services.AddScoped<ITradingReportService,TradingReportService>();
builder.Services.AddScoped<PostingValidator>();
builder.Services.AddScoped<IPostingEngine, PostingEngine>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IFiscalPeriodService, FiscalPeriodService>();
builder.Services.AddScoped<JournalService>();
builder.Services.AddScoped<IJournalService>(sp => sp.GetRequiredService<JournalService>());
builder.Services.AddScoped<IOpeningBalanceService, OpeningBalanceService>();
builder.Services.AddScoped<ILedgerService, LedgerService>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgresql", tags: ["ready"]);
builder.Services.AddControllers(options =>
{
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
    options.Filters.Add<RequestValidationFilter>();
}).ConfigureApiBehaviorOptions(options =>
{
    options.SuppressModelStateInvalidFilter = true;
    options.SuppressMapClientErrors = true;
});
builder.Services.AddOpenApi(options =>
{
    options.AddOperationTransformer<ControllerContractOpenApiTransformer>();
    options.AddDocumentTransformer<ControllerContractOpenApiTransformer>();
});
var app = builder.Build();
if (migrationOnly)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().Database.MigrateAsync();
    return;
}
if (!string.IsNullOrWhiteSpace(app.Configuration["WebhookKeys:Directory"]))
{
    var probe = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RAQM.Webhook.KeyRing.Startup");
    _ = probe.Unprotect(probe.Protect("startup"));
}
if (trustedProxies.Length > 0) app.UseForwardedHeaders();
app.UseMiddleware<RequestCorrelationMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages(async c =>
{
    await Results.Problem(statusCode: c.HttpContext.Response.StatusCode).ExecuteAsync(c.HttpContext);
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseRouting();
app.UseAuthentication();
app.UseWhen(c => c.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == "integration",
    branch => branch.UseMiddleware<IntegrationIngress>());
app.UseAuthorization();
app.UseRateLimiter();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapControllers();
app.Run();
public partial class Program { }



