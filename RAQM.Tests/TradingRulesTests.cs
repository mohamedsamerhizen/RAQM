using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.Trading;
using Xunit;

namespace RAQM.Tests;
public sealed class TradingRulesTests
{
    [Fact]
    public void ExclusiveTaxFollowsDiscountedBase()
    {
        var line = TradeRules.Calculate(2, 100, 10, 15, false, "USD");
        Assert.Equal(new CalculatedLine(200, 20, 180, 27, 207), line);
    }

    [Fact]
    public void InclusiveTaxExtractsNetAndPreservesGross()
    {
        Assert.Equal(new CalculatedLine(230, 23, 180, 27, 207), TradeRules.Calculate(2, 115, 10, 15, true, "USD"));
    }

    [Theory]
    [InlineData("USD", 1.005, 1.00)]
    [InlineData("USD", 1.015, 1.02)]
    [InlineData("IQD", 1.0005, 1.000)]
    [InlineData("IQD", 1.0015, 1.002)]
    [InlineData("JPY", 2.5, 2)]
    [InlineData("JPY", 3.5, 4)]
    public void MonetaryMidpointsUseEvenRounding(string currency, double price, double expected) => Assert.Equal((decimal)expected, TradeRules.Calculate(1, (decimal)price, 0, 0, false, currency).Total);
    [Theory]
    [InlineData(0, 1, 0, 0)]
    [InlineData(1.0000001, 1, 0, 0)]
    [InlineData(1, 1.00001, 0, 0)]
    [InlineData(1, -1, 0, 0)]
    [InlineData(1, 1, 100.01, 0)]
    [InlineData(1, 1, 0, -1)]
    [InlineData(1, 1, 0, 0.0000001)]
    public void InvalidPrecisionAndTaxPoliciesAreRejected(double q, double p, double discount, double tax) => Assert.Throws<ApiException>(() => TradeRules.Calculate((decimal)q, (decimal)p, (decimal)discount, (decimal)tax, false, "USD"));
    [Fact]
    public void PartialCreditsPreserveFinalTaxRoundingResidual()
    {
        var original = new TradeDocumentLine
        {
            Quantity = 3,
            UnitPrice = .05m,
            TaxPercent = 10,
            NetAmount = .15m,
            TaxAmount = .02m,
            Total = .17m
        };
        var first = TradeRules.Credit(original, 1, 0, 0, 0, 0, "USD");
        Assert.Equal(.05m, first.Net);
        Assert.Equal(0, first.Tax);
        var last = TradeRules.Credit(original, 2, 1, .05m, 0, 0, "USD");
        Assert.Equal(.10m, last.Net);
        Assert.Equal(.02m, last.Tax);
        Assert.Equal(.12m, last.Total);
        Assert.Throws<ApiException>(() => TradeRules.Credit(original, 3, 1, .05m, 0, 0, "USD"));
    }

    [Theory]
    [InlineData(TradeKind.Invoice, true)]
    [InlineData(TradeKind.Bill, false)]
    [InlineData(TradeKind.CustomerCredit, false)]
    [InlineData(TradeKind.SupplierCredit, true)]
    [InlineData(TradeKind.OpeningReceivable, true)]
    [InlineData(TradeKind.OpeningPayable, false)]
    public void DocumentEntriesUseIndependentDebitCreditExpectations(TradeKind kind, bool debitControl)
    {
        var control = Guid.NewGuid();
        var net = Guid.NewGuid();
        var tax = Guid.NewGuid();
        var d = new TradeDocument
        {
            Kind = kind,
            Currency = "USD",
            ControlAccountId = control,
            Total = 115,
            Lines = [new()
            {
                Number = 1,
                AccountId = net,
                NetAmount = 100,
                TaxAccountId = tax,
                TaxAmount = 15,
                Total = 115
            }

            ]
        };
        var lines = TradeRules.DocumentPosting(d);
        Assert.Equal(115, lines.Sum(x => x.Debit));
        Assert.Equal(115, lines.Sum(x => x.Credit));
        Assert.Equal(debitControl ? 115 : 0, lines.Single(x => x.AccountId == control).Debit);
        Assert.Equal(debitControl ? 100 : 0, lines.Single(x => x.AccountId == net).Credit);
        Assert.Equal(debitControl ? 15 : 0, lines.Single(x => x.AccountId == tax).Credit);
    }

