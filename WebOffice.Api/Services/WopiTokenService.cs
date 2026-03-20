using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace bsckend.Services;

public interface IWopiTokenService
{
    string GenerateToken(string userId, string fileId, TimeSpan lifetime);
    bool TryValidateToken(string token, out WopiTokenPayload payload);
}

public sealed record WopiTokenPayload(string UserId, string FileId, DateTime ExpiresAtUtc);

public class WopiTokenService : IWopiTokenService
{
    private readonly IConfiguration _configuration;
    private readonly JwtSecurityTokenHandler _handler = new();

    public WopiTokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GenerateToken(string userId, string fileId, TimeSpan lifetime)
    {
        var expiresAt = DateTime.UtcNow.Add(lifetime);
        var claims = new List<Claim>
        {
            new("wopi_user", userId),
            new("wopi_file", fileId)
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(GetSecurityKey(), SecurityAlgorithms.HmacSha256)
        };

        return _handler.CreateEncodedJwt(descriptor);
    }

    public bool TryValidateToken(string token, out WopiTokenPayload payload)
    {
        payload = default!;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = GetSecurityKey(),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            };

            var principal = _handler.ValidateToken(token, parameters, out var validatedToken);
            if (validatedToken is not JwtSecurityToken jwtToken)
            {
                return false;
            }

            var userId = principal.FindFirst("wopi_user")?.Value;
            var fileId = principal.FindFirst("wopi_file")?.Value;
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(fileId))
            {
                return false;
            }

            payload = new WopiTokenPayload(userId, fileId, jwtToken.ValidTo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private SymmetricSecurityKey GetSecurityKey()
    {
        var secret = _configuration["Wopi:TokenSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Wopi:TokenSecret is not configured");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }
}


