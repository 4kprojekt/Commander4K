namespace Commander4K.Plugins.Tests;

internal static class TestFolder
{
    /// <summary>
    /// Deletes a test folder that a plugin process ran in. A killed process (or the real interpreter behind the
    /// <c>py</c>/Store launcher, or a virus scan of files just written) can hold the folder for a moment after
    /// the plugin is disposed, so a sharing violation is retried for up to ~3 s.
    /// </summary>
    public static async Task DeleteAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 30)
            {
                await Task.Delay(100);
            }
        }
    }
}
