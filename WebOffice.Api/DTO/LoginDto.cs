using System.ComponentModel.DataAnnotations;

namespace bsckend.Models.DTOs.AuthDTOs;

public class LoginDto
{
    [Required, MinLength(3)]
    public string Login { get; set; }

    [Required, MinLength(6)]
    public string Password { get; set; }
}

