namespace Base.Web.ViewModels;

public sealed record ErrorViewModel(int StatusCode, string? Ticket)
{
    public string Title => StatusCode switch
    {
        404 => "Page not found",
        403 => "Access denied",
        429 => "Too many requests",
        >= 500 => "Something went wrong",
        _ => "Request failed",
    };
}
