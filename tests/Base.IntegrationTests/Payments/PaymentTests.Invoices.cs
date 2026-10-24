using System.Text;
using Base.Application.Common.Results;
using Base.Application.Documents.Generation;
using Base.Application.Payments;
using Base.Application.Settings;
using Base.Application.Tenancy;
using Base.Domain.Notifications;
using Base.Domain.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Payments;

/// <summary>Invoices: issued once per paid payment, frozen, previewed and emailed on request.</summary>
public sealed partial class PaymentTests
{
    [Fact]
    public async Task A_paid_payment_gets_one_frozen_invoice_that_can_be_previewed_and_emailed_anywhere()
    {
        var first = await PaidAsync(new PayRequest(_starter, BillingCycle.Monthly, "Test"), "i-1");
        var invoice = (await Invoices(s => s.ForPaymentAsync(first))).GetDataOrThrow();
        invoice.ShouldSatisfyAllConditions(
            i => i.Number.ShouldMatch(@"^INV-\d{6}$"),
            i => i.Title.ShouldBe("Invoice", "not VAT registered"),
            i => i.Total.ShouldBe(100m),
            i => i.VatAmount.ShouldBe(0m),
            i => i.BuyerName.ShouldBe("Acme"),
            i => i.BuyerEmail.ShouldBe("owner@acme.test"));
        (await Invoices(s => s.ForPaymentAsync(first))).GetDataOrThrow().Number.ShouldBe(invoice.Number, "issued once");

        var html = Encoding.UTF8.GetString((await Invoices(s => s.RenderAsync(invoice.Number, DocumentFormats.Html))).GetDataOrThrow().Content);
        html.ShouldContain(invoice.Number);
        html.ShouldContain("ZAR 100.00");
        html.ShouldContain("VAT (not VAT registered)");

        // Registering for VAT makes later invoices tax invoices; the one already issued doesn't change.
        await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var settings = sp.GetRequiredService<IApplicationSettingService>();
            (await settings.SetAsync(SettingCodes.Invoices.VatNumber, "4123456789")).IsSuccess.ShouldBeTrue();
            return await settings.SetAsync(SettingCodes.Invoices.SellerName, "Base Platform (Pty) Ltd");
        });
        var secondPayment = await PaidAsync(new PayRequest(_starter, BillingCycle.Monthly, "Test"), "i-2");
        var second = (await Invoices(s => s.ForPaymentAsync(secondPayment))).GetDataOrThrow();
        second.ShouldSatisfyAllConditions(
            i => i.Title.ShouldBe("Tax invoice"),
            i => i.VatAmount.ShouldBe(13.04m, "15% included in R100"),
            i => i.Number.ShouldBe($"INV-{long.Parse(invoice.Number[4..], System.Globalization.CultureInfo.InvariantCulture) + 1:D6}", "numbered in sequence, without gaps"));
        (await Invoices(s => s.GetAsync(invoice.Number))).GetDataOrThrow().Title.ShouldBe("Invoice");
        Encoding.UTF8.GetString((await Invoices(s => s.RenderAsync(second.Number, DocumentFormats.Html))).GetDataOrThrow().Content)
            .ShouldSatisfyAllConditions(h => h.ShouldContain("VAT number 4123456789"), h => h.ShouldContain("Base Platform (Pty) Ltd"), h => h.ShouldContain("VAT (15%)"));

        _app.Sent.Clear();
        (await Invoices(s => s.SendAsync(invoice.Number, "not an address"))).Errors.ShouldHaveSingleItem().Field.ShouldBe("email");
        (await Invoices(s => s.SendAsync(invoice.Number, "accounts@acme.test"))).IsSuccess.ShouldBeTrue();
        await DeliverAsync();
        var mail = _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem();
        mail.ShouldSatisfyAllConditions(m => m.To.ShouldBe("accounts@acme.test"), m => m.Subject!.ShouldStartWith("Invoice " + invoice.Number));
        (await Invoices(s => s.GetAsync(invoice.Number))).GetDataOrThrow().LastSentTo.ShouldBe("accounts@acme.test");
        (await _app.InScopeAsync(_tenant, sp => sp.GetRequiredService<IBillingHistory>().ListMineAsync())).GetDataOrThrow().Items
            .ShouldContain(e => e.Kind == Base.Domain.Payments.BillingEventKind.InvoiceSent && e.Detail == invoice.Number + " to accounts@acme.test");

        // Nobody can fill in the invoice template by hand.
        (await _app.InScopeAsync(_tenant, sp => sp.GetRequiredService<IDocumentGenerator>().RenderAsync(new DocumentGenerationRequest(DocumentTemplates.Invoice, DocumentFormats.Html, null))))
            .ErrorType.ShouldBe(ErrorType.Validation);
        DocumentTemplateCatalog.ForPeople.ShouldNotContain(t => t.Code == DocumentTemplates.Invoice);
    }

    [Fact]
    public async Task Invoices_are_made_out_to_the_billing_details_the_organisation_keeps_and_keep_them()
    {
        _actor.Permissions.Remove(Base.Application.Security.PermissionCodes.Subscription.Pay);
        (await Billing(s => s.SaveMineAsync(new BillingDetails(LegalName: "Nope")))).ErrorType.ShouldBe(ErrorType.Forbidden);
        _actor.Permissions.Add(Base.Application.Security.PermissionCodes.Subscription.Pay);

        (await Billing(s => s.SaveMineAsync(new BillingDetails(Phone: "call me")))).Errors.ShouldHaveSingleItem().Field.ShouldBe("Phone");
        (await Billing(s => s.SaveMineAsync(new BillingDetails(
            "Acme Trading (Pty) Ltd", "Ada Accounts", "accounts@acme.test", "+27 21 555 0100", "1 Main Road\r\nCape Town\n8001", "4999999999", "2020/123456/07"))))
            .IsSuccess.ShouldBeTrue();
        var kept = (await Billing(s => s.GetMineAsync())).GetDataOrThrow();
        kept.ShouldSatisfyAllConditions(d => d.Address.ShouldBe("1 Main Road\nCape Town\n8001"), d => d.Email.ShouldBe("accounts@acme.test"));

        // The organisation names its own application; the seller is still the platform.
        (await _app.InScopeAsync(_tenant, sp => sp.GetRequiredService<IApplicationSettingService>().SetAsync(SettingCodes.General.ApplicationName, "Acme Portal"))).IsSuccess.ShouldBeTrue();
        var paid = await PaidAsync(new PayRequest(_starter, BillingCycle.Monthly, "Test"), "b-1");
        var invoice = (await Invoices(s => s.ForPaymentAsync(paid))).GetDataOrThrow();
        invoice.ShouldSatisfyAllConditions(i => i.BuyerName.ShouldBe("Acme Trading (Pty) Ltd"), i => i.BuyerEmail.ShouldBe("accounts@acme.test"));
        async Task<string> HtmlAsync() => Encoding.UTF8.GetString((await Invoices(s => s.RenderAsync(invoice.Number, DocumentFormats.Html))).GetDataOrThrow().Content);
        (await HtmlAsync()).ShouldSatisfyAllConditions(
            h => h.ShouldContain("Attention: Ada Accounts"),
            h => h.ShouldContain("1 Main Road\nCape Town"),
            h => h.ShouldContain("+27 21 555 0100"),
            h => h.ShouldContain("VAT number 4999999999"),
            h => h.ShouldContain("Registration 2020/123456/07"),
            h => h.ShouldContain("class=\"logo is-empty\""),
            h => h.ShouldNotContain("Acme Portal"),
            h => h.ShouldContain("<div class=\"seller\">Base</div>"));

        // Changed later, from the platform: new invoices use the change, the one issued keeps what it had.
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<ITenantAdministration>().UpdateAsync(
            _tenant, new UpdateTenantRequest("Acme", "billing@acme.test", new BillingDetails(Address: "2 New Street")))))
            .IsSuccess.ShouldBeTrue();
        (await HtmlAsync()).ShouldSatisfyAllConditions(h => h.ShouldContain("Ada Accounts"), h => h.ShouldNotContain("2 New Street"));
        (await Billing(s => s.GetMineAsync())).GetDataOrThrow().ShouldBe(new BillingDetails(Email: "billing@acme.test", Address: "2 New Street"));
        var next = await PaidAsync(new PayRequest(_starter, BillingCycle.Monthly, "Test"), "b-2");
        (await Invoices(s => s.ForPaymentAsync(next))).GetDataOrThrow().ShouldSatisfyAllConditions(i => i.BuyerName.ShouldBe("Acme"), i => i.BuyerEmail.ShouldBe("billing@acme.test"));
    }

    private Task<T> Billing<T>(Func<IBillingDetailsService, Task<T>> work) =>
        _app.InScopeAsync(_tenant, sp => work(sp.GetRequiredService<IBillingDetailsService>()));

    private Task<T> Invoices<T>(Func<IInvoiceService, Task<T>> work) =>
        _app.InScopeAsync(_tenant, sp => work(sp.GetRequiredService<IInvoiceService>()));
}
