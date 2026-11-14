using System.ComponentModel.DataAnnotations;
namespace RAQM.Api.Configurations;
public sealed class AuthOptions
{
    public const string Section = "Auth";
    [Required, MinLength(32)] public string SigningKey { get; set; } = "";
    [Required] public string Issuer { get; set; } = "RAQM";
    [Required] public string Audience { get; set; } = "RAQM.Client";
    [Range(1, 1000)] public int RequestsPerMinute { get; set; } = 10;
    [Range(1, 30)] public int AccessMinutes { get; set; } = 10;
    [Range(1, 30)] public int RefreshDays { get; set; } = 14;
    [Required, MinLength(32)] public string OnboardingKey { get; set; } = "";
}

