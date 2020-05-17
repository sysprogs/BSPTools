using BSPEngine;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace RedLinkDebugPackage.GUI
{
    /// <summary>
    /// Interaction logic for RedLinkSettingsControl.xaml
    /// </summary>
    public partial class RedLinkSettingsControl : UserControl
    {
        public ControllerImpl Controller { get; }

        internal RedLinkSettingsControl(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host, RedLinkDebugController debugController)
        {
            host.InstallStyles(this);

            Controller = new ControllerImpl(method, host, debugController, this);
            InitializeComponent();

            host.MakeSearchableComboBox(DeviceComboBox, (i, f) => Controller.FilterItem(i, f), Resources["deviceScriptSelectionControl"]);
        }

        public class ControllerImpl : ICustomDebugMethodConfigurator, INotifyPropertyChanged
        {
            readonly LoadedBSP.LoadedDebugMethod _Method;
            private readonly IBSPConfiguratorHost _Host;

            internal ControllerImpl(LoadedBSP.LoadedDebugMethod method,
                                  IBSPConfiguratorHost host,
                                  RedLinkDebugController debugController,
                                  object control)
            {
                _Method = method;
                _Host = host;
                TypeProvider = debugController;

                Control = control;
            }


            public object Control { get; }

            public object Configuration
            {
                get
                {
                    var result = (_Settings ?? new RedLinkDebugSettings()).ShallowClone();
                    result.CommandLineArguments = CommandLine;
                    result.StartupCommands = StartupCommands?.Split('\n')?.Select(c => c.Trim())?.ToArray();
                    return result;
                }
            }

            public ICustomSettingsTypeProvider TypeProvider { get; }

            public bool SupportsLiveVariables => true;

            public event PropertyChangedEventHandler PropertyChanged;
            public event EventHandler SettingsChanged;

            RedLinkDebugSettings _Settings;

            RedLinkDeviceDatabase _Database = new RedLinkDeviceDatabase();

            public void SetConfiguration(object configuration, KnownInterfaceInstance context)
            {
                var settings = (configuration as RedLinkDebugSettings) ?? new RedLinkDebugSettings();

                CommandLine = settings.CommandLineArguments;
                ProgramMode = settings.ProgramMode;
                StartupCommands = string.Join("\r\n", settings.StartupCommands ?? new string[0]);
                _Settings = settings;
            }

            public bool TryFixSettingsFromStubError(IGDBStubInstance stub)
            {
                return false;
            }

            public string ValidateSettings()
            {
                return null;
            }

            void OnPropertyChanged(string name)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }

            public bool FilterItem(object item, string filter)
            {
                return item.ToString().IndexOf(filter ?? "", StringComparison.InvariantCultureIgnoreCase) != -1;
            }

            int _UpdateInProgress;

            private void ParseCommandLine()
            {
                if (_UpdateInProgress > 0)
                    return;

                _UpdateInProgress++;
                try
                {
                    var cmdLine = new RedLinkServerCommandLine(CommandLine);

                    //Update devices
                    var devices = _Database.AllDevices.Select(d => new KnownDeviceEntry(d)).ToList();

                    string mcuVendor = _Host.MCU.ExpandedMCU.AdditionalSystemVars.FirstOrDefault(v => v.Key == "REDLINK:VENDOR_ID")?.Value;
                    string mcuDev = _Host.MCU.ExpandedMCU.AdditionalSystemVars.FirstOrDefault(v => v.Key == "REDLINK:DEVICE_ID")?.Value;

                    var resolvedDevice = devices.FirstOrDefault(d => d.Device.MatchesDefinition(mcuVendor, mcuDev))?.Device;

                    devices.Insert(0, new KnownDeviceEntry(mcuDev, resolvedDevice));

                    string cmdlineVendor = cmdLine.Vendor ?? RedLinkServerCommandLine.DefaultVendor;
                    string cmdlineDev = cmdLine.Device ?? RedLinkServerCommandLine.DefaultDevice;

                    var selectedDevice = devices.FirstOrDefault(d => d.Device.MatchesDefinition(cmdlineVendor, cmdlineDev));
                    if (selectedDevice == null)
                    {
                        selectedDevice = new KnownDeviceEntry(new RedLinkDeviceDatabase.KnownDevice(new RedLinkDeviceDatabase.Key(cmdlineVendor, cmdlineDev), null, false));
                        devices.Add(selectedDevice);
                    }

                    Devices = devices.ToArray();
                    Device = selectedDevice;

                    //Update cores
                    int maxCoreCount = 8;
                    List<LabelAndValue> coreChoices = new List<LabelAndValue>();

                    if (int.TryParse(_Host.MCU.ExpandedMCU.AdditionalSystemVars.FirstOrDefault(v => v.Key == "REDLINK:CORE_INDEX")?.Value, out var mcuCore))
                        coreChoices.Add(new LabelAndValue($"#{mcuCore} (auto)", RedLinkServerCommandLine.DefaultCore));

                    for (int i = 0; i < maxCoreCount; i++)
                        coreChoices.Add(new LabelAndValue($"#{i}", $"{i}"));

                    var cmdlineCoreText = cmdLine.Core;
                    var selectedCore = coreChoices.FirstOrDefault(c => c.Value == cmdlineCoreText);
                    if (selectedCore == null && !string.IsNullOrEmpty(cmdlineCoreText))
                    {
                        selectedCore = new LabelAndValue(cmdlineCoreText, cmdlineCoreText);
                        coreChoices.Add(selectedCore);
                    }

                    Cores = coreChoices.ToArray();
                    Core = selectedCore;
                }
                finally
                {
                    _UpdateInProgress--;
                }
            }

            void UpdateCommandLine(Action<RedLinkServerCommandLine> updateAction)
            {
                if (_UpdateInProgress > 0)
                    return;

                _UpdateInProgress++;
                try
                {
                    var cmdLine = new RedLinkServerCommandLine(CommandLine);
                    updateAction(cmdLine);
                    CommandLine = cmdLine.CommandLine;
                }
                finally
                {
                    _UpdateInProgress--;
                }
            }

            #region Model
            string _CommandLine;
            public string CommandLine
            {
                get => _CommandLine;
                set
                {
                    _CommandLine = value;
                    OnPropertyChanged(nameof(CommandLine));
                    ParseCommandLine();
                }
            }

            string _StartupCommands;
            public string StartupCommands
            {
                get => _StartupCommands;
                set
                {
                    _StartupCommands = value;
                    OnPropertyChanged(nameof(StartupCommands));
                }
            }

            ProgramMode _ProgramMode;
            public ProgramMode ProgramMode
            {
                get => _ProgramMode;
                set
                {
                    _ProgramMode = value;
                    OnPropertyChanged(nameof(ProgramMode));
                }
            }

            public string MCUXpressoDirectory
            {
                get => RegistrySettings.MCUXpressoPath;
                set
                {
                    RegistrySettings.MCUXpressoPath = value;
                    OnPropertyChanged(nameof(MCUXpressoDirectory));
                }
            }

            KnownDeviceEntry[] _Devices;
            public KnownDeviceEntry[] Devices
            {
                get => _Devices;
                set
                {
                    _Devices = value;
                    OnPropertyChanged(nameof(Devices));
                }
            }

            KnownDeviceEntry _Device;
            public KnownDeviceEntry Device
            {
                get => _Device;
                set
                {
                    _Device = value;
                    OnPropertyChanged(nameof(Device));
                    UpdateCommandLine(c =>
                    {
                        c.Device = value?.Device?.Key.Device ?? RedLinkServerCommandLine.DefaultDevice;
                        c.Vendor = value?.Device?.Key.Vendor ?? RedLinkServerCommandLine.DefaultVendor;
                    });
                }
            }


            LabelAndValue[] _Cores;
            public LabelAndValue[] Cores
            {
                get => _Cores;
                set
                {
                    _Cores = value;
                    OnPropertyChanged(nameof(Cores));
                }
            }

            LabelAndValue _Core;
            public LabelAndValue Core
            {
                get => _Core;
                set
                {
                    _Core = value;
                    OnPropertyChanged(nameof(Core));
                    UpdateCommandLine(c => c.Core = value?.Value ?? RedLinkServerCommandLine.DefaultCore);
                }
            }

            #endregion

            public enum DeviceIcon
            {
                Normal,
                Auto,
                Missing,
            }

            public class KnownDeviceEntry
            {
                public DeviceIcon Icon { get; }
                public readonly RedLinkDeviceDatabase.KnownDevice Device;

                public readonly string NameOverride;

                public KnownDeviceEntry(RedLinkDeviceDatabase.KnownDevice dev)
                {
                    Device = dev;
                    if (dev.DefinitionDirectory == null)
                        Icon = DeviceIcon.Missing;
                }

                public KnownDeviceEntry(string devName, RedLinkDeviceDatabase.KnownDevice resolvedDeviceEntry)
                {
                    Device = new RedLinkDeviceDatabase.KnownDevice(new RedLinkDeviceDatabase.Key("$$REDLINK:VENDOR_ID$$", "$$REDLINK:DEVICE_ID$$"), null, false);
                    NameOverride = devName;
                    if (resolvedDeviceEntry != null)
                        Icon = DeviceIcon.Auto;
                    else
                        Icon = DeviceIcon.Missing;
                }

                public override string ToString() => NameOverride ?? Device?.Key.Device ?? "???";
            }

            public class LabelAndValue
            {
                public readonly string Label;
                public readonly string Value;

                public LabelAndValue(string label, string value)
                {
                    Label = label;
                    Value = value;
                }

                public override string ToString() => Label;
            }

            public void ImportDeviceDefinitions()
            {
                try
                {
                    var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Locate the MCUXpresso workspace..." };
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return;

                    var packagesDir = System.IO.Path.Combine(dlg.SelectedPath, RedLinkDeviceDatabase.DeviceSupportFilesFolder);
                    if (!Directory.Exists(packagesDir))
                        throw new Exception("Missing " + packagesDir);

                    int count = _Database.ImportDefinitionsFromFolder(packagesDir);
                    if (count == 0)
                        _Host.GUIService.Report("Could not find any device definitions in " + packagesDir);
                    else
                        _Host.GUIService.Report($"Imported {count} device definitions from " + packagesDir);

                    ParseCommandLine();
                }
                catch (Exception ex)
                {
                    _Host.GUIService.Report(ex.Message, System.Windows.Forms.MessageBoxIcon.Error);
                }
            }
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Locate MCUXpresso IDE", Filter = "MCUXpresso IDE executable|mcuxpressoide.exe|All Files|*.*" };
            if (dlg.ShowDialog() == true)
                Controller.MCUXpressoDirectory = System.IO.Path.GetDirectoryName(dlg.FileName);
        }

        private void HackPopupDataContext(object sender, RoutedEventArgs e)
        {
            (sender as FrameworkElement).SetBinding(FrameworkElement.DataContextProperty, new Binding { Source = this });
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            Controller.ImportDeviceDefinitions();
        }
    }

    [Obfuscation(Exclude = true)]
    public class MaxWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double)
                return (double)value * 0.75;
            else
                return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
