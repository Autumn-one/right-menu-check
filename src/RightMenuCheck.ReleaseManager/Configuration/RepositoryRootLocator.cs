namespace RightMenuCheck.ReleaseManager.Configuration;

public static class RepositoryRootLocator
{
    public static string Find()
    {
        foreach (var origin in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var candidate = FindFrom(origin);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "无法定位项目根目录，请从 RightMenuCheck 仓库内启动发布管理器。");
    }

    public static string? FindFrom(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        var directory = new DirectoryInfo(Path.GetFullPath(origin));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "github-conf.json")) &&
                File.Exists(Path.Combine(directory.FullName, "scripts", "publish.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
