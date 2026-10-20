using System.ComponentModel;
using System.Windows.Input;
using Kairos.Engine.Voice;

namespace Kairos.Ui;

/// <summary>A stable editable row: polling never discards a name the user is still typing.</summary>
public sealed class VoicePhraseRowViewModel : INotifyPropertyChanged
{
    readonly Func<bool> _canEdit;
    readonly Action<VoicePhraseCommand> _send;
    string _name, _draftName;
    bool _enabled;

    internal VoicePhraseRowViewModel(VoicePhraseInfo phrase, Func<bool> canEdit, Action<VoicePhraseCommand> send)
    {
        Id = phrase.Id;
        Action = phrase.Action;
        _name = _draftName = phrase.Name;
        _enabled = phrase.Enabled;
        _canEdit = canEdit;
        _send = send;
        RenameCommand = new RelayCommand(_ => _send(new("rename", Id, Name: DraftName.Trim())),
            _ => CanEdit && NameChanged && !string.IsNullOrWhiteSpace(DraftName));
        TestCommand = new RelayCommand(_ => _send(new("test", Id)), _ => CanEdit);
        RerecordCommand = new RelayCommand(_ => _send(new("rerecord", Id)), _ => CanEdit);
        DeleteCommand = new RelayCommand(_ => _send(new("delete", Id)), _ => CanEdit);
    }

    public string Id { get; }
    public string Action { get; }
    public string Name => _name;
    public bool CanEdit => _canEdit();
    public bool NameChanged => DraftName.Trim() != Name;
    public string DraftName
    {
        get => _draftName;
        set
        {
            if (_draftName == value)
                return;
            _draftName = value ?? "";
            Raise(nameof(DraftName));
            Raise(nameof(NameChanged));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value || !CanEdit)
                return;
            _enabled = value;
            Raise(nameof(Enabled));
            _send(new("enabled", Id, Enabled: value));
        }
    }
    public ICommand RenameCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand RerecordCommand { get; }
    public ICommand DeleteCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(VoicePhraseInfo phrase)
    {
        if (_name != phrase.Name)
        {
            bool keepDraft = NameChanged;
            _name = phrase.Name;
            Raise(nameof(Name));
            if (!keepDraft)
            {
                _draftName = phrase.Name;
                Raise(nameof(DraftName));
            }
            Raise(nameof(NameChanged));
        }
        if (_enabled != phrase.Enabled)
        {
            _enabled = phrase.Enabled;
            Raise(nameof(Enabled));
        }
    }

    internal void RefreshCommands() => Raise(nameof(CanEdit));
    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
