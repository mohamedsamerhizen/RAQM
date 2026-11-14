namespace RAQM.Api.DTOs.Identity;
public sealed record OnboardRequest(string Email, string Password, string OrganizationName, string OnboardingKey);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt);



