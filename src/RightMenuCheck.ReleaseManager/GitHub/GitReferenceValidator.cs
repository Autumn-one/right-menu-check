namespace RightMenuCheck.ReleaseManager.GitHub;

internal static class GitReferenceValidator
{
    public static string ValidateTag(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var tag = value.Trim();
        if (tag.StartsWith('/') ||
            tag.EndsWith('/') ||
            tag.EndsWith('.') ||
            tag.Contains("..", StringComparison.Ordinal) ||
            tag.Contains("@{", StringComparison.Ordinal) ||
            tag.Contains("//", StringComparison.Ordinal) ||
            tag.Any(static character => character is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\') ||
            tag.Any(char.IsControl))
        {
            throw new ArgumentException("Git tag 格式无效。", nameof(value));
        }

        return tag;
    }
}
