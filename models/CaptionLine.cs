using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveCaptionsTranslator.models
{
    public class CaptionLine : INotifyPropertyChanged
    {
        private string _original = "";
        private string _translation = "";
        private string _error = "";
        private bool _isFinal;
        private bool _isTranslating;

        public string Original
        {
            get => _original;
            set
            {
                if (_original == value)
                    return;
                _original = value;
                OnPropertyChanged();
            }
        }

        public string Translation
        {
            get => _translation;
            set
            {
                if (_translation == value)
                    return;
                _translation = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTranslation));
                OnPropertyChanged(nameof(IsPending));
            }
        }

        // The error lives separately from the translation: a previous successful translation stays visible,
        // and a new line cannot "inherit" it via a donor.
        public string Error
        {
            get => _error;
            set
            {
                if (_error == value)
                    return;
                _error = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(HasTranslation));
                OnPropertyChanged(nameof(IsPending));
            }
        }

        public bool IsFinal
        {
            get => _isFinal;
            set
            {
                if (_isFinal == value)
                    return;
                _isFinal = value;
                OnPropertyChanged();
            }
        }

        public bool IsTranslating
        {
            get => _isTranslating;
            set
            {
                if (_isTranslating == value)
                    return;
                _isTranslating = value;
                OnPropertyChanged();
            }
        }

        public bool HasError => _error.Length > 0;

        public bool HasTranslation => _error.Length == 0 && _translation.Length > 0;

        public bool IsPending => _error.Length == 0 && _translation.Length == 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
