using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GeminiSrtTranslator.Models;
using GeminiSrtTranslator.Services;
using Microsoft.Win32;

namespace GeminiSrtTranslator;

public partial class MainWindow : Window
{
    private const int BatchSize = 8;

    private readonly ObservableCollection<SubtitleEntry> _entries = new();
    private readonly GeminiTranslationService _translationService = new(new HttpClient());
    private readonly AppSettings _settings;

    private CancellationTokenSource? _translationCancellation;
    private string? _loadedFilePath;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();

        if (string.IsNullOrWhiteSpace(_settings.PreferredModel))
        {
            _settings.PreferredModel = GeminiTranslationService.DefaultModelName;
            try
            {
                SettingsService.Save(_settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to persist default model: {ex.Message}");
            }
        }

        SubtitleGrid.ItemsSource = _entries;
        FooterText.Text = "SRT 파일을 불러와 주세요.";
        if (!string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
        {
            ApiKeyBox.Password = _settings.GeminiApiKey;
        }

        ModelBox.Text = string.IsNullOrWhiteSpace(_settings.PreferredModel)
            ? GeminiTranslationService.DefaultModelName
            : _settings.PreferredModel;

        ApiKeyBox.PasswordChanged += OnApiKeyChanged;
        ModelBox.LostFocus += OnModelTextChanged;
    }

    private async void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SRT 파일 (*.srt)|*.srt|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await LoadSrtAsync(dialog.FileName);
    }

