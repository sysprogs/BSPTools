using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace TiXDSDebugPackage
{
    public class XDSDebugController : IDebugMethodController, ICustomSettingsTypeProvider
    {
        public Type[] SettingsObjectTypes => new[] { typeof(TiXDSDebugSettings) };

        public ICustomSettingsTypeProvider TypeProvider => this;

        public bool SupportsConnectionTesting => true;

        public ICustomDebugMethodConfigurator CreateConfigurator(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host) => new XDSDebugMethodConfigurator(this, method, host);

        public IGDBStubInstance StartGDBStub(IDebugStartService startService, DebugStartContext context)
        {
            var settings = context.Configuration as TiXDSDebugSettings ?? throw new Exception("Missing debug method settings");

            var exe = new TiXDSSettingsEditor(settings).GDBAgentExecutable;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                throw new Exception("Missing TI XDS stub: " + exe);

            var tool = startService.LaunchCommandLineTool(new CommandLineToolLaunchInfo { Command = exe, WorkingDirectory = Path.GetDirectoryName(exe), Arguments = "cc3220s.dat" });
            return new StubInstance(context.Method.Directory, settings, tool);
        }

        class LoadedProgrammingStub
        {
            const int Signature = 0x769730AE;

            public readonly uint LoadAddress, EndOfStack, ProgramBuffer, ProgramBufferSize;
            public readonly uint EntryPoint, Result;
            private readonly ISimpleGDBSession _Session;
            bool _Loaded;

            public LoadedProgrammingStub(string path, ISimpleGDBSession session, TiXDSDebugSettings settings)
            {
                byte[] data = File.ReadAllBytes(path);
                _Path = path.Replace('\\', '/');
                var offsets = Enumerable.Range(0, data.Length - 3).Where(d => BitConverter.ToInt32(data, d) == Signature).ToArray();
                if (offsets.Length != 1)
                    throw new Exception("Unable to uniquely locate the address table block in " + path);

                int off = offsets[0];
                LoadAddress = BitConverter.ToUInt32(data, off += 4);
                EndOfStack = BitConverter.ToUInt32(data, off += 4);
                ProgramBuffer = BitConverter.ToUInt32(data, off += 4);
                ProgramBufferSize = BitConverter.ToUInt32(data, off += 4);

                EntryPoint = BitConverter.ToUInt32(data, off += 4);
                Result = BitConverter.ToUInt32(data, off += 4);
                _Session = session;
                _Settings = settings;
            }

            void EnsureLoaded()
            {
                if (_Loaded)
                    return;

                var result = _Session.RunGDBCommand($"restore {_Path} binary 0x{LoadAddress:x8}");
                if (!result.IsDone)
                    throw new Exception("Failed to load " + _Path);
                _Loaded = true;
            }

            enum ProgramCommand
            {
                EraseAll,
                ErasePages,
                ProgramPages,
            };

            private TiXDSDebugSettings _Settings;
            private readonly string _Path;

            int ExecuteCommand(ProgramCommand cmd, uint arg1, uint arg2)
            {
                EnsureLoaded();
                _Session.RunGDBCommand($"-gdb-set $sp=0x{EndOfStack:x8}", false);
                _Session.RunGDBCommand($"-gdb-set $pc=0x{EntryPoint:x8}", false);
                _Session.RunGDBCommand($"-gdb-set $r0=0x{(int)cmd:x8}", false);
                _Session.RunGDBCommand($"-gdb-set $r1=0x{arg1:x8}", false);
                _Session.RunGDBCommand($"-gdb-set $r2=0x{arg2:x8}", false);
                _Session.ResumeAndWaitForStop(_Settings.ProgramTimeout);
                string result = _Session.EvaluateExpression($"*((int *)0x{Result:x8})");
                if (int.TryParse(result, out var t))
                    return t;
                throw new Exception("Failed to parse algorithm result: " + result);
            }

            public void EraseMemory(uint address, uint size)
            {
                int r = ExecuteCommand(ProgramCommand.ErasePages, address, size);
                if (r != 0)
                    throw new Exception($"Failed to erase FLASH range 0x{address:x8} - 0x{address + size:x8}: error {r}");
            }

            public void ProgramMemory(uint address, string elfFile, uint offset, uint size, string sectionName)
            {
                uint alignment = address - (address & ~3U);
                address -= alignment;
                offset -= alignment;
                size += alignment;

                size = (size + 3U) & ~3U;

                uint loadBase = ProgramBuffer - offset;

                var result = _Session.RunGDBCommand($"restore {elfFile} binary 0x{loadBase:x8} 0x{offset:x8} 0x{offset + size:x8}");
                if (!result.IsDone)
                    throw new Exception("Failed to load " + elfFile);

                int r = ExecuteCommand(ProgramCommand.ProgramPages, address, (uint)size);
                if (r != 0)
                    throw new Exception($"Failed to program FLASH range 0x{address:x8} - 0x{address + size:x8} ({sectionName}): error {r}");

            }
        }

        const int FLASHBase = 0x01000000;
        const int MaximumFLASHSize = 1024 * 1024;

        public class StubInstance : IGDBStubInstance
        {
            private string _BaseDir;
            private TiXDSDebugSettings _Settings;

            public StubInstance(string directory, TiXDSDebugSettings settings, IExternalToolInstance tool)
            {
                _BaseDir = directory;
                _Settings = settings;
                Tool = tool;
            }

            public IExternalToolInstance Tool { get; set; }

            public object LocalGDBEndpoint => 55000;

            public IConsoleOutputClassifier OutputClassifier => null;

            public void ConnectGDBToStub(IDebugStartService service, ISimpleGDBSession session)
            {
                var info = service.TargetFileInfo;

                var result = session.RunGDBCommand($"-target-select remote :{LocalGDBEndpoint}");
                if (result.MainStatus != "^connected")
                    throw new Exception("Failed to connect to gdb stub");

                result = session.RunGDBCommand("mon reset");
                if (!result.IsDone)
                    throw new Exception("Failed to reset target");

                var sections = info.GetLoadableSections()
                    .Where(section => section.LoadAddress.HasValue && section.LoadAddress.Value >= FLASHBase && section.LoadAddress.Value < (FLASHBase + MaximumFLASHSize))
                    .ToArray();

                if (sections.Length == 0)
                {
                    if (service.Mode != EmbeddedDebugMode.ConnectionTest)
                    {
                        session.SendInformationalOutput("No FLASH sections found in " + info.Path);

                        result = session.RunGDBCommand("load");
                        if (!result.IsDone)
                            throw new Exception("Failed to reset target");
                    }
                }
                else
                {
                    bool skipLoad = false;

                    switch (_Settings.ProgramMode)
                    {
                        case ProgramMode.Disabled:
                            skipLoad = true;
                            break;
                        case ProgramMode.Auto:
                            if (service.IsCurrentFirmwareAlreadyProgrammed())
                                skipLoad = true;
                            break;
                    }

                    if (service.Mode == EmbeddedDebugMode.Attach || service.Mode == EmbeddedDebugMode.ConnectionTest)
                        skipLoad = true;

                    if (!skipLoad)
                    {
                        var resources = _Settings.FLASHResources ?? new FLASHResource[0];

                        using (var progr = session.CreateScopedProgressReporter("Programming FLASH...", new[] { "Erasing FLASH", "Programing FLASH" }))
                        {
                            var stub = new LoadedProgrammingStub(service.GetPathWithoutSpaces(Path.Combine(_BaseDir, "CC3220SF.bin")), session, _Settings);
                            uint totalSize = 0;

                            int totalItems = sections.Length + resources.Length;
                            int itemsDone = 0;

                            foreach (var sec in sections)
                            {
                                stub.EraseMemory((uint)sec.LoadAddress.Value, (uint)sec.Size);
                                totalSize += (uint)sec.Size;
                                progr.ReportTaskProgress(itemsDone, totalItems, $"Erasing {sec.Name}...");
                            }

                            foreach(var r in resources)
                            {
                                r.ExpandedPath = service.ExpandProjectVariables(r.Path, true, true);
                                r.Data = File.ReadAllBytes(r.ExpandedPath);

                                stub.EraseMemory(FLASHBase + (uint)r.ParsedOffset, (uint)r.Data.Length);
                                totalSize += (uint)r.Data.Length;
                                progr.ReportTaskProgress(itemsDone, totalItems, $"Erasing area for {Path.GetFileName(r.ExpandedPath)}...");
                            }

                            progr.ReportTaskCompletion(true);

                            var path = service.GetPathWithoutSpaces(info.Path);

                            uint doneTotal = 0;
                            foreach (var sec in sections)
                            {
                                for (uint done = 0; done < (uint)sec.Size; done++)
                                {
                                    uint todo = Math.Min(stub.ProgramBufferSize, (uint)sec.Size - done);
                                    progr.ReportTaskProgress(doneTotal, totalSize, $"Programming {sec.Name}...");
                                    stub.ProgramMemory((uint)sec.LoadAddress.Value + done, path, (uint)sec.OffsetInFile + done, todo, sec.Name);

                                    doneTotal += todo;
                                    done += todo;
                                }
                            }

                            foreach (var r in resources)
                            {
                                var imgName = Path.GetFileName(r.ExpandedPath);
                                for (uint done = 0; done < (uint)r.Data.Length; done++)
                                {
                                    uint todo = Math.Min(stub.ProgramBufferSize, (uint)r.Data.Length - done);
                                    progr.ReportTaskProgress(doneTotal, totalSize, $"Programming {imgName}...");
                                    stub.ProgramMemory((uint)FLASHBase + (uint)r.ParsedOffset + done, path, done, todo, imgName);

                                    doneTotal += todo;
                                    done += todo;
                                }
                            }
                        }
                    }

                    service.OnFirmwareProgrammedSuccessfully();
                    session.RunGDBCommand("set $pc=resetISR", false);
                }
            }

            public ILiveMemoryEvaluator CreateLiveMemoryEvaluator(IDebugStartService service)
            {
                throw new NotImplementedException();
            }

            public void Dispose()
            {
            }

            public string TryGetMeaningfulErrorMessageFromStubOutput()
            {
                string[] allLines = Tool.AllText.Split('\n').Select(l => l.Trim()).ToArray();
                for (int i = 0; i < allLines.Length - 1; i++)
                {
                    if (allLines[i].StartsWith("(Error "))
                        return allLines[i + 1];
                }
                return null;
            }

            public bool WaitForToolToStart(ManualResetEvent cancelEvent)
            {
                while (!cancelEvent.WaitOne(50))
                {
                    var text = Tool.AllText;
                    if (text.Contains("Waiting for client"))
                        return true;
                }
                return true;
            }
        }

        public object TryConvertLegacyConfiguration(IBSPConfiguratorHost host, string methodDirectory, Dictionary<string, string> legacyConfiguration) => null;
    }
}