    [Theory]
    [InlineData(PartyKind.Customer, false, true)]
    [InlineData(PartyKind.Supplier, false, false)]
    [InlineData(PartyKind.Customer, true, false)]
    [InlineData(PartyKind.Supplier, true, true)]
    public void PaymentsRefundsAndAdvancesDoNotRecognizeRevenue(PartyKind side, bool refund, bool debitCash)
    {
        var cash = Guid.NewGuid();
        var control = Guid.NewGuid();
        var advance = Guid.NewGuid();
        var p = new SettlementPayment
        {
            PartyKind = side,
            IsRefund = refund,
            Currency = "USD",
            CashAccountId = cash,
            ControlAccountId = control,
            AdvanceAccountId = advance,
            Amount = 1200,
            ControlAmount = 1000,
            AdvanceAmount = 200
        };
        var lines = TradeRules.PaymentPosting(p);
        Assert.Equal(1200, lines.Sum(x => x.Debit));
        Assert.Equal(1200, lines.Sum(x => x.Credit));
        Assert.Equal(debitCash ? 1200 : 0, lines.Single(x => x.AccountId == cash).Debit);
        Assert.Equal(debitCash ? 1000 : 0, lines.Single(x => x.AccountId == control).Credit);
        Assert.Equal(debitCash ? 200 : 0, lines.Single(x => x.AccountId == advance).Credit);
        Assert.Equal(3, lines.Count);
    }

    [Theory]
    [InlineData(PartyKind.Customer)]
    [InlineData(PartyKind.Supplier)]
    public void AdvanceApplicationTransfersBetweenAdvanceAndControlOnly(PartyKind side)
    {
        var a = new SettlementAllocation
        {
            PartyKind = side,
            Amount = 100,
            ControlAmount = 40,
            AdvanceAmount = 60,
            ControlAccountId = Guid.NewGuid(),
            AdvanceAccountId = Guid.NewGuid()
        };
        var lines = TradeRules.AllocationPosting(a);
        Assert.Equal(60, lines.Sum(x => x.Debit));
        Assert.Equal(60, lines.Sum(x => x.Credit));
        Assert.Equal(side == PartyKind.Customer ? 60 : 0, lines.Single(x => x.AccountId == a.AdvanceAccountId).Debit);
        a.AdvanceAmount = 0;
        Assert.Empty(TradeRules.AllocationPosting(a));
    }

