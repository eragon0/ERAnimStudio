using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DSAnimStudio.HkxEdit
{
    /// <summary>
    /// Havok Content Tools 2018.1 (hctStandAloneFilterManager.exe), run headless to spline-compress an uncompressed animation the
    /// way Elden Ring's files are compressed. Havok's tools can't be shipped with this program: they are found on the user's PC, and
    /// only version 2018.1 is used (Elden Ring's files are Havok 2018.1; another version may write a file the game can't load).
    /// The filter set (HctSplineCompress.hko, embedded) runs Havok's spline compression, type pruning and platform writer.
    /// </summary>
    public static class HctCompressor
    {
        public const string ExeName = "hctStandAloneFilterManager.exe";
        public const string RequiredVersion = "2018.1";
        public const string EnvironmentVariable = "HCT_FILTER_MANAGER";

        /// <summary>A path set by the user in the program's settings; empty: not set.</summary>
        public static string ManualPath = "";

        public sealed class Found
        {
            public string Path;
            public string Version;
            public string Source;
        }

        /// <summary>
        /// Havok Content Tools 2018.1, looked for in order: the path in the settings, the HCT_FILTER_MANAGER environment variable,
        /// Havok's installer entry in the registry (HKCU\Software\Havok), the default install folder. Null and the reason when none
        /// is usable.
        /// </summary>
        public static Found Find(out string whyNot)
        {
            var tried = new List<string>();
            Found Check(string path, string source, string registryVersion = null)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;
                path = path.Trim().Trim('"');
                if (Directory.Exists(path))
                    path = System.IO.Path.Combine(path, ExeName);
                if (!File.Exists(path))
                {
                    tried.Add($"{source}: no {ExeName} at {path}");
                    return null;
                }
                string version = registryVersion ?? FileVersion(path);
                if (version == null || !version.StartsWith(RequiredVersion))
                {
                    tried.Add($"{source}: {path} is version {version ?? "unknown"}, {RequiredVersion} is needed");
                    return null;
                }
                return new Found { Path = path, Version = version, Source = source };
            }

            var found = Check(ManualPath, "the settings")
                ?? Check(Environment.GetEnvironmentVariable(EnvironmentVariable), $"the {EnvironmentVariable} variable");
            if (found == null)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Havok");
                    string dir = key?.GetValue("InstallationDirectory_x64") as string;
                    string ver = key?.GetValue("InstalledVersion_x64") as string;
                    if (dir == null)
                        tried.Add(@"the registry: no Havok install recorded in HKCU\Software\Havok");
                    else
                        found = Check(System.IO.Path.Combine(dir, "HavokContentTools", ExeName), @"the registry (HKCU\Software\Havok)", ver);
                }
                catch (Exception e)
                {
                    tried.Add($"the registry: {e.Message}");
                }
            }
            found ??= Check(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Havok", "HavokContentTools", ExeName),
                            "the default folder");
            whyNot = found == null ? "Havok Content Tools " + RequiredVersion + " was not found. Looked in: " + string.Join("; ", tried) : null;
            return found;
        }

        static string FileVersion(string path)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                return v.FileMajorPart > 0 ? $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}" : v.FileVersion;
            }
            catch
            {
                return null;
            }
        }

        static string FilterSet()
        {
            var asm = typeof(HctCompressor).Assembly;
            string name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("HctSplineCompress.hko"))
                ?? throw new FileNotFoundException("the HCT filter set HctSplineCompress.hko is not embedded");
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s, Encoding.ASCII);
            return r.ReadToEnd();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);

        /// <summary>A scratch folder for HCT, whose asset paths can't contain spaces.</summary>
        public static string ScratchFolder()
        {
            string temp = System.IO.Path.GetTempPath();
            if (temp.Contains(' '))
            {
                var sb = new StringBuilder(1024);
                if (GetShortPathName(temp, sb, sb.Capacity) > 0)
                    temp = sb.ToString();
            }
            if (temp.Contains(' '))
                throw new IOException($"the temp folder {temp} has a space in its path, which Havok Content Tools can't take: point TEMP to a folder without spaces");
            string dir = System.IO.Path.Combine(temp, "hkxedit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>The uncompressed tagfile compressed by HCT; returns the compressed file's bytes.</summary>
        public static byte[] Compress(Found hct, byte[] uncompressed, string work, int timeoutMs = 5 * 60 * 1000)
        {
            string src = System.IO.Path.Combine(work, "uncompressed.hkx");
            string dst = System.IO.Path.Combine(work, "compressed.hkx");
            string cfg = System.IO.Path.Combine(work, "compress.hko");
            File.WriteAllBytes(src, uncompressed);
            File.WriteAllText(cfg, FilterSet().Replace("{{OUTPUT}}", dst), Encoding.ASCII);
            if (File.Exists(dst))
                File.Delete(dst);
            var psi = new ProcessStartInfo(hct.Path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = work,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-s", cfg, "-o", work, "-p", work, src })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new IOException("Havok Content Tools did not start");
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                p.Kill(true);
                throw new TimeoutException("Havok Content Tools ran for over 5 minutes: stopped");
            }
            if (!File.Exists(dst))
            {
                string log = (output + err.Result).Trim();
                throw new IOException($"Havok Content Tools wrote nothing (exit {p.ExitCode}{(p.ExitCode == -2 ? ": the input was not loaded" : "")})"
                                      + (log.Length > 0 ? ": " + (log.Length > 400 ? log[..400] : log) : ""));
            }
            return File.ReadAllBytes(dst);
        }
    }
}
