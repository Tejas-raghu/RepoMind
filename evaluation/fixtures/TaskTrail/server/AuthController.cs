using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TaskTrail.Server;

public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResult(string AccessToken, string DisplayName);

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IUserAuthenticationService _authentication;
    private readonly IJwtTokenService _tokens;

    public AuthController(IUserAuthenticationService authentication, IJwtTokenService tokens)
    {
        _authentication = authentication;
        _tokens = tokens;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResult>> Login(LoginRequest request)
    {
        var user = await _authentication.CheckCredentialsAsync(request.Email, request.Password);
        if (user is null) return Unauthorized();

        var jwt = _tokens.CreateAccessToken(user);
        return Ok(new LoginResult(jwt, user.DisplayName));
    }

    [Authorize]
    [HttpGet("session")]
    public IActionResult CurrentSession()
    {
        // ASP.NET Core authentication middleware validates the incoming JWT.
        return Ok(new { Subject = User.Identity?.Name });
    }
}

// Illustrative interfaces; implementations are not provided in this teaching sample.
public interface IUserAuthenticationService
{
    Task<DemoUser?> CheckCredentialsAsync(string email, string password);
}
public interface IJwtTokenService
{
    string CreateAccessToken(DemoUser user);
}
public sealed record DemoUser(string DisplayName);
