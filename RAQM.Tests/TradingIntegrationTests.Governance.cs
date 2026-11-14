using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using Xunit;

namespace RAQM.Tests;

public sealed partial class TradingIntegrationTests
{
    [Fact]
    public async Task RecentSupplierFinancialEditorCannotApproveOrExecuteTheScopedPayment()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var editor = await TaxReviewer(s);
        using var approver = await TaxReviewer(s);
        var root = s.Root.Replace("/trading", "/governance", StringComparison.Ordinal);
        var policy = await Send<GovernancePolicy>(owner, HttpMethod.Post, root + "/policies",
            new PolicyInput(FinancialDuty.SupplierPayment, "AP-SOD", "Supplier edit restriction", s.Actor, PolicyMode.Enforce,
                DateTimeOffset.UtcNow.AddSeconds(-2), new PolicyRules([new(CurrencyPolicy.Maximum, 1)], true, false, false, 7, 24, false)));
        await Send<GovernancePolicy>(editor, HttpMethod.Post, $"{root}/policies/{policy.Id}/approve", new { });
        var supplier = await Party(s, PartyKind.Supplier);
        var bill = await Post(s, await Approve(s, await Draft(s, supplier)));
        var update = new PartyRequest(supplier.Kind, supplier.Code, supplier.Name, supplier.Email, supplier.Phone, supplier.BillingAddress,
            supplier.TaxIdentifier, supplier.PaymentTermsDays + 1, supplier.CreditLimit, supplier.ControlAccountId, supplier.DefaultAccountId);
        await Send<PartyResponse>(editor, HttpMethod.Put, $"{s.Root}/parties/{supplier.Id}", new UpdatePartyRequest(update, supplier.Version));
        var payment = new PaymentRequest(supplier.Id, s.Accounts["1100"].Id, 250, Today, "Payment source", "sod-pay",
            Allocations: [new(bill.Id, 250)]);
        var request = await Send<GovernanceAuthorization>(owner, HttpMethod.Post, root + "/authorizations",
            new AuthorizationInput(FinancialDuty.SupplierPayment, null, payment, "Supplier obligation review"));
        Assert.Equal(250m, request.Amount);
        var review = new ReviewInput(ReviewDecision.Approve, null, "Reviewed independently");
        await Denied(editor, $"{root}/authorizations/{request.Id}/review", review, HttpStatusCode.Conflict);
        await Send<GovernanceReview>(approver, HttpMethod.Post, $"{root}/authorizations/{request.Id}/review", review);
        await Denied(editor, s.Root + "/payments", payment, HttpStatusCode.Conflict);
        var result = await Send<PaymentResponse>(approver, HttpMethod.Post, s.Root + "/payments", payment);
        Assert.Equal(250m, result.Allocated);
        var after = await owner.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{bill.Id}");
        Assert.Equal(750m, after!.Outstanding);
        var report = await owner.GetFromJsonAsync<JsonElement>(root + "/overview");
        Assert.Equal(0, report.GetProperty("pendingAuthorizations").GetInt32());
    }
}
