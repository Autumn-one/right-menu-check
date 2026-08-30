using System.Globalization;

namespace RightMenuCheck.Distribution;

public readonly record struct SemanticVersion(
    int Major,
    int Minor,
    int Patch,
    string? PreRelease = null,
    string? BuildMetadata = null) : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new FormatException($"'{value}' is not a valid semantic version.");
        }

        return version;
    }

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var buildParts = value.Trim().Split('+', 2);
        var versionAndPreRelease = buildParts[0].Split('-', 2);
        var coreParts = versionAndPreRelease[0].Split('.');
        if (coreParts.Length != 3 ||
            !TryParseCoreNumber(coreParts[0], out var major) ||
            !TryParseCoreNumber(coreParts[1], out var minor) ||
            !TryParseCoreNumber(coreParts[2], out var patch))
        {
            return false;
        }

        var preRelease = versionAndPreRelease.Length == 2
            ? versionAndPreRelease[1]
            : null;
        var buildMetadata = buildParts.Length == 2 ? buildParts[1] : null;
        if (!AreIdentifiersValid(preRelease, rejectNumericLeadingZeros: true) ||
            !AreIdentifiersValid(buildMetadata, rejectNumericLeadingZeros: false))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, preRelease, buildMetadata);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        return result != 0 ? result : ComparePreRelease(PreRelease, other.PreRelease);
    }

    public override string ToString()
    {
        var value = $"{Major}.{Minor}.{Patch}";
        if (PreRelease is not null)
        {
            value += $"-{PreRelease}";
        }

        return BuildMetadata is null ? value : $"{value}+{BuildMetadata}";
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) >= 0;

    private static bool TryParseCoreNumber(string value, out int number)
    {
        number = 0;
        return value.Length > 0 &&
               (value.Length == 1 || value[0] != '0') &&
               int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
               number >= 0;
    }

    private static bool AreIdentifiersValid(string? value, bool rejectNumericLeadingZeros)
    {
        if (value is null)
        {
            return true;
        }

        var identifiers = value.Split('.');
        return identifiers.All(identifier =>
            identifier.Length > 0 &&
            identifier.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character == '-') &&
            (!rejectNumericLeadingZeros ||
             !identifier.All(char.IsAsciiDigit) ||
             identifier.Length == 1 ||
             identifier[0] != '0'));
    }

    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null)
        {
            return right is null ? 0 : 1;
        }

        if (right is null)
        {
            return -1;
        }

        var leftIdentifiers = left.Split('.');
        var rightIdentifiers = right.Split('.');
        for (var index = 0; index < Math.Min(leftIdentifiers.Length, rightIdentifiers.Length); index++)
        {
            var result = CompareIdentifier(leftIdentifiers[index], rightIdentifiers[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var result = left.Length.CompareTo(right.Length);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left, right);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return StringComparer.Ordinal.Compare(left, right);
    }
}
