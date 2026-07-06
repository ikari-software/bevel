namespace Bevel.Core.Vfs;

public sealed record NameValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }

    public static NameValidationResult Ok => new() { IsValid = true };

    public static NameValidationResult Fail(string message) => new() { IsValid = false, ErrorMessage = message };
}
