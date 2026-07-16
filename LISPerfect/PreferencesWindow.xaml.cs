using System.Windows;

namespace LISPerfect
{
    public partial class PreferencesWindow : Window
    {
        private readonly Settings _settings;

        public PreferencesWindow(Settings settings)
        {
            InitializeComponent();
            _settings = settings;

            ParedItCheckBox.IsChecked = _settings.ParedItEnabled;
            AutoSaveOnFocusLossCheckBox.IsChecked = _settings.AutoSaveOnFocusLoss;
            AutoSaveOnIntervalCheckBox.IsChecked = _settings.AutoSaveOnInterval;
            AutoSaveIntervalBox.Text = _settings.AutoSaveIntervalSeconds.ToString();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            _settings.ParedItEnabled = ParedItCheckBox.IsChecked ?? false;
            _settings.AutoSaveOnFocusLoss = AutoSaveOnFocusLossCheckBox.IsChecked ?? false;
            _settings.AutoSaveOnInterval = AutoSaveOnIntervalCheckBox.IsChecked ?? false;

            if (int.TryParse(AutoSaveIntervalBox.Text, out int seconds) && seconds >= 5)
            {
                _settings.AutoSaveIntervalSeconds = seconds;
            }
            // Otherwise silently keep the previous value.

            DialogResult = true;
            Close();
        }
    }
}