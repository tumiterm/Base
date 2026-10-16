using Base.Application.Common.Persistence;

namespace Base.UnitTests.Common;

public sealed class PagingTests
{
    [Theory]
    [InlineData(0, 10, 1, 10)]
    [InlineData(-5, 0, 1, 1)]
    [InlineData(3, 5000, 3, PageRequest.MaxPageSize)]
    public void Page_request_is_clamped(int page, int size, int expectedPage, int expectedSize)
    {
        var request = new PageRequest(page, size);

        request.Page.ShouldBe(expectedPage);
        request.PageSize.ShouldBe(expectedSize);
    }

    [Fact]
    public void Paged_result_computes_navigation()
    {
        var result = new PagedResult<int>([1, 2], Total: 45, Page: 2, PageSize: 20);

        result.TotalPages.ShouldBe(3);
        result.HasPrevious.ShouldBeTrue();
        result.HasNext.ShouldBeTrue();
    }

    private sealed class Order
    {
        public Customer Customer { get; set; } = new();
    }

    private sealed class Customer
    {
        public Address Address { get; set; } = new();
    }

    private sealed class Address;

    [Fact]
    public void Include_paths_follow_member_chains() =>
        IncludePaths.From<Order>(o => o.Customer.Address).ShouldBe("Customer.Address");

    [Fact]
    public void Include_paths_reject_non_member_expressions() =>
        Should.Throw<ArgumentException>(() => IncludePaths.From<Order>(o => o));
}
