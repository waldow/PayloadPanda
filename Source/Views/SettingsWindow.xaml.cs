using System.IO;
using System.Windows;
using Microsoft.Win32;
using PayloadPanda.Models;

namespace PayloadPanda.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsModel _settings;

    public SettingsWindow(SettingsModel settings, string[] availableModels)
    {
        InitializeComponent();
        _settings = settings;

        AiModelBox.ItemsSource = availableModels;
        PopulateFromSettings();
    }

    private void PopulateFromSettings()
    {
        HistoryPathBox.Text = _settings.HistoryFilePath ?? string.Empty;
        MaxHistoryBox.Text = _settings.MaxHistoryItems.ToString();
        TimeoutBox.Text = _settings.DefaultTimeoutSeconds.ToString();
        FollowRedirectsBox.IsChecked = _settings.DefaultFollowRedirects;
        SslVerificationBox.IsChecked = _settings.SslCertificateVerification;
        FontSizeBox.Text = _settings.EditorFontSize.ToString();
        WordWrapBox.IsChecked = _settings.EditorWordWrap;
        ApiKeyBox.Password = _settings.OpenAiApiKey ?? string.Empty;
        AiEndpointBox.Text = _settings.AiEndpoint;
        AiModelBox.SelectedItem = _settings.AiDefaultModel;
        if (AiModelBox.SelectedIndex < 0 && AiModelBox.Items.Count > 0)
            AiModelBox.SelectedIndex = 0;
        AiTimeoutBox.Text = _settings.AiTimeoutSeconds.ToString();
    }

    private bool ValidateAndApply()
    {
        // Validate history path
        var historyPath = HistoryPathBox.Text.Trim();
        if (!string.IsNullOrEmpty(historyPath))
        {
            var dir = Path.GetDirectoryName(historyPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir!); }
                catch
                {
                    MessageBox.Show("Invalid history file directory.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
        }

        // Validate max history
        if (!int.TryParse(MaxHistoryBox.Text, out var maxHistory) || maxHistory < 0)
        {
            MessageBox.Show("Max history items must be 0 or greater.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Validate timeout
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 1 || timeout > 300)
        {
            MessageBox.Show("Default timeout must be between 1 and 300 seconds.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Validate font size
        if (!int.TryParse(FontSizeBox.Text, out var fontSize) || fontSize < 8 || fontSize > 36)
        {
            MessageBox.Show("Font size must be between 8 and 36.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Validate AI timeout
        if (!int.TryParse(AiTimeoutBox.Text, out var aiTimeout) || aiTimeout < 1 || aiTimeout > 300)
        {
            MessageBox.Show("AI timeout must be between 1 and 300 seconds.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Apply to settings model
        _settings.HistoryFilePath = string.IsNullOrWhiteSpace(historyPath) ? null : historyPath;
        _settings.MaxHistoryItems = maxHistory;
        _settings.DefaultTimeoutSeconds = timeout;
        _settings.DefaultFollowRedirects = FollowRedirectsBox.IsChecked == true;
        _settings.SslCertificateVerification = SslVerificationBox.IsChecked == true;
        _settings.EditorFontSize = fontSize;
        _settings.EditorWordWrap = WordWrapBox.IsChecked == true;
        _settings.OpenAiApiKey = string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? null : ApiKeyBox.Password;
        _settings.AiEndpoint = string.IsNullOrWhiteSpace(AiEndpointBox.Text)
            ? "https://api.openai.com/v1/chat/completions"
            : AiEndpointBox.Text.Trim();
        _settings.AiDefaultModel = AiModelBox.SelectedItem as string ?? "gpt-5-nano";
        _settings.AiTimeoutSeconds = aiTimeout;

        return true;
    }

    private void BrowseHistoryPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = "history.json",
            Title = "Choose history file location"
        };

        if (!string.IsNullOrWhiteSpace(HistoryPathBox.Text))
        {
            var dir = Path.GetDirectoryName(HistoryPathBox.Text);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dialog.InitialDirectory = dir;
        }

        if (dialog.ShowDialog() == true)
            HistoryPathBox.Text = dialog.FileName;
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new SettingsModel();
        HistoryPathBox.Text = string.Empty;
        MaxHistoryBox.Text = defaults.MaxHistoryItems.ToString();
        TimeoutBox.Text = defaults.DefaultTimeoutSeconds.ToString();
        FollowRedirectsBox.IsChecked = defaults.DefaultFollowRedirects;
        SslVerificationBox.IsChecked = defaults.SslCertificateVerification;
        FontSizeBox.Text = defaults.EditorFontSize.ToString();
        WordWrapBox.IsChecked = defaults.EditorWordWrap;
        ApiKeyBox.Password = string.Empty;
        AiEndpointBox.Text = defaults.AiEndpoint;
        AiModelBox.SelectedItem = defaults.AiDefaultModel;
        AiTimeoutBox.Text = defaults.AiTimeoutSeconds.ToString();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateAndApply())
        {
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
