using Base.Domain.Common;

namespace Base.Application.Users;

/// <summary>A user was invited. The handler emails a set-password link; the token is generated at send time, never stored.</summary>
public sealed record UserInvited(Guid UserId) : IDomainEvent;

/// <summary>A password reset was requested (by the user or an administrator). The handler emails a reset link.</summary>
public sealed record PasswordResetRequested(Guid UserId) : IDomainEvent;
