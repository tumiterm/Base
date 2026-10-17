using Base.Domain.Common;

namespace Base.Application.Users;

/// <summary>A user was invited. The handler emails a set-password link; the token is generated at send time, never stored.</summary>
/// <param name="Welcome">A new organisation's first administrator: the welcome email (what to do next) instead of the plain invitation.</param>
public sealed record UserInvited(Guid UserId, bool Welcome = false) : IDomainEvent;

/// <summary>A password reset was requested (by the user or an administrator). The handler emails a reset link.</summary>
public sealed record PasswordResetRequested(Guid UserId) : IDomainEvent;
