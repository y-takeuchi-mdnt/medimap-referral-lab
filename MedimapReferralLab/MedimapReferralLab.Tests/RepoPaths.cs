namespace MedimapReferralLab.Tests;

internal static class RepoPaths
{
    /// <summary>MedimapReferralLab.slnx のあるディレクトリ。data/ はここにある。</summary>
    public static string FindSolutionRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MedimapReferralLab.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("MedimapReferralLab.slnx が見つからない");
    }

    public static string CatalogDirectory(string root) => Path.Combine(root, "data", "catalog");
    public static string CasesFile(string root) => Path.Combine(root, "data", "cases", "架空症例.json");
}
