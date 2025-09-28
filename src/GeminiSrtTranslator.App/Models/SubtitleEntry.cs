using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GeminiSrtTranslator.Models;

public class SubtitleEntry : INotifyPropertyChanged
{
    private string? _translatedText;

    public int Index { get; init; }

    public TimeSpan StartTime { get; init; }

    public TimeSpan EndTime { get; init; }

    public string Text { get; init; } = string.Empty;

    public string? TranslatedText
    {
        get => _translatedText;
        set
        {
            if (_translatedText == value)
            {
                return;
            }

            _translatedText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
