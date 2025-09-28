using System;
using System.Collections.Generic;
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
    private string? _autoSavePath;
    private readonly Stopwatch _translationStopwatch = new();

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
            _autoSavePath = null;
            FilePathBox.Text = path;
            FooterText.Text = $"불러온 항목: {_entries.Count}개";
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
        if (_translationCancellation is { IsCancellationRequested: false })
        {
            StatusText.Text = "번역을 중지합니다...";
            _translationCancellation.Cancel();
            return;
        }

        if (_translationCancellation is { IsCancellationRequested: true })
        {
            return;
        }

        ResetState(preserveStatus: false);
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

        await TryApplyExistingTranslationAsync(targetLanguage).ConfigureAwait(true);

        _translationCancellation = new CancellationTokenSource();

        try
        {
            SetTranslationUiState(isTranslating: true);
            _autoSavePath = BuildAutoSavePath(targetLanguage);
            _translationStopwatch.Restart();
            var translated = await TranslateAsync(apiKey, modelName, sourceLanguage, targetLanguage, preserveFormatting, _autoSavePath, _translationCancellation.Token).ConfigureAwait(true);
            _translationStopwatch.Stop();
            var elapsed = FormatElapsed(_translationStopwatch.Elapsed);
            if (translated)
            {
                StatusText.Text = string.IsNullOrWhiteSpace(_autoSavePath)
                    ? $"번역이 완료되었습니다. (소요 시간: {elapsed})"
                    : $"번역이 완료되었습니다. {_autoSavePath} 저장됨 (소요 시간: {elapsed})";
            }
            else
            {
                StatusText.Text = string.IsNullOrWhiteSpace(_autoSavePath)
                    ? $"이미 번역된 자막입니다. (확인 시간: {elapsed})"
                    : $"{_autoSavePath}에서 번역을 불러왔습니다. (확인 시간: {elapsed})";
            }
        }
        catch (OperationCanceledException)
        {
            _translationStopwatch.Stop();
            StatusText.Text = "번역이 취소되었습니다.";
            ResetState(preserveStatus: true);
        }
        catch (Exception ex)
        {
            _translationStopwatch.Stop();
            MessageBox.Show(this, ex.Message, "번역 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "오류가 발생했습니다.";
        }
        finally
        {
            SetTranslationUiState(isTranslating: false);
            _translationCancellation = null;
        }
    }

    private async Task<bool> TranslateAsync(string apiKey, string? modelName, string sourceLanguage, string targetLanguage, bool preserveFormatting, string? autoSavePath, CancellationToken cancellationToken)
    {
        var batches = _entries
            .Where(NeedsTranslation)
            .OrderBy(e => e.Index)
            .ToList();

        if (batches.Count == 0)
        {
            Progress.Visibility = Visibility.Collapsed;
            Progress.Value = 0;
            if (string.IsNullOrWhiteSpace(_autoSavePath))
            {
                FooterText.Text = $"목표 언어: {targetLanguage} | 완료: {_entries.Count}/{_entries.Count}";
            }
            else
            {
                FooterText.Text = $"목표 언어: {targetLanguage} | 완료: {_entries.Count}/{_entries.Count} | 자동 저장: {_autoSavePath}";
            }
            return false;
        }

        var total = batches.Count;
        var processed = 0;

        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = "Gemini 번역 요청 중...";

        for (var i = 0; i < batches.Count; i += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = batches.Skip(i).Take(BatchSize).ToList();
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

            if (!string.IsNullOrWhiteSpace(autoSavePath))
            {
                await SaveAutoTranslatedFileAsync(autoSavePath, cancellationToken).ConfigureAwait(true);
            }
        }

        return true;
    }

    private void UpdateProgress(int processed, int total, string targetLanguage)
    {
        var percentage = total == 0 ? 0 : processed * 100.0 / total;
        Progress.Value = percentage;
        StatusText.Text = $"{processed}/{total} 문장 번역 ({percentage:0.#}%)";
        if (string.IsNullOrWhiteSpace(_autoSavePath))
        {
            FooterText.Text = $"목표 언어: {targetLanguage} | 완료: {processed}/{total}";
        }
        else
        {
            FooterText.Text = $"목표 언어: {targetLanguage} | 완료: {processed}/{total} | 자동 저장: {_autoSavePath}";
        }
    }

    private void SetTranslationUiState(bool isTranslating)
    {
        TranslateButton.IsEnabled = !isTranslating;
        Progress.Visibility = isTranslating ? Visibility.Visible : Visibility.Collapsed;
        Progress.Value = 0;
        ModelBox.IsEnabled = !isTranslating;
        BrowseButton.IsEnabled = !isTranslating;
        ResetButton.Content = isTranslating ? "번역 중지" : "초기화";
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

    private void ResetState(bool preserveStatus)
    {
        if (_translationCancellation is { IsCancellationRequested: false } cts)
        {
            cts.Cancel();
        }

        _entries.Clear();
        _loadedFilePath = null;
        _autoSavePath = null;
        _translationStopwatch.Reset();
        FilePathBox.Text = string.Empty;
        FooterText.Text = "SRT 파일을 불러와 주세요.";
        if (!preserveStatus)
        {
            StatusText.Text = string.Empty;
        }

        SetTranslationUiState(isTranslating: false);
    }

    private async Task TryApplyExistingTranslationAsync(string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(_loadedFilePath))
        {
            return;
        }

        var path = BuildAutoSavePath(targetLanguage);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            var translatedEntries = await SrtService.LoadAsync(path).ConfigureAwait(true);
            var translatedLookup = translatedEntries.ToDictionary(e => e.Index);

            foreach (var entry in _entries)
            {
                if (translatedLookup.TryGetValue(entry.Index, out var translated))
                {
                    if (!string.Equals(entry.Text?.Trim(), translated.Text?.Trim(), StringComparison.Ordinal))
                    {
                        entry.TranslatedText = translated.Text;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"기존 번역 파일 로드 실패: {ex.Message}");
        }
    }

    private static bool NeedsTranslation(SubtitleEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.TranslatedText))
        {
            return true;
        }

        return string.Equals(entry.Text?.Trim(), entry.TranslatedText.Trim(), StringComparison.Ordinal);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}시간 {elapsed.Minutes:D2}분 {elapsed.Seconds:D2}초";
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return $"{(int)elapsed.TotalMinutes}분 {elapsed.Seconds:D2}초";
        }

        return elapsed.TotalSeconds < 1
            ? $"{elapsed.TotalMilliseconds:0}ms"
            : $"{elapsed.TotalSeconds:0.0}초";
    }

    private async Task SaveAutoTranslatedFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await SrtService.SaveAsync(path, _entries, useTranslatedText: true, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"자동 저장 실패: {ex.Message}");
            throw;
        }
    }

    private string? BuildAutoSavePath(string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(_loadedFilePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(_loadedFilePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(_loadedFilePath);
        var extension = Path.GetExtension(_loadedFilePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".srt";
        }

        var suffix = string.IsNullOrWhiteSpace(targetLanguage) ? "translated" : targetLanguage.Trim();
        return Path.Combine(directory, $"{fileName}.{suffix}{extension}");
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
