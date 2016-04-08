using BSPEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ESP8266DebugPackage
{
    class ESP8266StubDebugExtension : IDebugMethodExtension2
    {
        public IEnumerable<ICustomStartupSequenceBuilder> StartupSequences
        {
            get { return new ICustomStartupSequenceBuilder[] { new StubStartSequence() }; }
        }

        public void AdjustDebugMethod(LoadedBSP.ConfiguredMCU mcu, ConfiguredDebugMethod method)
        {
        }

        public ICustomBSPConfigurator CreateConfigurator(LoadedBSP.ConfiguredMCU mcu, DebugMethod method)
        {
            return null;
        }

        class StubStartSequence : ICustomStartupSequenceBuilder
        {
            readonly SynchronizationContext _SyncContext;

            public StubStartSequence()
            {
                _SyncContext = SynchronizationContext.Current;
            }

            class ResultWrapper
            {
                public DialogResult Result;
            }

            public CustomStartupSequence BuildSequence(string targetPath, Dictionary<string, string> bspDict, Dictionary<string, string> debugMethodConfig, LiveMemoryLineHandler lineHandler)
            {
                if (!File.Exists(targetPath))
                    throw new Exception(targetPath + " not found. Debugging will not be possible.");

                bool stubFound = false;
                using (var elf = new ELFFile(targetPath))
                {
                    foreach (var sym in elf.LoadAllSymbols())
                    {
                        if (sym.Name == "gdbstub_init")
                        {
                            stubFound = true;
                            break;
                        }
                    }
                }

                if (!stubFound)
                {
                    var wrp = new ResultWrapper();
                    _SyncContext.Send(o => ((ResultWrapper)o).Result = MessageBox.Show("The programmed image does not contain the GDB stub. Do you want to open instructions on debugging with ESP8266 GDB stub?", "VisualGDB", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information), wrp);
                    switch(wrp.Result)
                    {
                        case DialogResult.Yes:
                            Process.Start("http://visualgdb.com/KB/esp8266gdbstub");
                            goto case DialogResult.Cancel;
                        case DialogResult.No:
                            break;
                        case DialogResult.Cancel:
                            throw new OperationCanceledException();
                    }
                }

                string val;
                if (!debugMethodConfig.TryGetValue("com.sysprogs.esp8266.program_flash", out val) || val != "0")
                {
                    var wrp = new ResultWrapper();
                    _SyncContext.Send(o => ((ResultWrapper)o).Result = MessageBox.Show("Please reboot your ESP8266 into the bootloader mode and press OK.", "VisualGDB", MessageBoxButtons.OKCancel, MessageBoxIcon.Information), wrp);
                    if (wrp.Result != DialogResult.OK)
                        throw new OperationCanceledException();

                    using (var serialPort = new SerialPortStream(debugMethodConfig["com.sysprogs.esp8266.gdbstub.com_port"], int.Parse(debugMethodConfig["com.sysprogs.esp8266.gdbstub.bl_baud"]), System.IO.Ports.Handshake.None))
                    {
                        serialPort.AllowTimingOutWithZeroBytes = true;
                        int resetDelay;
                        if (!debugMethodConfig.TryGetValue("com.sysprogs.esp8266.reset_delay", out val) || !int.TryParse(val, out resetDelay))
                            resetDelay = 25;

                        string seq;
                        debugMethodConfig.TryGetValue("com.sysprogs.esp8266.gdbstub.reset_sequence", out seq);
                        ESP8266BootloaderClient client = new ESP8266BootloaderClient(serialPort, resetDelay, seq);
                        client.Sync();
                        var regions = ESP8266StartupSequence.BuildFLASHImages(targetPath, bspDict, debugMethodConfig, lineHandler);

                        ProgramProgressForm frm = null;
                        _SyncContext.Post(o => { frm = new ProgramProgressForm(); frm.ShowDialog(); }, null);
                        int totalSize = 0;
                        foreach (var r in regions)
                            totalSize += r.Size;

                        ESP8266BootloaderClient.BlockWrittenHandler handler = (s, a, len) => frm.UpdateProgressAndThrowIfCanceled(a, len, totalSize);
                        bool useDIO = false;

                        try
                        {
                            client.BlockWritten += handler;
                            foreach (var r in regions)
                            {
                                var data = File.ReadAllBytes(r.FileName);
                                if (r.Offset == 0 && data.Length >= 4)
                                    useDIO = (data[2] == 2);

                                client.ProgramFLASH((uint)r.Offset, data);
                            }
                        }
                        finally
                        {
                            client.BlockWritten -= handler;
                            _SyncContext.Post(o => { frm.Close(); frm.Dispose(); }, null);
                        }

                        client.RunProgram(useDIO, false);
                    }
                }

                return new CustomStartupSequence
                {
                    Steps = new List<CustomStartStep> { 
                        new CustomStartStep("set serial baud $$com.sysprogs.esp8266.gdbstub.baud$$"),
                        new CustomStartStep(@"target remote \\.\$$com.sysprogs.esp8266.gdbstub.com_port$$"),
                    }
                };
            }

            public string FirstStepName
            {
                get { return "Programming firmware"; }
            }

            public string ID
            {
                get { return "com.sysprogs.esp8266.gdbstub.startup_sequence"; }
            }

            public string SecondStepName
            {
                get { return "Connecting to GDB stub"; }
            }

            public string Title
            {
                get { return "Preparing debug session"; }
            }
        }

    }
}
