using Base.Application.Tenancy;
using Base.Domain.Tenancy;

namespace Base.UnitTests.Domain;

public sealed class TenantDomainTests
{
    [Theory]
    [InlineData("mtn.base.co.za", "mtn.base.co.za")]
    [InlineData("  MTN.Base.co.za. ", "mtn.base.co.za")]
    [InlineData("https://mtn.localhost:7262", "mtn.localhost")]
    [InlineData("https://MTN.dev.localhost:7262/account/login?x=1", "mtn.dev.localhost")]
    [InlineData("http://mtn.base.co.za/", "mtn.base.co.za")]
    [InlineData("mtn.base.co.za:443", "mtn.base.co.za")]
    public void A_pasted_address_is_reduced_to_its_host_name(string input, string host) =>
        TenantDomain.Normalize(input).ShouldBe(host);

    [Theory]
    [InlineData("https://mtn.dev.localhost:7262", true)]
    [InlineData("mtn.base.co.za", true)]
    [InlineData("not a host", false)]
    [InlineData("https://", false)]
    [InlineData("mtn_base.co.za", false)]
    public void Hosts_are_checked_after_normalising(string input, bool valid) =>
        TenantRules.IsValidHost(input).ShouldBe(valid);

    [Theory]
    [InlineData("mtn.base.co.za", "https://base.co.za", "https://mtn.base.co.za")]
    [InlineData("mtn.dev.localhost", "https://localhost:7262", "https://mtn.dev.localhost:7262")]
    [InlineData("mtn.dev.localhost", "http://localhost:5016/", "http://mtn.dev.localhost:5016")]
    [InlineData("mtn.base.co.za", null, "https://mtn.base.co.za")]
    public void A_tenant_address_keeps_the_platform_address_scheme_and_port(string host, string? platform, string url) =>
        TenantAddresses.Url(host, platform).ShouldBe(url);
}