    [Fact]
    public void OutstandingFundingAndCutoffsDeriveFromEventsIncludingReversal()
    {
        var day = new DateOnly(2026, 1, 1);
        var party = Guid.NewGuid();
        var invoice = new TradeDocument
        {
            Id = Guid.NewGuid(),
            PartyId = party,
            Kind = TradeKind.Invoice,
            State = TradeState.Posted,
            PostingDate = day,
            Total = 1000
        };
        var payment = new SettlementPayment
        {
            Id = Guid.NewGuid(),
            PartyId = party,
            PostingDate = day,
            State = SettlementState.Reversed,
            ReversalDate = day.AddDays(2),
            Amount = 1200,
            ControlAmount = 1000,
            AdvanceAmount = 200
        };
        var a = new SettlementAllocation
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            TargetDocumentId = invoice.Id,
            Amount = 600,
            ControlAmount = 600,
            EffectiveDate = day
        };
        var reverse = new SettlementAllocation
        {
            ReversesAllocationId = a.Id,
            PaymentId = payment.Id,
            TargetDocumentId = invoice.Id,
            Amount = 600,
            ControlAmount = 600,
            EffectiveDate = day.AddDays(2)
        };
        var s = new TradingSnapshot
        {
            Documents = [invoice],
            Payments = [payment],
            Allocations = [a, reverse]
        };
        Assert.Equal(400, s.Outstanding(invoice, day));
        Assert.Equal((400m, 200m), s.Available(FundingKind.Payment, payment.Id, day));
        Assert.Equal(0, s.ControlBalance(party, day));
        Assert.Equal(200, s.Advances(party, day));
        Assert.Equal(1000, s.Outstanding(invoice, day.AddDays(2)));
        Assert.Equal(1000, s.ControlBalance(party, day.AddDays(2)));
        Assert.Equal((0m, 0m), s.Available(FundingKind.Payment, payment.Id, day.AddDays(2)));
        Assert.Equal(0, s.Outstanding(invoice, day.AddDays(-1)));
    }

    [Fact]
    public void CreditAllocationRefundAndUnallocatedControlAreNotDoubleCounted()
    {
        var day = new DateOnly(2026, 1, 1);
        var party = Guid.NewGuid();
        var invoice = new TradeDocument
        {
            Id = Guid.NewGuid(),
            PartyId = party,
            Kind = TradeKind.Bill,
            State = TradeState.Posted,
            PostingDate = day,
            Total = 1000
        };
        var credit = new TradeDocument
        {
            Id = Guid.NewGuid(),
            PartyId = party,
            Kind = TradeKind.SupplierCredit,
            State = TradeState.Posted,
            PostingDate = day,
            Total = 200
        };
        var payment = new SettlementPayment
        {
            Id = Guid.NewGuid(),
            PartyId = party,
            PostingDate = day,
            State = SettlementState.Posted,
            Amount = 600,
            ControlAmount = 600
        };
        var refund = new SettlementPayment
        {
            IsRefund = true,
            RefundCreditId = credit.Id,
            PartyId = party,
            PostingDate = day,
            State = SettlementState.Posted,
            Amount = 50,
            ControlAmount = 50
        };
        var s = new TradingSnapshot
        {
            Documents = [invoice, credit],
            Payments = [payment, refund],
            Allocations = [new()
            {
                TargetDocumentId = invoice.Id,
                CreditDocumentId = credit.Id,
                EffectiveDate = day,
                Amount = 100,
                ControlAmount = 100
            }, new()
            {
                TargetDocumentId = invoice.Id,
                PaymentId = payment.Id,
                EffectiveDate = day,
                Amount = 400,
                ControlAmount = 400
            }

            ]
        };
        Assert.Equal(500, s.Outstanding(invoice, day));
        Assert.Equal(50, s.UnappliedCredits(party, day));
        Assert.Equal(200, s.UnallocatedControl(party, day));
        Assert.Equal(250, s.ControlBalance(party, day));
    }

    [Theory]
    [InlineData(-1, "Current")]
    [InlineData(0, "Current")]
    [InlineData(1, "1-30")]
    [InlineData(30, "1-30")]
    [InlineData(31, "31-60")]
    [InlineData(60, "31-60")]
    [InlineData(61, "61-90")]
    [InlineData(90, "61-90")]
    [InlineData(91, "90+")]
    public void AgingBoundariesAreExplicit(int days, string bucket) => Assert.Equal(bucket, TradeRules.AgingBucket(days));
    [Fact]
    public void IdempotencyHashIgnoresDecimalStorageScaleButDetectsValueChanges()
    {
        Assert.Equal(TradeRules.Hash(new { Amount = 1.0m }), TradeRules.Hash(new { Amount = 1.0000m }));
        Assert.NotEqual(TradeRules.Hash(new { Amount = 1m }), TradeRules.Hash(new { Amount = 1.01m }));
    }
}
