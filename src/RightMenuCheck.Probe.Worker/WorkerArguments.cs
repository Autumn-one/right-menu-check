namespace RightMenuCheck.Probe.Worker;

internal sealed record WorkerArguments(string PipeName, string Nonce)
{
    public static bool TryParse(string[] args, out WorkerArguments? result)
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
            var value = args[index + 1];
            switch (args[index])
            {
                case "--pipe":
                    pipeName = value;
                    break;
                case "--nonce":
                    nonce = value;
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

        result = new WorkerArguments(pipeName, nonce);
        return true;
    }
}
