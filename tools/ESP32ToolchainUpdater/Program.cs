﻿using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ESP32ToolchainUpdater
{
    internal class Program
    {
        static string QueryToolVersion(string tool)
        {
            Regex rgVersion = new Regex(@".* \(crosstool-.*\) ([0-9]+\.[0-9]+\.[0-9]+|[0-9]+\.[0-9]+)");
            Regex rgGDBVersion = new Regex(@"GNU gdb \(esp-gdb\) ([0-9\.]+)_.*");

            var proc = new Process();
            proc.StartInfo.FileName = tool;
            proc.StartInfo.Arguments = "--version";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.Start();
            proc.WaitForExit();
            var firstLine = proc.StandardOutput.ReadLine();

            var m = rgVersion.Match(firstLine);
            if (!m.Success)
                m = rgGDBVersion.Match(firstLine);

            if (!m.Success)
                throw new Exception("Unexpected version output: " + firstLine);

            return m.Groups[1].Value;
        }


        /*  Typical usage:
         *      1. Copy an existing toolchain under a new directory
         *      2. Copy the new tool directories inside the new toolchain, delete old ones
         *      3. Run ESP32ToolchainUpdater <old toolchain> <new toolchain>
         *         The updater will check each path directory in the old toolchain, find which exes it provides, find
         *         the same exes in the new toolchain, and update the paths accordingly.
         */
        static void Main(string[] args)
        {
            if (args.Length < 2)
                throw new Exception("Usage: ESP32ToolchainUpdater <old toolchain dir> <new toolchain dir>");

            string oldDir = Path.GetFullPath(args[0]), newDir = Path.GetFullPath(args[1]);
            var bspXML = DeviceDefinitionUpdater.UpdateBSP(newDir, @"E:\BSPDATA\CodeScope\esp32");

            var oldToolchain = XmlTools.LoadObject<Toolchain>(Path.Combine(oldDir, "toolchain.xml"));
            var newToolchain = XmlTools.LoadObject<Toolchain>(Path.Combine(newDir, "toolchain.xml"));

            if (oldToolchain.AdditionalPathDirs.Length != newToolchain.AdditionalPathDirs.Length)
                throw new Exception("Old/new toolchain have different number of path directories");

            var newToolchainExesByName = Directory.GetFiles(newDir, "*.exe", SearchOption.AllDirectories)
                .Select(f => f.Substring(newDir.Length + 1))
                .GroupBy(f => Path.GetFileName(f), StringComparer.InvariantCultureIgnoreCase)
                .ToDictionary(k => k.Key, v => v.ToArray());

            for (int i = 0; i < oldToolchain.AdditionalPathDirs.Length; i++)
            {
                var exes = Directory.GetFiles(Path.Combine(oldDir, oldToolchain.AdditionalPathDirs[i]), "*.exe");

                if (exes.Length == 0)
                    throw new Exception("No files in " + oldToolchain.AdditionalPathDirs[i]);

                Dictionary<string, int> candidateDirs = new Dictionary<string, int>();
                foreach (var exe in exes)
                {
                    if (!newToolchainExesByName.TryGetValue(Path.GetFileName(exe), out var paths))
                        continue;

                    foreach (var dir in paths.Select(f => Path.GetDirectoryName(f)))
                    {
                        candidateDirs.TryGetValue(dir, out var cnt);
                        candidateDirs[dir] = cnt + 1;
                    }

                }

                string bestMatch = null;

                if (candidateDirs.Count == 1)
                    bestMatch = candidateDirs.First().Key;
                else
                {
                    //Find a directory that differs in the version/date, (e.g. tools\xtensa-esp-elf\esp-12.x => tools\xtensa-esp-elf\esp-13.x)
                    foreach (var k in candidateDirs.Keys)
                    {
                        int idx = CountMatchingChars(oldToolchain.AdditionalPathDirs[i], k);
                        if (idx < 0 || (char.IsDigit(oldToolchain.AdditionalPathDirs[i][idx]) && char.IsDigit(k[idx])))
                        {
                            if (bestMatch != null)
                                throw new Exception("Could not find a non-ambiguous substitute for " + oldToolchain.AdditionalPathDirs[i]);
                            bestMatch = k;
                        }
                    }

                    if (bestMatch == null)
                        throw new Exception("Failed to find a substitute for " + oldToolchain.AdditionalPathDirs[i]);
                }

                newToolchain.AdditionalPathDirs[i] = bestMatch;
                if (!Directory.Exists(Path.Combine(newDir, newToolchain.AdditionalPathDirs[i])))
                    throw new Exception("Invalid path: " + newToolchain.AdditionalPathDirs[i]);

                if (newToolchain.AdditionalPathDirs[i] != oldToolchain.AdditionalPathDirs[i])
                    Console.WriteLine($"{oldToolchain.AdditionalPathDirs[i]} => {newToolchain.AdditionalPathDirs[i]}");
            }

            newToolchain.Ninja = newToolchainExesByName["ninja.exe"][0];
            newToolchain.BinaryDirectory = Path.GetDirectoryName(newToolchainExesByName["xtensa-esp32-elf-gcc.exe"][0]);

            newToolchain.GCCVersion = QueryToolVersion(Path.Combine(newDir, newToolchain.BinaryDirectory, "xtensa-esp32-elf-gcc.exe"));
            newToolchain.GDBVersion = QueryToolVersion(Path.Combine(newDir, newToolchainExesByName["xtensa-esp32-elf-gdb.exe"].First()));
            newToolchain.BinutilsVersion = QueryToolVersion(Path.Combine(newDir, newToolchain.BinaryDirectory, "xtensa-esp32-elf-as.exe"));

            XmlTools.SaveObject(newToolchain, Path.Combine(newDir, "toolchain.xml"));

            var bsp = XmlTools.LoadObject<BoardSupportPackage>(bspXML);
            foreach (var mcu in bsp.SupportedMCUs)
            {
                var v = mcu.AdditionalSystemVars.FirstOrDefault(kv => kv.Key == "com.sysprogs.visualgdb.gdb_override");
                if (v != null)
                    v.Value = @"$(ToolchainDir)\" + newToolchainExesByName[Path.GetFileName(v.Value)].Single();

            }
            XmlTools.SaveObject(bsp, Path.Combine(newDir, @"esp32-bsp\BSP.xml"));
        }

        static int CountMatchingChars(string x, string y)
        {
            for (int i = 0; i < x.Length && i < y.Length; i++)
                if (x[i] != y[i])
                    return i;

            return -1;
        }
    }
}
