namespace NeytrixAI.Infrastructure.Auth;

/// <summary>
/// The verified identity extracted from a valid Clerk session token. This is the
/// ONLY thing a successful verification produces — it carries no authority of its
/// own beyond "this Clerk user id was proven". Guardian resolution, tenant
/// scoping and every downstream gate still apply exactly as before.
/// </summary>
/// <param name="UserId">Clerk user id (the token's <c>sub</c> claim). Never null/empty on a verified identity.</param>
/// <param name="Email">Primary email address, if the token included one.</param>
/// <param name="FirstName">Given name, if the token included one.</param>
/// <param name="LastName">Family name, if the token included one.</param>
public sealed record ClerkIdentity(
    string UserId,
    string? Email,
    string? FirstName,
    string? LastName);
