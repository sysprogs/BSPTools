/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Serialization;
using System.IO;
using System.IO.Compression;

namespace LinkerScriptGenerator
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: LinkerScriptGenerator <script template> <memory template> <linker script to generate>");
                Environment.ExitCode = 1;
                return;
            }

            var scriptTemplate = LoadObject<LinkerScriptTemplate>(args[0]);
            var memoryTemplate = LoadObject<MemoryLayout>(args[1]);
            using (var fs = new StreamWriter(args[2]))
            using (var generator = new LdsFileGenerator(scriptTemplate, memoryTemplate))
                generator.GenerateLdsFile(fs, null);
        }

        public static _Ty LoadObject<_Ty>(string xmlFile, params Type[] extraTypes) where _Ty : new()
        {
            XmlSerializer ser = new XmlSerializer(typeof(_Ty), extraTypes);
            if (File.Exists(xmlFile))
                using (var fs = File.OpenRead(xmlFile))
                {
                    var obj = (_Ty)ser.Deserialize(fs);
#if _XML_FILE_TESTING
                    using (var fs2 = File.OpenWrite(xmlFile + ".2"))
                        ser.Serialize(fs2, obj);
#endif
                    return obj;
                }
            else if (File.Exists(xmlFile + ".gz"))
                using (var fs = File.OpenRead(xmlFile + ".gz"))
                using (var gs = new GZipStream(fs, CompressionMode.Decompress))
                    return (_Ty)ser.Deserialize(gs);
            else
            {
#if _XML_FILE_TESTING
                using (var fs = File.OpenWrite(xmlFile))
                    ser.Serialize(fs, new _Ty());
#endif

                throw new Exception(xmlFile + " does not exist!");
            }
        }

    }
}