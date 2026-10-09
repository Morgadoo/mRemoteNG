using Avalonia.Media;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// One editable connection property in the connection editor, with its "inherit from folder" checkbox.
/// While <see cref="Inherit"/> is on the editor shows the folder's value read-only; the node's own value
/// is kept and restored when inheritance is switched off. Edits are buffered until <see cref="ApplyTo"/>.
/// </summary>
public abstract class PropertyFieldViewModel : ReactiveObject
{
    private readonly object? _parentValue;
    private readonly object? _originalValue;
    private readonly bool _originalInherit;
    private readonly bool _alwaysEditable;
    private object? _own;
    private bool _inherit;
    private bool _isVisible = true;
    private string? _error;

    /// <param name="alwaysEditable">
    /// True when the Inherit box only records a flag and never replaces the value (the default connection).
    /// </param>
    protected PropertyFieldViewModel(ConnectionPropertyDescriptor descriptor, ConnectionInfo target,
        ContainerInfo? parent, bool canInherit, bool alwaysEditable, string parentName)
    {
        Descriptor = descriptor;
        _alwaysEditable = alwaysEditable;
        SupportsInheritance = descriptor.SupportsInheritance;
        CanInherit = canInherit && SupportsInheritance;
        _own = ConnectionInheritanceAccessor.GetOwnValue<object?>(target, descriptor.Name);
        _inherit = CanInherit && ConnectionInheritanceAccessor.GetInheritFlag(target, descriptor.Name);
        _parentValue = CanInherit && parent is not null
            ? ConnectionInheritanceAccessor.GetValue<object?>(parent, descriptor.Name)
            : _own;
        _originalValue = _own;
        _originalInherit = _inherit;
        InheritTip = alwaysEditable
            ? "New connections inherit this property from their folder."
            : CanInherit ? $"Use the value of the folder \"{parentName}\"." : "Nodes directly under the root have nothing to inherit from.";
    }

    public ConnectionPropertyDescriptor Descriptor { get; }
    public string Name => Descriptor.Name;
    public string DisplayName => Descriptor.DisplayName;
    public string Description => Descriptor.Description;

    /// <summary>True when the property has an inheritance flag at all.</summary>
    public bool SupportsInheritance { get; }

    /// <summary>True when the flag can be changed (the node is in a folder it can inherit from).</summary>
    public bool CanInherit { get; }

    public string InheritTip { get; }

    public bool Inherit
    {
        get => _inherit;
        set
        {
            if (!CanInherit || _inherit == value) return;
            this.RaiseAndSetIfChanged(ref _inherit, value);
            this.RaisePropertyChanged(nameof(IsEditable));
            OnValueChanged();
        }
    }

    public bool IsEditable => _alwaysEditable || !_inherit;

    /// <summary>False when the property does not apply to the edited protocol / settings.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    /// <summary>Validation message shown under the editor (or null).</summary>
    public string? Error
    {
        get => _error;
        set => this.RaiseAndSetIfChanged(ref _error, value);
    }

    /// <summary>The effective value: the folder's while inheriting, otherwise the node's own.</summary>
    public object? BoxedValue
    {
        get => _inherit && !_alwaysEditable ? _parentValue : _own;
        set
        {
            if (!IsEditable || Equals(_own, value)) return;
            _own = value;
            OnValueChanged();
        }
    }

    /// <summary>The node's own value (ignoring inheritance).</summary>
    public object? OwnValue => _own;

    /// <summary>True when the value or the inheritance flag differs from the node's.</summary>
    public bool IsModified => !Equals(_own, _originalValue) || _inherit != _originalInherit;

    /// <summary>Raised when <see cref="BoxedValue"/> changes (edited, or switched to/from the folder's value).</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Writes the own value and the inheritance flag to <paramref name="target"/>; true when anything changed.</summary>
    internal bool ApplyTo(ConnectionInfo target)
    {
        var changed = IsModified;
        ConnectionInheritanceAccessor.SetOwnValue(target, Name, _own);
        // Flags of nodes that cannot inherit (directly under the root) are inactive; keep them as loaded.
        if (CanInherit)
            ConnectionInheritanceAccessor.SetInheritFlag(target, Name, _inherit);
        return changed;
    }

