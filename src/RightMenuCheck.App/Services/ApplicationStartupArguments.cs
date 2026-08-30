namespace RightMenuCheck.App.Services;

public sealed record ApplicationStartupArguments(
    string? UpdateHealthPipeName,
    string? UpdateHealthToken,
    bool UpdateRolledBack)
{
    public static ApplicationStartupArguments Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? healthToken = null;
        string? healthPipeName = null;
        var rolledBack = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals("--update-health-pipe", StringComparison.Ordinal))
            {
                if (healthPipeName is not null || index + 1 >= arguments.Count ||
                    !IsValidPipeName(arguments[++index]))
                {
                    throw new ArgumentException("Update health pipe argument is invalid.");
                }

                healthPipeName = arguments[index];
            }
            else if (arguments[index].Equals("--update-health-token", StringComparison.Ordinal))
            {
                if (healthToken is not null || index + 1 >= arguments.Count ||
                    !Guid.TryParseExact(arguments[++index], "N", out _))
                {
                    throw new ArgumentException("Update health startup argument is invalid.");
                }

                healthToken = arguments[index];
            }
            else if (arguments[index].Equals("--update-rollback", StringComparison.Ordinal))
            {
                rolledBack = true;
            }
        }

        if ((healthPipeName is null) != (healthToken is null))
        {
            throw new ArgumentException("Update health arguments must be supplied together.");
        }

        return new ApplicationStartupArguments(healthPipeName, healthToken, rolledBack);
    }

    private static bool IsValidPipeName(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-');
}
