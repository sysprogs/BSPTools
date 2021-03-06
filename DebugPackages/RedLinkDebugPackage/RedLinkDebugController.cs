﻿using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RedLinkDebugPackage
{
    class RedLinkDebugController : IDebugMethodController, ICustomSettingsTypeProvider
    {
        public ICustomSettingsTypeProvider TypeProvider => this;

        public bool SupportsConnectionTesting => true;

        public Type[] SettingsObjectTypes { get; } = new[] { typeof(RedLinkDebugSettings) };

        public ICustomDebugMethodConfigurator CreateConfigurator(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host)
        {
            return new GUI.RedLinkSettingsControl(method, host, this).Controller;
        }

        public const string DeviceDirectoryVariableReference = "$$REDLINK:DEVICE_DIRECTORY$$";
        public const string FLASHDriverDirectoryVariableReference = "$$REDLINK:FLASH_DRIVER_DIRECTORY$$";

        public IGDBStubInstance StartGDBStub(IDebugStartService startService, DebugStartContext context)
        {
            var settings = (RedLinkDebugSettings)context.Configuration ?? new RedLinkDebugSettings();

            var cmdLine = new RedLinkServerCommandLine(settings.CommandLineArguments);
            int gdbPort;
            using (var allocator = startService.BeginAllocatingTCPPorts())
            {
                gdbPort = allocator.AllocateUnusedTCPPort("SYS:GDB_PORT");
            }

            var ideRoot = RegistrySettings.MCUXpressoPath ?? throw new Exception("Please specify the MCUXpresso directory via Debug Settings");

            bool programNow;
            switch (settings.ProgramMode)
            {
                case ProgramMode.Disabled:
                    programNow = false;
                    break;
                case ProgramMode.Auto:
                    programNow = !startService.IsCurrentFirmwareAlreadyProgrammed();
                    break;
                default:
                    programNow = true;
                    break;
            }

            var gdbServer = Path.Combine(ideRoot, @"binaries\crt_emu_cm_redlink.exe");
            if (!File.Exists(gdbServer))
                throw new Exception("Could not find " + gdbServer);

            var cmdLineText = cmdLine.CommandLine;
            if (cmdLineText.Contains(DeviceDirectoryVariableReference))
            {
                var db = new RedLinkDeviceDatabase();

                var device = cmdLine.Device ?? throw new Exception("RedLink command line does not specify the device (-pXXX)");
                var vendor = cmdLine.Vendor ?? throw new Exception("RedLink command line does not specify the vendor (-vendor)");

                device = startService.ExpandProjectVariables(device, true, false);
                vendor = startService.ExpandProjectVariables(vendor, true, false);

                var dev = db.ProvideDeviceDefinition(vendor, device) ?? throw new Exception($"Unknown vendor/device: {vendor}/{device}. Please use VisualGDB Project Properties -> Debug Settings to configure.");

                cmdLineText = cmdLineText.Replace(DeviceDirectoryVariableReference, TranslatePath(dev.DefinitionDirectory));
            }

            if (cmdLineText.Contains(FLASHDriverDirectoryVariableReference))
            {
                var pluginDir = Path.Combine(ideRoot, "plugins");
                if (Directory.Exists(pluginDir))
                {
                    var toolsPlugin = Directory.GetDirectories(pluginDir, "com.nxp.mcuxpresso.tools.bin.win32*").FirstOrDefault();
                    if (toolsPlugin != null)
                        cmdLineText = cmdLineText.Replace(FLASHDriverDirectoryVariableReference, TranslatePath(Path.Combine(toolsPlugin, @"binaries\Flash")));
                }
            }

            string explicitSerialNumber = null;

            if (context.ResolvedDevices?.BestMatch.Device.SerialNumber != null)
            {
                if ((context.ResolvedDevices.AllCompatibleDevices?.Length ?? 0) > 1 || settings.AlwaysUseProbeSerialNumber)
                {
                    explicitSerialNumber = context.ResolvedDevices.BestMatch.Device.SerialNumber;
                    cmdLineText += " --probeserial " + explicitSerialNumber;
                }
            }

            int coreIndex = 0;
            if (cmdLine.Core is string core)
            {
                core = startService.ExpandProjectVariables(core, true, false);
                int.TryParse(core, out coreIndex);
            }

            var tool = startService.LaunchCommandLineTool(new CommandLineToolLaunchInfo
            {
                Command = gdbServer,
                Arguments = cmdLineText,
                WorkingDirectory = Path.GetDirectoryName(gdbServer)
            });

            return new RedLinkGDBStub(context, settings, cmdLine, gdbPort, tool, programNow, explicitSerialNumber, coreIndex);
        }

        public static string TranslatePath(string path)
        {
            path = path.Replace('\\', '/');
            if (path.Contains(' '))
                path = "\"" + path + "\"";
            return path;
        }

        public object TryConvertLegacyConfiguration(IBSPConfiguratorHost host, string methodDirectory, Dictionary<string, string> legacyConfiguration) => null;
    }
}
