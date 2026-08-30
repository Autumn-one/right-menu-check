if (args is ["--marker", var markerPath] && Path.IsPathFullyQualified(markerPath))
{
    File.WriteAllText(markerPath, "uninstall-completed");
    return 0;
}

return 2;
