using System.ComponentModel.DataAnnotations;
namespace RAQM.Api.Configurations;

public sealed class DatabaseOptions
{
    [Required] public string Database { get; set; } = "";
}


