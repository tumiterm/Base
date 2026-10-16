using Base.Application.Common.Results;

namespace Base.UnitTests.Common;

public sealed class ValidationResponseTests
{
    [Fact]
    public void Success_has_no_errors()
    {
        var response = ValidationResponse.Success("Saved");

        response.IsSuccess.ShouldBeTrue();
        response.IsError.ShouldBeFalse();
        response.ErrorType.ShouldBe(ErrorType.None);
        response.Errors.ShouldBeEmpty();
        response.Message.ShouldBe("Saved");
    }

    [Fact]
    public void Failure_carries_field_errors_and_first_message()
    {
        var response = ValidationResponse.Failure([new ValidationError("Email", "Required"), new ValidationError("Name", "Too long")]);

        response.IsError.ShouldBeTrue();
        response.ErrorType.ShouldBe(ErrorType.Validation);
        response.Message.ShouldBe("Required");
        response.Errors.Select(e => e.Field).ShouldBe(["Email", "Name"]);
    }

    [Fact]
    public void Failure_without_errors_is_rejected() =>
        Should.Throw<ArgumentException>(() => ValidationResponse.Failure(Array.Empty<ValidationError>()));

    [Fact]
    public void Typed_success_carries_data_and_implicit_conversion_works()
    {
        ValidationResponse<int> response = 42;

        response.IsSuccess.ShouldBeTrue();
        response.Data.ShouldBe(42);
        response.GetDataOrThrow().ShouldBe(42);
    }

    [Fact]
    public void As_propagates_a_failure_into_a_typed_response()
    {
        var typed = ValidationResponse.NotFound("Missing").As<string>();

        typed.IsError.ShouldBeTrue();
        typed.ErrorType.ShouldBe(ErrorType.NotFound);
        typed.Message.ShouldBe("Missing");
        Should.Throw<InvalidOperationException>(() => typed.GetDataOrThrow());
    }

    [Fact]
    public void As_refuses_to_convert_a_success() =>
        Should.Throw<InvalidOperationException>(() => ValidationResponse.Success().As<int>());

    [Fact]
    public void Error_cannot_be_created_with_type_none() =>
        Should.Throw<ArgumentException>(() => ValidationResponse.Error(ErrorType.None, "x"));
}
