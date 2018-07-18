using BSPEngine;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
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

namespace RenesasDebugPackage.GUI
{
    /// <summary>
    /// Interaction logic for RenesasDebugSettingsControl.xaml
    /// </summary>
    public partial class RenesasDebugSettingsControl : UserControl
    {
        public RenesasDebugSettingsControl(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host, RenesasDebugController controller)
        {
            InitializeComponent();
            DataContext = Controller = new ControllerImpl(this, method, host, controller);
        }

        public ControllerImpl Controller { get; }

        public class ControllerImpl : ICustomDebugMethodConfigurator, INotifyPropertyChanged
        {
            void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

            RenesasGDBServerCommandLine _CommandLine;

            public ControllerImpl(RenesasDebugSettingsControl control, LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host, RenesasDebugController controller)
            {
                Control = control;
                TypeProvider = controller;
                _CommandLine = new RenesasGDBServerCommandLine(new RenesasDebugSettings().CommandLineArguments);
            }

            public string CommandLine
            {
                get => _CommandLine.CommandLine;
                set
                {
                    _CommandLine.CommandLine = value;
                    OnCommandLineChanged();
                }
            }

            void OnCommandLineChanged()
            {
                OnPropertyChanged(nameof(CommandLine));
            }

            public object Control { get; }

            public object Configuration => new RenesasDebugSettings { CommandLineArguments = _CommandLine.CommandLine };

            public ICustomSettingsTypeProvider TypeProvider { get; }

            public bool SupportsLiveVariables => false;

            public event EventHandler SettingsChanged;
            public event PropertyChangedEventHandler PropertyChanged;

            public void SetConfiguration(object configuration, KnownInterfaceInstance context)
            {
                _CommandLine = new RenesasGDBServerCommandLine(((configuration as RenesasDebugSettings) ?? new RenesasDebugSettings()).CommandLineArguments);
            }

            public bool TryFixSettingsFromStubError(IGDBStubInstance stub)
            {
                return false;
            }

            public string ValidateSettings()
            {
                return null;
            }
        }
    }

    public class MaxWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double)
                return (double)value * 1;
            else
                return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
