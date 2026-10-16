using Base.Application.Common.Results;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>One-request flash messages for the post/redirect/get pattern (rendered by the layout).</summary>
internal static class StatusMessage
{
    public const string SuccessKey = "StatusSuccess";
    public const string ErrorKey = "StatusError";

    public static void Flash(this Controller controller, ValidationResponse response)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(response);
        controller.TempData[response.IsSuccess ? SuccessKey : ErrorKey] = response.Message;
    }
}
