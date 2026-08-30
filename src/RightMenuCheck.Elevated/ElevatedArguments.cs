namespace RightMenuCheck.Elevated;

internal sealed record ElevatedArguments(string PipeName, string Nonce)
{
    public static bool TryParse(string[] args, out ElevatedArguments? result)
    {
        result = null;
        if (args.Length != 4)
        {
            return false;
        }

        string? pipeName = null;
        string? nonce = null;
        for (var index = 0; index < args.Length; index += 2)
        {
            switch (args[index])
            {
                case "--pipe":
                    pipeName = args[index + 1];
                    break;
                case "--nonce":
                    nonce = args[index + 1];
                    break;
                default:
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200 ||
            pipeName.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_') ||
            string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        result = new ElevatedArguments(pipeName, nonce);
        return true;
    }
}
