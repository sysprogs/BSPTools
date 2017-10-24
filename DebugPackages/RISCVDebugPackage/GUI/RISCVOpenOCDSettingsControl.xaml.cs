using BSPEngine;
using OpenOCDPackage;
using System;
using System.Collections.Generic;
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

namespace RISCVDebugPackage.GUI
{
    /// <summary>
    /// Interaction logic for RISCVOpenOCDSettingsControl.xaml
    /// </summary>
    public partial class RISCVOpenOCDSettingsControl : UserControl, ICustomDebugMethodConfigurator
    {
        private readonly LoadedBSP.LoadedDebugMethod _Method;
        private readonly IBSPConfiguratorHost _Host;

        public event EventHandler SettingsChanged;
        RISCVOpenOCDSettingsEditor _Editor;


        public ICustomSettingsTypeProvider TypeProvider { get; }

        public object Control => this;

        public object Configuration => _Editor.Settings;

        public bool SupportsLiveVariables => true;

        public RISCVOpenOCDSettingsControl(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host, ICustomSettingsTypeProvider typeProvider)
        {
            _Method = method;
            _Host = host;
            TypeProvider = typeProvider;
            host.InstallStyles(this);

            InitializeComponent();

            host.MakeSearchableComboBox(InterfaceComboBox, (i, f) => _Editor?.FilterItem(i, f) ?? false, Resources["interfaceScriptSelectionControl"]);
            host.MakeSearchableComboBox(DeviceComboBox, (i, f) => _Editor?.FilterItem(i, f) ?? false, Resources["deviceScriptSelectionControl"]);
        }

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

        private void ResetToDefaultDevice(object sender, RoutedEventArgs e)
        {
            _Editor.ResetToDefaultDevice();
            DeviceComboBox.IsDropDownOpen = false;
        }

        public void SetConfiguration(object configuration, KnownInterfaceInstance context)
        {
            var settings = configuration as RISCVOpenOCDSettings;
            _Editor = new RISCVOpenOCDSettingsEditor(_Host, _Method.Directory, settings, context);

            _Editor.PropertyChanged += (s, e) => SettingsChanged?.Invoke(this, EventArgs.Empty);
            DataContext = _Editor;
        }

        public string ValidateSettings() => _Editor?.ValidateSettings();

        public bool TryFixSettingsFromStubError(IGDBStubInstance stub) => _Editor.TryFixSettingsFromStubError(stub);
    }

    public class ShowOnlyInListViewConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isInsidePopup = false;
            for (DependencyObject obj = value as DependencyObject; obj != null; obj = VisualTreeHelper.GetParent(obj))
            {
                if (obj is ComboBoxItem)
                {
                    isInsidePopup = true;
                    break;
                }
            }

            return isInsidePopup ? Visibility.Visible : Visibility.Hidden;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

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
