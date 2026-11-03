namespace Angelito.ScreenStudio.Tests;

/// <summary>Temp folders for tests (they hold rendered videos); all of them are deleted when the test run ends.</summary>
internal static class TempDir
{
    private static readonly List<string> Created = [];

    static TempDir() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        lock (Created)
            foreach (var d in Created)
                try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    };

    public static DirectoryInfo Create(string prefix)
    {
        var dir = Directory.CreateTempSubdirectory(prefix);
        lock (Created) Created.Add(dir.FullName);
        return dir;
    }
}
