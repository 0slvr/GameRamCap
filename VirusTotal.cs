using System.Diagnostics;
using System.Security.Cryptography;

namespace GameRamCap;

/// <summary>VirusTotal lookup by hash. Only the SHA-256 of the file is used to build the page address:
/// the file is never uploaded and no API key is needed. If VirusTotal has never seen the file, its page says so.</summary>
internal static class VirusTotal
{
    public static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static string Url(string sha256) => "https://www.virustotal.com/gui/file/" + sha256;

    public static bool Open(string sha256)
    {
        try { Process.Start(new ProcessStartInfo(Url(sha256)) { UseShellExecute = true }); return true; }
        catch (Exception ex) { Logger.Write("VirusTotal: could not open the browser: " + ex.Message); return false; }
    }
}
