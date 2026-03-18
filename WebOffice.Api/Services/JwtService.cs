using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using bsckend.Models.User;

namespace bsckend.Services;

public class JwtService
{
    private readonly IConfiguration _cfg;
    private readonly UserManager<UserModel> _userManager;

    public JwtService(IConfiguration cfg, UserManager<UserModel> userManager)
    {
        _cfg = cfg;
        _userManager = userManager;
    }

    public async Task<string> GenerateTokenAsync(UserModel user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_cfg["Jwt:Key"]));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Email, user.Email ?? ""),
            new Claim("UserName", user.UserName ?? "")
        };

        var userRoles = await _userManager.GetRolesAsync(user);
        foreach (var role in userRoles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var token = new JwtSecurityToken(
            issuer: _cfg["Jwt:Issuer"],
            audience: _cfg["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(int.Parse(_cfg["Jwt:ExpiryMinutes"] ?? "60")),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}