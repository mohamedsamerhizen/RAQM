using RAQM.Api.Configurations;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.Interfaces.Identity;
using RAQM.Api.Interfaces.Organizations;
using RAQM.Api.Services.Organizations;
using RAQM.Api.Entities.Identity;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RAQM.Api.Helpers;
using RAQM.Api.Data;
using RAQM.Api.Entities.Organizations;

namespace RAQM.Api.Services.Identity;

public sealed class IdentityService(RaqmDbContext db, UserManager<ApplicationUser> users,
    IOptions<AuthOptions> options, IOrganizationService organizations) : IIdentityService
{
    private readonly AuthOptions auth = options.Value;
    public async Task<CurrentUserResponse> GetCurrentUser(Guid userId, CancellationToken ct) =>
        new(userId, await db.OrganizationMembers.Where(x => x.UserId == userId)
            .Select(x => new OrganizationMembershipResponse(x.OrganizationId, x.Role.ToString())).ToListAsync(ct));
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public async Task<TokenResponse> Onboard(OnboardRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.OnboardingKey) || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(request.OnboardingKey)),
            SHA256.HashData(Encoding.UTF8.GetBytes(auth.OnboardingKey))))
            throw new ApiException(403, "Onboarding is restricted.");
        OrganizationService.ValidateName(request.OrganizationName);
        if (string.IsNullOrWhiteSpace(request.Email) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(request.Email))
            throw new ApiException(400, "A valid email is required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = request.Email.Trim(), UserName = request.Email.Trim() };
        var result = await users.CreateAsync(user, request.Password ?? "");
        if (!result.Succeeded)
        {
            var status = result.Errors.Any(x => x.Code is "DuplicateUserName" or "DuplicateEmail") ? 409 : 400;
            throw new ApiException(status, "Registration rejected: " + string.Join("; ", result.Errors.Select(x => x.Description)));
        }
        await organizations.Create(user.Id, request.OrganizationName, ct);
        var tokens = await CreateSession(user, ct);
        await tx.CommitAsync(ct);
        return tokens;
    }
    public async Task<TokenResponse> Login(LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            throw new ApiException(401, "Invalid credentials or locked account.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var normalized = users.NormalizeEmail(request.Email.Trim());
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM identity.\"AspNetUsers\" WHERE \"NormalizedEmail\" = {normalized} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (user is null || await users.IsLockedOutAsync(user))
            throw new ApiException(401, "Invalid credentials or locked account.");
        if (!await users.CheckPasswordAsync(user, request.Password ?? ""))
        {
            RequireIdentityUpdate(await users.AccessFailedAsync(user));
            await tx.CommitAsync(ct);
            throw new ApiException(401, "Invalid credentials or locked account.");
        }
        RequireIdentityUpdate(await users.ResetAccessFailedCountAsync(user));
        var session = await CreateSession(user, ct);
        await tx.CommitAsync(ct);
        return session;
    }
    private static void RequireIdentityUpdate(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new ApiException(409, "Account state changed during authentication. Retry the request.", "identity_conflict");
    }
    private async Task<TokenResponse> CreateSession(ApplicationUser user, CancellationToken ct)
    {
        var session = new UserSession { UserId = user.Id, ExpiresAt = DateTimeOffset.UtcNow.AddDays(auth.RefreshDays) };
        db.Sessions.Add(session);
        var result = Issue(user.Id, session);
        await db.SaveChangesAsync(ct);
        return result;
    }
    private TokenResponse Issue(Guid userId, UserSession session)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, Hash = Hash(raw), ExpiresAt = session.ExpiresAt });
        var expires = DateTimeOffset.UtcNow.AddMinutes(auth.AccessMinutes);
        var token = new JwtSecurityToken(auth.Issuer, auth.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()), new Claim("sid", session.Id.ToString()),
             new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())],
            expires: expires.UtcDateTime, signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.SigningKey)), SecurityAlgorithms.HmacSha256));
        return new TokenResponse(new JwtSecurityTokenHandler().WriteToken(token), raw, expires);
    }
    public async Task<TokenResponse> Refresh(RefreshRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken) || request.RefreshToken.Length > 256)
            throw new ApiException(401, "Invalid refresh token.");
        var hash = Hash(request.RefreshToken);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var token = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(x => x.Hash == hash, ct)
            ?? throw new ApiException(401, "Invalid refresh token.");
        // All rotations and revocations serialize on the session row, including concurrent reuse.
        var session = await db.Sessions.FromSqlInterpolated(
            $"SELECT * FROM identity.\"Sessions\" WHERE \"Id\" = {token.SessionId} FOR UPDATE").SingleAsync(ct);
        token = await db.RefreshTokens.SingleAsync(x => x.Id == token.Id, ct);
        var now = DateTimeOffset.UtcNow;
        if (token.UsedAt != null || token.ExpiresAt <= now || session.RevokedAt != null || session.ExpiresAt <= now)
        {
            session.RevokedAt ??= now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new ApiException(401, "Refresh token expired, revoked, or reused; session revoked.");
        }
        token.UsedAt = now;
        var result = Issue(session.UserId, session);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }
    public async Task Logout(Guid userId, Guid sessionId, CancellationToken ct)
    {
        await db.Sessions.Where(x => x.Id == sessionId && x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
    }
    public async Task RevokeAll(Guid userId, CancellationToken ct)
    {
        await db.Sessions.Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
    }
}


