using Base.Application.Common.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Base.Web.Results;

public static class ValidationResponseExtensions
{
    /// <summary>Maps a failure onto the HTTP status its <see cref="ErrorType"/> implies, as ProblemDetails.</summary>
    public static IActionResult ToActionResult(this ValidationResponse response, ControllerBase controller)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(controller);
        return response.IsSuccess ? controller.NoContent() : Failure(response, controller);
    }

    public static IActionResult ToActionResult<T>(this ValidationResponse<T> response, ControllerBase controller)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(controller);
        return response.IsSuccess ? controller.Ok(response.Data) : Failure(response, controller);
    }

    /// <summary>For MVC forms: puts each error on its field so tag helpers render it next to the input.</summary>
    public static void AddToModelState(this ValidationResponse response, ModelStateDictionary modelState, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(modelState);
        foreach (var error in response.Errors)
        {
            var key = string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(error.Field) ? error.Field : $"{prefix}.{error.Field}";
            modelState.AddModelError(key, error.Message);
        }
    }

    public static int ToStatusCode(this ErrorType type) => type switch
    {
        ErrorType.None => StatusCodes.Status200OK,
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static IActionResult Failure(ValidationResponse response, ControllerBase controller)
    {
        if (response.ErrorType == ErrorType.Validation)
        {
            response.AddToModelState(controller.ModelState);
            return controller.ValidationProblem(detail: response.Message, modelStateDictionary: controller.ModelState);
        }

        return controller.Problem(detail: response.Message, statusCode: response.ErrorType.ToStatusCode());
    }
}
