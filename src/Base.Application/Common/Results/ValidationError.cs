namespace Base.Application.Common.Results;

/// <summary>
/// One problem with the input. <see cref="Field"/> maps straight onto <c>ModelState.AddModelError(field, message)</c>;
/// an empty field means the error applies to the whole request.
/// </summary>
public sealed record ValidationError(string Field, string Message, string? Code = null)
{
    public static ValidationError General(string message, string? code = null) => new(string.Empty, message, code);
}
