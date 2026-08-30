namespace RightMenuCheck.Core.Inventory;

public static class ClsidUtilities
{
    public static string? Normalize(string? value)
    {
        if (value is null || !Guid.TryParse(value.Trim(), out var guid))
        {
            return null;
        }

        return guid.ToString("B").ToUpperInvariant();
    }
}
