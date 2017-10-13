using BSPEngine;
using OpenOCDPackage;
using System;
using System.Collections.Generic;
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
using System.Globalization;
using System.Reflection;
using Microsoft.Win32;

namespace ESP8266DebugPackage.GUI
{
    /// <summary>
    /// Interaction logic for ESPxxOpenOCDSettingsControl.xaml
    /// </summary>
    public partial class ESPxxOpenOCDSettingsControl : UserControl, ICustomDebugMethodConfigurator
    {
        private ESPxxOpenOCDSettingsEditor _Editor;

        private LoadedBSP.LoadedDebugMethod _Method;
        private IBSPConfiguratorHost _Host;
        private readonly bool _IsESP32;

        public ESPxxOpenOCDSettingsControl(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host, ICustomSettingsTypeProvider typeProvider, bool isESP32)
        {
            _Method = method;
            _Host = host;
            _IsESP32 = isESP32;
            TypeProvider = typeProvider;
            host.InstallStyles(this);
            InitializeComponent();

            host.MakeSearchableComboBox(InterfaceComboBox, (i, f) => _Editor?.FilterItem(i, f) ?? false, Resources["interfaceScriptSelectionControl"]);
        }


        public object Control => this;

        public object Configuration => _Editor?.Settings;


        public ICustomSettingsTypeProvider TypeProvider { get; private set; }

        public bool SupportsLiveVariables => true;

        public event EventHandler SettingsChanged;

        public void SetConfiguration(object configuration, KnownInterfaceInstance context)
        {
            ESPxxOpenOCDSettings settings;
            if (_IsESP32)
                settings = configuration as ESP32OpenOCDSettings;
            else
                settings = configuration as ESP8266OpenOCDSettings;

            _Editor = new ESPxxOpenOCDSettingsEditor(_Host, _Method.Directory, settings, context, _IsESP32);

            _Editor.PropertyChanged += (s, e) => SettingsChanged?.Invoke(this, EventArgs.Empty);
            DataContext = _Editor;
        }

        public bool TryFixSettingsFromStubError(IGDBStubInstance stub) => _Editor.TryFixSettingsFromStubError(stub);

        public string ValidateSettings() => _Editor?.ValidateSettings();

        private void HackPopupDataContext(object sender, RoutedEventArgs e)
        {
            (sender as FrameworkElement).SetBinding(FrameworkElement.DataContextProperty, new Binding { Source = this });
        }

        private void SelectScriptManually(object sender, RoutedEventArgs e)
        {
            try
            {
                ((sender as FrameworkElement).DataContext as OpenOCDSettingsEditor.IBrowseableScriptSelector)?.Browse();
            }
            catch (Exception ex)
            {
                _Host.ReportException(ex);
            }
        }

        private void RaiseSettingsChangedEvent(object sender, SelectionChangedEventArgs e)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void AddNewResource(object sender, RoutedEventArgs e)
        {
            _Editor.FLASHResources.Add(new FLASHResource());
        }

        private void BrowseResource_Click(object sender, RoutedEventArgs e)
        {
            var rsrc = (sender as FrameworkElement)?.DataContext as FLASHResource;
            if (rsrc != null)
            {
                var dlg = new OpenFileDialog { Filter = "All files|*.*" };
                if (dlg.ShowDialog() == true)
                    rsrc.UpdatePath(dlg.FileName);
            }
        }

        private void RemoveResource_Click(object sender, RoutedEventArgs e)
        {
            var rsrc = (sender as FrameworkElement)?.DataContext as FLASHResource;
            if (rsrc != null)
                _Editor.FLASHResources.Remove(rsrc);
        }
    }

    public class AnnotatedValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return "";

            foreach (FieldInfo fld in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!fld.GetValue(null).Equals(value))
                    continue;

                var attr = fld.GetCustomAttributes(typeof(ArgumentValueAttribute), false);
                if (attr != null && attr.Length > 0)
                    return (attr[0] as ArgumentValueAttribute).Hint;
            }

            return value.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public static class Options
    {
        public static Array FLASHSizes => Enum.GetValues(typeof(ESP8266BinaryImage.FLASHSize));
        public static Array ESP32FLASHSizes => Enum.GetValues(typeof(ESP8266BinaryImage.ESP32FLASHSize));
        public static Array FLASHModes => Enum.GetValues(typeof(ESP8266BinaryImage.FLASHMode));
        public static Array FLASHFrequencies => Enum.GetValues(typeof(ESP8266BinaryImage.FLASHFrequency));
        public static Array ResetModes => Enum.GetValues(typeof(ResetMode));
        public static AnnotatedValueConverter Converter { get; } = new AnnotatedValueConverter();
    }
}
