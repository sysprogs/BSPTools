using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RenesasToolchainManager.Tools
{
    public class GNUToolInfo
    {
        public string Version;
        public string Target;
    }

    class GCCInfoCollector
    {
        static GNUToolInfo QueryToolVersion(string path, Regex rgTarget, Regex rgVersion, int idxVersion, bool throwOnFailure = true)
        {
            List<string> output = new List<string>();
            Process proc = new Process();
            proc.StartInfo.FileName = path;
            proc.StartInfo.Arguments = "-v";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.StartInfo.CreateNoWindow = true;
            proc.Start();

            proc.Start();
            DataReceivedEventHandler handler = (s, e) =>
           {
               if (e.Data != null)
                   output.Add(e.Data);
           };

            proc.OutputDataReceived += handler;
            proc.ErrorDataReceived += handler;
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                if (!throwOnFailure)
                    return null;

                throw new Exception($"Error: {path} exited with code {proc.ExitCode}");
            }

            GNUToolInfo info = new GNUToolInfo();

            foreach (var line in output)
            {
                var m = rgVersion.Match(line);
                if (m.Success)
                    info.Version = m.Groups[idxVersion].ToString();

                if (rgTarget != null)
                {
                    m = rgTarget.Match(line);
                    if (m.Success)
                        info.Target = m.Groups[1].ToString();
                }
            }

            if (info.Version == null)
            {
                if (!throwOnFailure)
                    return null;
                throw new Exception($"{path} returned unexpected output");
            }

            return info;
        }

        public static GNUToolInfo QueryGCCVersion(string path, bool throwOnFailure = true)
        {
            return QueryToolVersion(path, new Regex(@"^Target: (.*)$"), new Regex(@"(gcc|clang|Apple LLVM) version ([^ ]+)"), 2, throwOnFailure);
        }

        public static GNUToolInfo QueryGDBVersion(string path, bool throwOnFailure = true)
        {
            return QueryToolVersion(path, new Regex("^This GDB was configured as \"([^ \"]+)\".$"), new Regex(@"GNU gdb( \(GDB\))? ([^ ]+)"), 2, throwOnFailure);
        }
    }
}
