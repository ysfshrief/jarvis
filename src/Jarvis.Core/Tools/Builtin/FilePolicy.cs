using Jarvis.Core.Settings;

namespace Jarvis.Core.Tools.Builtin;

/// <summary>
/// Decides how risky it is to touch a path. Inside the user's allowed folders, reading is
/// safe; system folders, credential stores and JARVIS's own data are always treated as risky.
/// </summary>
public sealed class FilePolicy(JarvisPaths paths)
{
    public string Resolve(string path)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (p == "~") p = home;
        else if (p.StartsWith("~/") || p.StartsWith("~\\")) p = Path.Combine(home, p[2..]);
        p = KnownFolder(p) ?? p;
        if (!Path.IsPathRooted(p)) p = Path.Combine(home, p);
        return Path.GetFullPath(p);
    }

    public IReadOnlyList<string> AllowedRoots(JarvisSettings s)
    {
        var roots = s.Files.AllowedRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Resolve).ToList();
        if (roots.Count == 0) roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return roots;
    }

    public bool IsInsideAllowedRoots(string fullPath, JarvisSettings s) =>
        AllowedRoots(s).Any(root => IsUnder(fullPath, root));

    /// <summary>Returns a reason when the path is a protected/system/secret location.</summary>
    public string? ProtectedReason(string fullPath)
    {
        if (IsUnder(fullPath, paths.DataDir)) return "This is JARVIS's own data folder (memory, settings, secrets).";

        var name = Path.GetFileName(fullPath).ToLowerInvariant();
        if (name is ".env" or "id_rsa" or "id_ed25519" or "credentials" or "secrets.json" || name.EndsWith(".pem") || name.EndsWith(".key") || name.EndsWith(".pfx"))
            return "This looks like a credentials or secrets file.";

        var segments = fullPath.Replace('\\', '/').ToLowerInvariant();
        if (segments.Contains("/.ssh") || segments.Contains("/.gnupg") || segments.Contains("/.aws") || segments.Contains("/microsoft/credentials") ||
            segments.Contains("/microsoft/protect") || segments.Contains("/google/chrome/user data") || segments.Contains("/microsoft/edge/user data"))
            return "This folder holds credentials or browser profile data.";

        if (OperatingSystem.IsWindows())
        {
            string[] system =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ];
            if (system.Where(d => !string.IsNullOrEmpty(d)).Any(d => IsUnder(fullPath, d))) return "This is a Windows system folder.";
            var root = Path.GetPathRoot(fullPath);
            if (root is not null && string.Equals(fullPath.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return "This is the root of a drive.";
        }
        else
        {
            string[] system = ["/etc", "/bin", "/sbin", "/usr", "/boot", "/sys", "/proc", "/dev", "/var/lib"];
            if (system.Any(d => IsUnder(fullPath, d))) return "This is a system folder.";
            if (fullPath == "/") return "This is the filesystem root.";
        }
        return null;
    }

    /// <summary>Risk for reading/listing a path.</summary>
    public RiskAssessment ReadRisk(string fullPath, JarvisSettings s, string summary)
    {
        if (ProtectedReason(fullPath) is { } why) return new(RiskLevel.Sensitive, summary, why);
        return IsInsideAllowedRoots(fullPath, s)
            ? new(RiskLevel.Safe, summary)
            : new(RiskLevel.Sensitive, summary, "Outside the folders JARVIS may read freely.");
    }

    /// <summary>Risk for creating/modifying a path.</summary>
    public RiskAssessment WriteRisk(string fullPath, JarvisSettings s, string summary)
    {
        if (ProtectedReason(fullPath) is { } why) return new(RiskLevel.Critical, summary, why);
        if (!IsInsideAllowedRoots(fullPath, s)) return new(RiskLevel.Critical, summary, "Outside the folders JARVIS may work in.");
        return new(RiskLevel.Sensitive, summary, File.Exists(fullPath) ? "Overwrites an existing file." : "Creates or changes a file.");
    }

    public static bool IsUnder(string path, string root)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return p.Equals(r, cmp) || p.StartsWith(r + Path.DirectorySeparatorChar, cmp);
    }

    /// <summary>Maps "desktop", "downloads", "documents"... (and Arabic names) to real folders.</summary>
    public static string? KnownFolder(string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Language.TextNormalizer.Normalize(name) switch
        {
            "desktop" or "سطح المكتب" or "الديسكتوب" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) is { Length: > 0 } d ? d : Path.Combine(home, "Desktop"),
            "documents" or "my documents" or "المستندات" or "الدوكيومنتس" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } d ? d : Path.Combine(home, "Documents"),
            "downloads" or "التنزيلات" or "الداونلودز" or "الداونلود" => Path.Combine(home, "Downloads"),
            "pictures" or "photos" or "الصور" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } d ? d : Path.Combine(home, "Pictures"),
            "music" or "الاغاني" or "المزيكا" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) is { Length: > 0 } d ? d : Path.Combine(home, "Music"),
            "videos" or "الفيديوهات" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) is { Length: > 0 } d ? d : Path.Combine(home, "Videos"),
            "home" or "user folder" => home,
            _ => null,
        };
    }
}

/// <summary>Where "deleted" files go. Windows uses the Recycle Bin; elsewhere a JARVIS trash folder.</summary>
public interface IFileTrash
{
    string Name { get; }
    void Trash(string fullPath);
}

public sealed class FolderTrash(JarvisPaths paths) : IFileTrash
{
    public string Name => "JARVIS trash folder";

    public void Trash(string fullPath)
    {
        var dest = Path.Combine(paths.TrashDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dest);
        var target = Path.Combine(dest, Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)));
        if (Directory.Exists(fullPath)) Directory.Move(fullPath, target);
        else File.Move(fullPath, target);
    }
}