    private async Task LoadSrtAsync(string path)
    {
        try
        {
            SetBusyState(true, "자막을 읽는 중...");
            var entries = await SrtService.LoadAsync(path).ConfigureAwait(true);

            _entries.Clear();
            foreach (var entry in entries)
            {
                _entries.Add(entry);
            }

            _loadedFilePath = path;
            FilePathBox.Text = path;
            FooterText.Text = $"불러온 항목: {_entries.Count}개";
            SaveButton.IsEnabled = _entries.Any(e => !string.IsNullOrWhiteSpace(e.TranslatedText));
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "읽기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        _entries.Clear();
        _loadedFilePath = null;
        FilePathBox.Text = string.Empty;
        FooterText.Text = "SRT 파일을 불러와 주세요.";
        StatusText.Text = string.Empty;
        SaveButton.IsEnabled = false;
    }

    private async void OnTranslateClicked(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0)
        {
            MessageBox.Show(this, "번역할 자막이 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var apiKey = ApiKeyBox.Password?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(this, "Gemini API 키를 입력하세요.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PersistApiKey(apiKey);

        var sourceLanguage = GetSelectedLanguage(SourceLanguageBox) ?? "auto";
        var targetLanguage = GetSelectedLanguage(TargetLanguageBox) ?? "ko";
        var preserveFormatting = PreserveFormattingBox.IsChecked == true;
        var modelName = GetSelectedModel();
        PersistSelectedModel(modelName);

        _translationCancellation = new CancellationTokenSource();

        try
        {
            SetTranslationUiState(isTranslating: true);
            await TranslateAsync(apiKey, modelName, sourceLanguage, targetLanguage, preserveFormatting, _translationCancellation.Token);
            StatusText.Text = "번역이 완료되었습니다.";
            SaveButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "번역이 취소되었습니다.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "번역 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "오류가 발생했습니다.";
        }
        finally
        {
            SetTranslationUiState(isTranslating: false);
            _translationCancellation = null;
        }
    }

    private async Task TranslateAsync(string apiKey, string? modelName, string sourceLanguage, string targetLanguage, bool preserveFormatting, CancellationToken cancellationToken)
    {
        var total = _entries.Count;
        var processed = 0;

        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = "Gemini 번역 요청 중...";

        var orderedEntries = _entries.OrderBy(e => e.Index).ToList();

        for (var i = 0; i < orderedEntries.Count; i += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = orderedEntries.Skip(i).Take(BatchSize).ToList();
            var translations = await _translationService.TranslateBatchAsync(batch, apiKey, modelName, sourceLanguage, targetLanguage, preserveFormatting, cancellationToken).ConfigureAwait(true);

            foreach (var entry in batch)
            {
                if (translations.TryGetValue(entry.Index, out var translation))
                {
                    entry.TranslatedText = translation.Trim();
                }
            }

            processed += batch.Count;
            UpdateProgress(processed, total, targetLanguage);
        }
    }

    private void UpdateProgress(int processed, int total, string targetLanguage)
    {
        var percentage = total == 0 ? 0 : processed * 100.0 / total;
        Progress.Value = percentage;
        StatusText.Text = $"{processed}/{total} 문장 번역 ({percentage:0.#}%)";
        FooterText.Text = $"목표 언어: {targetLanguage} | 완료: {processed}/{total}";
    }

    private void SetTranslationUiState(bool isTranslating)
    {
        TranslateButton.IsEnabled = !isTranslating;
        SaveButton.IsEnabled = !isTranslating && _entries.Any(e => !string.IsNullOrWhiteSpace(e.TranslatedText));
        Progress.Visibility = isTranslating ? Visibility.Visible : Visibility.Collapsed;
        Progress.Value = 0;
        ModelBox.IsEnabled = !isTranslating;
    }

    private void SetBusyState(bool isBusy, string? message = null)
    {
        Cursor = isBusy ? Cursors.Wait : Cursors.Arrow;
        Progress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = isBusy;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
        }
        else if (!isBusy)
        {
            Progress.IsIndeterminate = false;
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0)
        {
            MessageBox.Show(this, "저장할 자막이 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "SRT 파일 (*.srt)|*.srt|모든 파일 (*.*)|*.*",
            AddExtension = true,
            FileName = BuildSuggestedFileName()
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SaveTranslatedFileAsync(dialog.FileName).FireAndForgetSafeAsync();
    }

    private async Task SaveTranslatedFileAsync(string path)
    {
        try
        {
            SetBusyState(true, "번역 자막 저장 중...");
            await SrtService.SaveAsync(path, _entries, useTranslatedText: true).ConfigureAwait(true);
            StatusText.Text = "저장이 완료되었습니다.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "저장 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private string BuildSuggestedFileName()
    {
        if (string.IsNullOrWhiteSpace(_loadedFilePath))
        {
            return "translated.srt";
        }

        var directory = Path.GetDirectoryName(_loadedFilePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(_loadedFilePath);
        var extension = Path.GetExtension(_loadedFilePath);
        return Path.Combine(directory, $"{fileName}.translated{extension}");
    }

    private static string? GetSelectedLanguage(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is ComboBoxItem item && item.Content is string value)
        {
            return value;
        }

        return comboBox.Text;
    }

    private string GetSelectedModel()
    {
        var modelName = ModelBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            return modelName;
        }

        if (!string.IsNullOrWhiteSpace(_settings.PreferredModel))
        {
            return _settings.PreferredModel;
        }

        return GeminiTranslationService.DefaultModelName;
    }

    private void OnModelTextChanged(object sender, RoutedEventArgs e)
    {
        var modelName = ModelBox.Text?.Trim() ?? string.Empty;
        PersistSelectedModel(string.IsNullOrWhiteSpace(modelName) ? GeminiTranslationService.DefaultModelName : modelName);
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        var apiKey = ApiKeyBox.Password ?? string.Empty;
        apiKey = apiKey.Trim();
        PersistApiKey(apiKey);
    }

    private void PersistApiKey(string apiKey)
    {
        if (string.Equals(_settings.GeminiApiKey, apiKey, StringComparison.Ordinal))
        {
            return;
        }

        _settings.GeminiApiKey = apiKey;
        try
        {
            SettingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            StatusText.Text = "API 키 저장에 실패했습니다.";
            Debug.WriteLine($"Failed to persist API key: {ex.Message}");
        }
    }

    private void PersistSelectedModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            modelName = GeminiTranslationService.DefaultModelName;
        }

        if (string.Equals(_settings.PreferredModel, modelName, StringComparison.Ordinal))
        {
            return;
        }

        _settings.PreferredModel = modelName;
        try
        {
            SettingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to persist model: {ex.Message}");
        }
    }
}

internal static class TaskExtensions
{
    public static async void FireAndForgetSafeAsync(this Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // UI 스레드에 안전하게 예외를 보고
            Application.Current.Dispatcher.Invoke(() =>
                MessageBox.Show(ex.Message, "비동기 작업 오류", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }
}
