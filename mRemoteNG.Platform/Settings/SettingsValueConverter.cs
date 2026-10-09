using System.ComponentModel;
using System.Globalization;

namespace mRemoteNG.Platform.Settings;

/// <summary>
/// Converts settings values to and from their persisted string form.
/// Formatting and parsing always use <see cref="CultureInfo.InvariantCulture"/>, so a settings
/// file written under one locale (e.g. "1.5" vs "1,5") reads back identically under another.
/// </summary>
public static class SettingsValueConverter
{
    /// <summary>Formats a value for storage.</summary>
    public static string Format<T>(T value) => FormatObject(value);

    /// <summary>Formats a value for storage.</summary>
    public static string FormatObject(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? bool.TrueString : bool.FalseString,
        Enum e => e.ToString(),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => TypeDescriptor.GetConverter(value.GetType()).ConvertToInvariantString(value) ?? string.Empty,
    };

    /// <summary>Parses a stored value. Returns false when <paramref name="raw"/> is not a valid <typeparamref name="T"/>.</summary>
    public static bool TryParse<T>(string? raw, out T value)
    {
        value = default!;
        if (!TryParse(raw, typeof(T), out var result))
            return false;

        if (result is T typed)
        {
            value = typed;
            return true;
        }

        // Nullable<T> stored as an empty string.
        return result is null && default(T) is null;
    }

    /// <summary>Parses a stored value into <paramref name="type"/>.</summary>
    public static bool TryParse(string? raw, Type type, out object? value)
    {
        value = null;
        if (raw is null)
            return false;

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            if (raw.Length == 0)
                return true; // null
            type = underlying;
        }

        const NumberStyles integer = NumberStyles.Integer;
        const NumberStyles floating = NumberStyles.Float | NumberStyles.AllowThousands;
        var inv = CultureInfo.InvariantCulture;
        var text = type == typeof(string) ? raw : raw.Trim();

        switch (Type.GetTypeCode(type))
        {
            case TypeCode.String:
                value = raw;
                return true;
            case TypeCode.Boolean:
                if (bool.TryParse(text, out var b)) { value = b; return true; }
                if (text == "1") { value = true; return true; }
                if (text == "0") { value = false; return true; }
                return false;
            case TypeCode.Char:
                if (text.Length == 1) { value = text[0]; return true; }
                return false;
            case TypeCode.Int32 when !type.IsEnum:
                if (int.TryParse(text, integer, inv, out var i32)) { value = i32; return true; }
                return false;
            case TypeCode.Int64 when !type.IsEnum:
                if (long.TryParse(text, integer, inv, out var i64)) { value = i64; return true; }
                return false;
            case TypeCode.Int16 when !type.IsEnum:
                if (short.TryParse(text, integer, inv, out var i16)) { value = i16; return true; }
                return false;
            case TypeCode.Byte when !type.IsEnum:
                if (byte.TryParse(text, integer, inv, out var u8)) { value = u8; return true; }
                return false;
            case TypeCode.SByte when !type.IsEnum:
                if (sbyte.TryParse(text, integer, inv, out var i8)) { value = i8; return true; }
                return false;
            case TypeCode.UInt16 when !type.IsEnum:
                if (ushort.TryParse(text, integer, inv, out var u16)) { value = u16; return true; }
                return false;
            case TypeCode.UInt32 when !type.IsEnum:
                if (uint.TryParse(text, integer, inv, out var u32)) { value = u32; return true; }
                return false;
            case TypeCode.UInt64 when !type.IsEnum:
                if (ulong.TryParse(text, integer, inv, out var u64)) { value = u64; return true; }
                return false;
            case TypeCode.Double:
                // Fall back to the current culture for files written by older builds,
                // which stored numbers with ToString() under the user's locale.
                if (double.TryParse(text, floating, inv, out var d)
                    || double.TryParse(text, floating, CultureInfo.CurrentCulture, out d))
                {
                    value = d;
                    return true;
                }
                return false;
            case TypeCode.Single:
                if (float.TryParse(text, floating, inv, out var f)
                    || float.TryParse(text, floating, CultureInfo.CurrentCulture, out f))
                {
                    value = f;
                    return true;
                }
                return false;
            case TypeCode.Decimal:
                if (decimal.TryParse(text, floating, inv, out var m)
                    || decimal.TryParse(text, floating, CultureInfo.CurrentCulture, out m))
                {
                    value = m;
                    return true;
                }
                return false;
            case TypeCode.DateTime:
                if (DateTime.TryParse(text, inv, DateTimeStyles.RoundtripKind, out var dt)) { value = dt; return true; }
                return false;
        }

        if (type.IsEnum)
            return TryParseEnum(text, type, out value);

        if (type == typeof(Guid))
        {
            if (Guid.TryParse(text, out var g)) { value = g; return true; }
            return false;
        }

        if (type == typeof(TimeSpan))
        {
            if (TimeSpan.TryParse(text, inv, out var ts)) { value = ts; return true; }
            return false;
        }

        if (type == typeof(DateTimeOffset))
        {
            if (DateTimeOffset.TryParse(text, inv, DateTimeStyles.RoundtripKind, out var dto)) { value = dto; return true; }
            return false;
        }

        if (type == typeof(Version))
        {
            if (Version.TryParse(text, out var v)) { value = v; return true; }
            return false;
        }

        try
        {
            var converter = TypeDescriptor.GetConverter(type);
            if (!converter.CanConvertFrom(typeof(string)))
                return false;
            value = converter.ConvertFromInvariantString(text);
            return value is not null;
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException or ArgumentException or InvalidCastException)
        {
            return false;
        }
    }

    private static bool TryParseEnum(string text, Type type, out object? value)
    {
        value = null;
        if (!Enum.TryParse(type, text, ignoreCase: true, out var parsed))
            return false;

        // Enum.TryParse accepts any number ("42"); only accept values the enum actually defines.
        var isFlags = type.IsDefined(typeof(FlagsAttribute), inherit: false);
        if (!isFlags && !Enum.IsDefined(type, parsed!))
            return false;

        value = parsed;
        return true;
    }
}
