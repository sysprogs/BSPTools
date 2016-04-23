using ESP8266DebugPackage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace ESPImageTool
{
    class Program
    {
        private static Assembly _DebugPackage;

        class ProgressTracker
        {
            private ProgrammableRegion _Region;
            int _BytesWritten;

            public ProgressTracker(ProgrammableRegion region)
            {
                _Region = region;
            }

            public void BlockWritten(ESP8266BootloaderClient sender, uint address, int blockSize)
            {
                _BytesWritten += blockSize;
                int percent = (_BytesWritten * 100) / _Region.Size;
                int progress = percent / 5;
                Console.Write($"\r[{new string('#', progress).PadRight(20)}] {percent}%");
            }
        }

        static void Main(string[] args)
        {
            try
            {
                string pkg = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"sysprogs\debug\core\ESP8266DebugPackage.dll");
                if (File.Exists(pkg))
                    _DebugPackage = Assembly.LoadFrom(pkg);
                AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
                Run(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (args.Name?.StartsWith("ESP8266DebugPackage,") == true)
                return _DebugPackage;
            return null;
        }

        static void Run(string[] args)
        { 
            Console.WriteLine("ESP8266 image tool v1.0 [http://sysprogs.com/]");
            if (args.Length < 1)
            {
                PrintUsage();
                return;
            }

            string port = null;
            string bootloader = null;
            int otaPort = 0;
            int baud = 115200;
            bool erase = false;
            List<string> files = new List<string>();
            string frequency = null, mode = null, size = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--boot")
                {
                    if (i >= (args.Length - 1))
                        throw new Exception("--boot must be followed by the bootloader image");
                    bootloader = args[++i];
                }
                else if (args[i] == "--program")
                {
                    if (i >= (args.Length - 1))
                        throw new Exception("--program must be followed by port number");
                    port = args[++i];
                    if ((i + 1) < args.Length && !args[i + 1].StartsWith("-"))
                        baud = int.Parse(args[++i]);
                }
                else if (args[i] == "--mode")
                {
                    if (i >= (args.Length - 1))
                        throw new Exception("--mode must be followed by FLASH mode");
                    mode = args[++i];
                }
                else if (args[i] == "--size")
                {
                    if (i >= (args.Length - 1))
                        throw new Exception("--size must be followed by FLASH mode");
                    size = args[++i];
                }
                else if (args[i] == "--freq")
                {
                    if (i >= (args.Length - 1))
                        throw new Exception("--freq must be followed by FLASH mode");
                    frequency = args[++i];
                }
                else if (args[i].ToLower() == "--ota")
                {
                    if (i >= (args.Length - 1))
                        throw new Exception("--OTA must be followed by port number");
                    otaPort = int.Parse(args[++i]);
                }
                else if (args[i] == "--erase")
                {
                    erase = true;
                }
                else
                    files.Add(args[i]);
            }

            ESP8266BinaryImage.ParsedHeader hdr = new ESP8266BinaryImage.ParsedHeader(frequency, mode, size);
            Console.WriteLine("FLASH Parameters:");
            Console.WriteLine("\tFrequency: " + DumpEnumValue(hdr.Frequency));
            Console.WriteLine("\tMode: " + DumpEnumValue(hdr.Mode));
            Console.WriteLine("\tSize: " + DumpEnumValue(hdr.Size));

            if (otaPort != 0)
            {
                OTAServer.ServeOTAFiles(otaPort, hdr, files.ToArray());
                return;
            }

            foreach (var elf in files)
            {
                string pathBase = Path.ChangeExtension(elf, ".").TrimEnd('.');
                List<ProgrammableRegion> regions = new List<ProgrammableRegion>();

                Console.WriteLine("Processing " + elf + "...");

                using (var elfFile = new ELFFile(elf))
                {
                    string status;
                    int appMode = ESP8266BinaryImage.DetectAppMode(elfFile, out status);
                    Console.WriteLine(status);

                    if (appMode == 0)
                    {
                        var img = ESP8266BinaryImage.MakeNonBootloaderImageFromELFFile(elfFile, hdr);

                        string fn = pathBase + "-0x00000.bin";
                        using (var fs = new FileStream(fn, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                        {
                            img.Save(fs);
                            regions.Add(new ProgrammableRegion { FileName = fn, Offset = 0, Size = (int)fs.Length });
                        }

                        foreach (var sec in ESP8266BinaryImage.GetFLASHSections(elfFile))
                        {
                            fn = string.Format("{0}-0x{1:x5}.bin", pathBase, sec.OffsetInFLASH);
                            using (var fs = new FileStream(fn, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                            {
                                fs.Write(sec.Data, 0, sec.Data.Length);
                                regions.Add(new ProgrammableRegion { FileName = fn, Offset = (int)sec.OffsetInFLASH, Size = sec.Data.Length });
                            }
                        }
                    }
                    else
                    {
                        string fn;
                        var img = ESP8266BinaryImage.MakeBootloaderBasedImageFromELFFile(elfFile, hdr, appMode);

                        if (bootloader == null)
                            Console.WriteLine("Warning: no bootloader specified. Skipping bootloader...");
                        else
                        {
                            if (!File.Exists(bootloader))
                                throw new Exception(bootloader + " not found. Cannot program OTA images.");

                            byte[] data = File.ReadAllBytes(bootloader);
                            data[2] = (byte)img.Header.Mode;
                            data[3] = (byte)(((byte)img.Header.Size << 4) | (byte)img.Header.Frequency);
                            fn = string.Format("{0}-boot.bin", pathBase);
                            File.WriteAllBytes(fn, data);

                            regions.Add(new ProgrammableRegion { FileName = fn, Offset = 0, Size = File.ReadAllBytes(fn).Length });
                        }

                        fn = string.Format("{0}-user{1}.bin", pathBase, appMode);
                        using (var fs = new FileStream(fn, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                        {
                            img.Save(fs);
                            regions.Add(new ProgrammableRegion { FileName = fn, Offset = (int)img.BootloaderImageOffset, Size = (int)fs.Length });
                        }
                    }
                }

                if (port != null)
                {
                    using (var serialPort = new SerialPortStream(port, baud, System.IO.Ports.Handshake.None) { AllowTimingOutWithZeroBytes = true })
                    {
                        ESP8266BootloaderClient client = new ESP8266BootloaderClient(serialPort, 50, null);
                        Console.WriteLine("Connecting to bootloader on {0}...", port);
                        client.Sync();
                        if (erase)
                        {
                            Console.WriteLine("Erasing FLASH...");
                            client.EraseFLASH();
                            Console.WriteLine("FLASH erased. Please restart your ESP8266 into the bootloader mode again.\r\nPress any key when done...");
                            Console.ReadKey();
                            client.Sync();
                        }
                        foreach (var region in regions)
                        {
                            DateTime start = DateTime.Now;
                            Console.WriteLine("Programming " + Path.GetFileName(region.FileName) + "...");
                            var tracker = new ProgressTracker(region);
                            client.BlockWritten += tracker.BlockWritten;
                            client.ProgramFLASH((uint)region.Offset, File.ReadAllBytes(region.FileName));
                            client.BlockWritten -= tracker.BlockWritten;
                            Console.WriteLine("\rProgrammed in {0} seconds        ", (int)(DateTime.Now - start).TotalSeconds);
                        }
                    }
                }
                else
                {
                    int fileNameLen = Path.GetFileName(args[0]).Length + 10;
                    Console.WriteLine("\r\nCreated the following files:");

                    Console.WriteLine("File".PadRight(fileNameLen) + " FLASH Offset  Size");
                    foreach (var region in regions)
                    {
                        Console.WriteLine(Path.GetFileName(region.FileName).PadRight(fileNameLen) + " " + string.Format("0x{0:x8}    {1}KB", region.Offset, region.Size / 1024));
                    }
                }
            }
        }



        static string DumpEnum<_Ty>()
        {
            List<string> values = new List<string>();
            foreach (FieldInfo fld in typeof(_Ty).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = fld.GetCustomAttributes(typeof(ArgumentValueAttribute), false);
                if (attr != null && attr.Length > 0)
                {
                    values.Add((attr[0] as ArgumentValueAttribute).Name);
                }
            }
            return string.Join("/", values.ToArray());
        }

        static string DumpEnumValue<_Ty>(_Ty value)
        {
            List<string> values = new List<string>();
            foreach (FieldInfo fld in typeof(_Ty).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = fld.GetCustomAttributes(typeof(ArgumentValueAttribute), false);
                if (attr != null && attr.Length > 0)
                {
                    if (fld.GetValue(null).Equals(value))
                        return (attr[0] as ArgumentValueAttribute).Hint ?? value.ToString();
                }
            }
            return value.ToString();
        }

        private static void PrintUsage()
        {
            Console.WriteLine($"Usage: ESPImageTool <ELF file> [--mode {DumpEnum<ESP8266BinaryImage.FLASHMode>()}] [--size {DumpEnum<ESP8266BinaryImage.FLASHSize>()}] [--freq {DumpEnum<ESP8266BinaryImage.FLASHFrequency>()}] [--program <COM port> <baud rate>] [--boot <bootloader binary>]");
            Console.WriteLine($"       ESPImageTool --OTA <port> <user1.elf> [<user2.elf]");
        }
    }
}
