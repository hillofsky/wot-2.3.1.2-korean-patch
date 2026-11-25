using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;

internal static class InstallKorean
{
    private const string DefaultGame = @"D:\World_of_Tanks_EU_Offline_2.3.1.2";
    private const int ExpectedFiles = 267;
    private static readonly Regex LocalizedPath = new Regex(
        @"\A(loc_version\.xml|res/gui/flash/fontconfig\.xml|res/text/lc_messages/[a-zA-Z0-9_.-]+\.mo)\z",
        RegexOptions.CultureInvariant);

    private sealed class Change
    {
        public string Name;
        public string Source;
        public string Target;
        public string Before;
        public string After;
    }

    private static int Main(string[] args)
    {
        int result = 0;
        try
        {
            string game = args.Length > 0 ? args[0] : DefaultGame;
            if (args.Length == 0 && !File.Exists(Path.Combine(game, "version.xml")))
            {
                Console.WriteLine("Enter the folder containing WorldOfTanks.exe:");
                game = (Console.ReadLine() ?? "").Trim().Trim('"');
            }
            if (String.IsNullOrWhiteSpace(game))
                throw new InvalidOperationException("Select the game folder.");

            Install(Path.GetFullPath(game), AppDomain.CurrentDomain.BaseDirectory);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            result = 1;
        }
        if (args.Length == 0 && !Console.IsInputRedirected)
        {
            Console.WriteLine("Press Enter to close this window.");
            Console.ReadLine();
        }
        return result;
    }

    private static void Install(string game, string package)
    {
        XmlDocument version = new XmlDocument();
        version.XmlResolver = null;
        version.Load(Path.Combine(game, "version.xml"));
        XmlNode build = version.SelectSingleNode("/version.xml/version");
        XmlNode realm = version.SelectSingleNode("/version.xml/meta/realm");
        if (build == null || realm == null || build.InnerText.Trim() != "v.2.3.1.2 #921"
            || realm.InnerText.Trim() != "EU")
            throw new InvalidOperationException("This is for World of Tanks EU 2.3.1.2 #921.");

        RequireClosedGame();
        if (!File.Exists(Path.Combine(game, "res/gui/flash/fonts_ko.swf")))
            throw new InvalidOperationException("The Korean font resource in this build is missing.");

        List<Change> changes = new List<Change>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (SHA256 sha = SHA256.Create())
        {
            foreach (string line in File.ReadAllLines(Path.Combine(package, "manifest.tsv")))
            {
                string[] fields = line.Split('\t');
                if (fields.Length != 3 || !LocalizedPath.IsMatch(fields[0]) || !seen.Add(fields[0]))
                    throw new InvalidDataException("Unexpected file in the Korean pack manifest.");

                string name = fields[0];
                string localPath = name.Replace('/', Path.DirectorySeparatorChar);
                Change change = new Change {
                    Name = name, Source = Path.Combine(package, "payload", localPath),
                    Target = Path.Combine(game, localPath), After = fields[2]
                };
                if (!Matches(Hash(sha, change.Source), change.After))
                    throw new InvalidDataException("The Korean patch file is incomplete: " + name);
                change.Before = Hash(sha, change.Target);
                if (Matches(change.Before, change.After))
                    continue;
                if (!Array.Exists(fields[1].Split(','), hash => Matches(hash, change.Before)))
                    throw new InvalidDataException("The game file differs from the supported original: " + name);
                changes.Add(change);
            }
            if (seen.Count != ExpectedFiles)
                throw new InvalidDataException("The Korean pack manifest is incomplete.");
            if (changes.Count == 0)
            {
                Console.WriteLine("The Korean pack is already installed.");
                return;
            }
            Apply(game, changes, sha);
        }
    }

    private static void Apply(string game, List<Change> changes, SHA256 sha)
    {
        string backup = Path.Combine(game, "wot-ko-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".zip");
        string staging = TemporaryBeside(backup);
        try
        {
            using (ZipArchive zip = ZipFile.Open(staging, ZipArchiveMode.Create))
            {
                foreach (Change change in changes)
                {
                    byte[] original = File.ReadAllBytes(change.Target);
                    if (!Matches(Hex(sha.ComputeHash(original)), change.Before))
                        throw new IOException("The game file changed during backup: " + change.Name);
                    using (Stream entry = zip.CreateEntry(change.Name, CompressionLevel.Optimal).Open())
                        entry.Write(original, 0, original.Length);
                }
            }
            File.Move(staging, backup);
        }
        finally { RemoveTemporary(staging); }

        List<Change> applied = new List<Change>();
        using (ZipArchive zip = ZipFile.OpenRead(backup))
        {
            try
            {
                RequireClosedGame();
                foreach (Change change in changes)
                {
                    string temp = TemporaryBeside(change.Target);
                    try
                    {
                        File.Copy(change.Source, temp);
                        if (!Matches(Hash(sha, temp), change.After)
                            || !Matches(Hash(sha, change.Target), change.Before))
                            throw new IOException("A file changed during installation: " + change.Name);
                        File.Replace(temp, change.Target, null);
                        applied.Add(change);
                    }
                    finally { RemoveTemporary(temp); }
                }
            }
            catch
            {
                foreach (Change change in applied)
                {
                    string temp = TemporaryBeside(change.Target);
                    try
                    {
                        zip.GetEntry(change.Name).ExtractToFile(temp);
                        File.Replace(temp, change.Target, null);
                    }
                    catch (Exception)
                    {
                        Console.Error.WriteLine("Restore " + change.Name + " from " + backup);
                    }
                    finally { RemoveTemporary(temp); }
                }
                throw;
            }
        }

        Console.WriteLine("Korean UI installed. Files applied: " + applied.Count);
        Console.WriteLine("Original files backed up at " + backup);
    }

    private static void RequireClosedGame()
    {
        Process[] running = Process.GetProcessesByName("WorldOfTanks");
        foreach (Process process in running)
            process.Dispose();
        if (running.Length != 0)
            throw new InvalidOperationException("Close World of Tanks before installing the Korean pack.");
    }

    private static string Hash(SHA256 sha, string path)
    {
        using (Stream input = File.OpenRead(path))
            return Hex(sha.ComputeHash(input));
    }

    private static string Hex(byte[] data) { return BitConverter.ToString(data).Replace("-", ""); }
    private static bool Matches(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    private static string TemporaryBeside(string path) { return path + "." + Guid.NewGuid().ToString("N") + ".tmp"; }
    private static void RemoveTemporary(string path) { if (File.Exists(path)) File.Delete(path); }
}
