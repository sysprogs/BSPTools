using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace pic32
{
    class Program
    {
        static void Main(string[] args)
        {
            var normalArgs = args.Where(a => !a.StartsWith("/")).ToArray();

            if (normalArgs.Length < 1)
                throw new Exception("Usage: pic32_bsp_generator <packs dir>");

            string specificDir = null;
            if (normalArgs.Length > 1)
                specificDir = normalArgs[1];

            var baseDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"..\.."));

            using (var generator = new PIC32BSPGenerator(baseDir))
            {
                foreach (var subdir in Directory.GetDirectories(Path.Combine(normalArgs[0], "Microchip")))
                {
                    if (!Path.GetFileName(subdir).StartsWith("PIC32", StringComparison.InvariantCultureIgnoreCase))
                        continue;

                    if (specificDir != null && StringComparer.InvariantCultureIgnoreCase.Compare(Path.GetFileName(subdir), specificDir) != 0)
                        continue;

                    var versionDirs = Directory.GetDirectories(subdir);
                    if (versionDirs.Length != 1)
                        throw new Exception("Multiple versions found for " + subdir);

                    var pdscFiles = Directory.GetFiles(versionDirs[0], "*.pdsc");
                    if (pdscFiles.Length != 1)
                        throw new Exception("Multiple PDSC files found in " + versionDirs[0]);

                    generator.GenerateSingleBSP(pdscFiles[0], Path.GetFileName(subdir), args.Contains("/nocopy"));
                }
            }
        }

    }
}