    protected virtual void OnValueChanged()
    {
        this.RaisePropertyChanged(nameof(BoxedValue));
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static PropertyFieldViewModel Create(ConnectionPropertyDescriptor descriptor, ConnectionInfo target,
        ContainerInfo? parent, bool canInherit, bool alwaysEditable, string parentName,
        Func<ConnectionPropertySuggestions, IReadOnlyList<string>> suggestions) => descriptor.Editor switch
    {
        ConnectionPropertyEditor.Boolean => new BoolFieldViewModel(descriptor, target, parent, canInherit, alwaysEditable, parentName),
        ConnectionPropertyEditor.Number => new NumberFieldViewModel(descriptor, target, parent, canInherit, alwaysEditable, parentName),
        ConnectionPropertyEditor.Choice => new ChoiceFieldViewModel(descriptor, target, parent, canInherit, alwaysEditable, parentName),
        ConnectionPropertyEditor.Color => new ColorFieldViewModel(descriptor, target, parent, canInherit, alwaysEditable, parentName,
            suggestions(descriptor.Suggestions)),
        ConnectionPropertyEditor.Suggest => new SuggestFieldViewModel(descriptor, target, parent, canInherit, alwaysEditable, parentName,
            suggestions(descriptor.Suggestions)),
        _ => new TextFieldViewModel(descriptor, target, parent, canInherit, alwaysEditable, parentName),
    };
}

/// <summary>Free text (or a password when <see cref="IsPassword"/>).</summary>
public class TextFieldViewModel(ConnectionPropertyDescriptor descriptor, ConnectionInfo target, ContainerInfo? parent,
    bool canInherit, bool alwaysEditable, string parentName)
    : PropertyFieldViewModel(descriptor, target, parent, canInherit, alwaysEditable, parentName)
{
    public bool IsPassword => Descriptor.Editor == ConnectionPropertyEditor.Password;

    public char PasswordChar => IsPassword ? '●' : '\0';

    public string Value
    {
        get => BoxedValue as string ?? string.Empty;
        set => BoxedValue = value ?? string.Empty;
    }

    protected override void OnValueChanged()
    {
        base.OnValueChanged();
        this.RaisePropertyChanged(nameof(Value));
    }
}

/// <summary>Free text with suggested values (panels, PuTTY sessions, SSH tunnels, external tools).</summary>
public class SuggestFieldViewModel(ConnectionPropertyDescriptor descriptor, ConnectionInfo target, ContainerInfo? parent,
    bool canInherit, bool alwaysEditable, string parentName, IReadOnlyList<string> suggestions)
    : TextFieldViewModel(descriptor, target, parent, canInherit, alwaysEditable, parentName)
{
    public IReadOnlyList<string> Suggestions { get; } = suggestions;

    public bool HasSuggestions => Suggestions.Count > 0;
}

/// <summary>A colour name or #RRGGBB with a preview swatch.</summary>
public sealed class ColorFieldViewModel(ConnectionPropertyDescriptor descriptor, ConnectionInfo target, ContainerInfo? parent,
    bool canInherit, bool alwaysEditable, string parentName, IReadOnlyList<string> suggestions)
    : SuggestFieldViewModel(descriptor, target, parent, canInherit, alwaysEditable, parentName, suggestions)
{
    public IBrush Swatch => Color.TryParse(Value, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;

    protected override void OnValueChanged()
    {
        base.OnValueChanged();
        this.RaisePropertyChanged(nameof(Swatch));
    }
}

public sealed class BoolFieldViewModel(ConnectionPropertyDescriptor descriptor, ConnectionInfo target, ContainerInfo? parent,
    bool canInherit, bool alwaysEditable, string parentName)
    : PropertyFieldViewModel(descriptor, target, parent, canInherit, alwaysEditable, parentName)
{
    public bool Value
    {
        get => BoxedValue is true;
        set => BoxedValue = value;
    }

    protected override void OnValueChanged()
    {
        base.OnValueChanged();
        this.RaisePropertyChanged(nameof(Value));
    }
}

public sealed class NumberFieldViewModel(ConnectionPropertyDescriptor descriptor, ConnectionInfo target, ContainerInfo? parent,
    bool canInherit, bool alwaysEditable, string parentName)
    : PropertyFieldViewModel(descriptor, target, parent, canInherit, alwaysEditable, parentName)
{
    public decimal Minimum => Descriptor.Minimum;
    public decimal Maximum => Descriptor.Maximum;

    /// <summary>Bound to a NumericUpDown (which uses decimal?); stored as int.</summary>
    public decimal? Value
    {
        get => BoxedValue is int i ? i : 0;
        set => BoxedValue = (int)Math.Clamp(value ?? 0, Minimum, Maximum);
    }

    public int IntValue
    {
        get => BoxedValue is int i ? i : 0;
        set => BoxedValue = value;
    }

    protected override void OnValueChanged()
    {
        base.OnValueChanged();
        this.RaisePropertyChanged(nameof(Value));
        this.RaisePropertyChanged(nameof(IntValue));
    }
}

/// <summary>One value of an enum.</summary>
public sealed class ChoiceFieldViewModel : PropertyFieldViewModel
{
    public ChoiceFieldViewModel(ConnectionPropertyDescriptor descriptor, ConnectionInfo target, ContainerInfo? parent,
        bool canInherit, bool alwaysEditable, string parentName)
        : base(descriptor, target, parent, canInherit, alwaysEditable, parentName)
    {
        var values = Enum.GetValues(descriptor.PropertyType).Cast<object>().ToList();
        // Files may hold values outside the enum (e.g. RenderingEngine 0); keep them selectable.
        foreach (var current in new[] { OwnValue, BoxedValue })
        {
            if (current is not null && !values.Contains(current))
                values.Insert(0, current);
        }
        Options = values;
    }

    public IReadOnlyList<object> Options { get; }

    public object? Value
    {
        get => BoxedValue;
        set
        {
            if (value is not null)
                BoxedValue = value;
        }
    }

    protected override void OnValueChanged()
    {
        base.OnValueChanged();
        this.RaisePropertyChanged(nameof(Value));
    }
}

/// <summary>A heading inside a tab and the fields under it.</summary>
public sealed class PropertySectionViewModel(string header, IReadOnlyList<PropertyFieldViewModel> fields) : ReactiveObject
{
    private bool _isVisible = true;

    public string Header { get; } = header;
    public IReadOnlyList<PropertyFieldViewModel> Fields { get; } = fields;

    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }
}

/// <summary>A tab of the connection editor (one property category).</summary>
public sealed class PropertyTabViewModel(string header, IReadOnlyList<PropertySectionViewModel> sections) : ReactiveObject
{
    private bool _isVisible = true;

    public string Header { get; } = header;
    public IReadOnlyList<PropertySectionViewModel> Sections { get; } = sections;

    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }
}
