using System.Windows;
using TransIt.Core;

namespace TransIt.Windows.Settings;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        if (_settings.Provider == TranslationProvider.OpenAI)
            RbOpenAI.IsChecked = true;
        else
            RbGoogle.IsChecked = true;

        TbOpenAiKey.Password = _settings.OpenAiApiKey;
        TbGoogleKey.Password = _settings.GoogleApiKey;
        TbModel.Text         = _settings.OpenAiModel;
        TbSourceLang.Text    = _settings.SourceLanguage;
        TbTargetLang.Text    = _settings.TargetLanguage;
        SlInterval.Value     = _settings.RealtimeIntervalMs;
        LblInterval.Text     = $"{_settings.RealtimeIntervalMs} ms";
        RbOverlayMode.IsChecked   = _settings.RegionOverlayMode;
        RbTextPaneMode.IsChecked  = !_settings.RegionOverlayMode;
        CbVisionApi.IsChecked     = _settings.UseVisionApi;

        UpdateProviderPanels();
    }

    private void UpdateProviderPanels()
    {
        bool openAi = RbOpenAI.IsChecked == true;
        OpenAiPanel.Visibility = openAi ? Visibility.Visible : Visibility.Collapsed;
        GooglePanel.Visibility = openAi ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Provider_Checked(object sender, RoutedEventArgs e) => UpdateProviderPanels();

    private void TbOpenAiKey_Changed(object sender, RoutedEventArgs e) { /* no action needed */ }
    private void TbGoogleKey_Changed(object sender, RoutedEventArgs e) { /* no action needed */ }

    private void SlInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblInterval != null)
            LblInterval.Text = $"{(int)e.NewValue} ms";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.Provider        = RbOpenAI.IsChecked == true
                                     ? TranslationProvider.OpenAI
                                     : TranslationProvider.Google;
        _settings.OpenAiApiKey    = TbOpenAiKey.Password;
        _settings.GoogleApiKey    = TbGoogleKey.Password;
        _settings.OpenAiModel     = TbModel.Text;
        _settings.SourceLanguage  = TbSourceLang.Text.Trim();
        _settings.TargetLanguage  = TbTargetLang.Text.Trim();
        _settings.RealtimeIntervalMs = (int)SlInterval.Value;
        _settings.RegionOverlayMode  = RbOverlayMode.IsChecked == true;
        _settings.UseVisionApi       = CbVisionApi.IsChecked == true;
        _settings.Save();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
