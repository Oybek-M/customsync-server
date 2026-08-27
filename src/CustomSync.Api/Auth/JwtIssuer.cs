using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CustomSync.Services;
using Microsoft.IdentityModel.Tokens;

namespace CustomSync.Api.Auth;

public class JwtIssuer(IConfiguration config, SettingsService settings)
{
    public async Task<(string Token, DateTime ExpiresAt)> IssueAsync(
        string deviceId, string role, CancellationToken ct = default)
    {
        var minutes  = await settings.GetIntAsync("auth.jwt_lifetime_minutes", ct);
        var expires  = DateTime.UtcNow.AddMinutes(minutes);
        var key      = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!));

        var token = new JwtSecurityToken(
            issuer:             config["Jwt:Issuer"],
            audience:           config["Jwt:Audience"],
            claims:             [
                new Claim(ClaimTypes.NameIdentifier, deviceId),
                new Claim(ClaimTypes.Role, role)
            ],
            expires:            expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
