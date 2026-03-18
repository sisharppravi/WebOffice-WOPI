using bsckend.Models.DTOs.AuthDTOs;
using bsckend.Models.User;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using bsckend.Services;

namespace bsckend.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController: ControllerBase
{
    private readonly ILogger<AuthController> _logger;
    private readonly UserManager<UserModel> _userManager;
    private readonly SignInManager<UserModel> _signInManager;
    private readonly JwtService _jwtService;
    
    public AuthController(
        ILogger<AuthController> logger, 
        UserManager<UserModel> userManager,
        SignInManager<UserModel> signInManager,
        JwtService jwtService)
    {
        _logger = logger;
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtService = jwtService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Invalid register dto: {Errors}", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return BadRequest(ModelState);
        }

        _logger.LogInformation("Registering new user with login: {Login}",  dto.Login);
        var user = new UserModel
        {
            UserName = dto.Login
        };
        var result = await _userManager.CreateAsync(user, dto.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                _logger.LogWarning(
                    "Registration failed for user {Login}. Error: {Error}",
                    dto.Login,
                    error.Description);
            }

            return BadRequest(result.Errors);
        }

        _logger.LogInformation("User {Login} successfully registered with id {UserId}", 
            dto.Login, user.Id);

        return Ok(new
        {
            message = "User created"
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Invalid login dto: {Errors}", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return BadRequest(ModelState);
        }

        _logger.LogInformation("Login attempt for user: {Login}", dto.Login);

        var user = await _userManager.FindByNameAsync(dto.Login);
        if (user == null)
        {
            _logger.LogWarning("Login failed: user {Login} not found", dto.Login);
            return Unauthorized(new { message = "Invalid login or password" });
        }

        var result = await _signInManager.CheckPasswordSignInAsync(user, dto.Password, false);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Login failed: invalid password for user {Login}", dto.Login);
            return Unauthorized(new { message = "Invalid login or password" });
        }

        var token = await _jwtService.GenerateTokenAsync(user);
        _logger.LogInformation("User {Login} successfully logged in", dto.Login);

        return Ok(new { token });
    }
    
}