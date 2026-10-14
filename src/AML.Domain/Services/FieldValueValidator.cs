using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AML.Domain.Entities;

namespace AML.Domain.Services;

public sealed class FieldValidationConfig
{
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public string? Regex { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public DateTime? MinDate { get; set; }
    public DateTime? MaxDate { get; set; }
    /// <summary>Phone policy: E164 (default) or a country code such as JO.</summary>
    public string? PhoneRegion { get; set; }
    public int? MaxSelections { get; set; }

    public static FieldValidationConfig Parse(string? json) => string.IsNullOrWhiteSpace(json)
        ? new FieldValidationConfig()
        : JsonSerializer.Deserialize<FieldValidationConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
}

public sealed class ParsedFieldValue
{
    public string? Text { get; set; }
    public decimal? Number { get; set; }
    public DateTime? Date { get; set; }
    public bool? Bool { get; set; }
    public List<string>? Multi { get; set; }
    public string Display { get; set; } = "";
}

/// <summary>Server-side typed validation of dynamic field values.</summary>
public static class FieldValueValidator
{
    private static readonly Regex Email = new(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex E164 = new(@"^\+[1-9][0-9]{7,14}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex JordanMobile = new(@"^(\+9627|009627|07)[789][0-9]{7}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Iso2 = new("^[A-Z]{2}$", RegexOptions.Compiled);
    private static readonly Regex Iso3 = new("^[A-Z]{3}$", RegexOptions.Compiled);

    /// <param name="activeOptions">Active option codes for select fields.</param>
    /// <param name="knownCodes">Known country/currency codes (lookup) for Country/Currency types; null to skip.</param>
    public static (ParsedFieldValue? Value, string? Error) Validate(CustomerFieldDefinition def, JsonElement raw,
        IReadOnlyCollection<string>? activeOptions = null, IReadOnlyCollection<string>? knownCodes = null)
    {
        var cfg = FieldValidationConfig.Parse(def.ValidationJson);
        var label = $"{def.LabelEn} ({def.Code})";
        if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return (null, null);
        try
        {
            switch (def.DataType)
            {
                case FieldDataTypes.Integer:
                case FieldDataTypes.Decimal:
                {
                    decimal d;
                    if (raw.ValueKind == JsonValueKind.Number) d = raw.GetDecimal();
                    else if (raw.ValueKind == JsonValueKind.String && decimal.TryParse(raw.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p)) d = p;
                    else return (null, $"{label}: value must be a {(def.DataType == FieldDataTypes.Integer ? "whole number" : "decimal number")}.");
                    if (def.DataType == FieldDataTypes.Integer && d != Math.Truncate(d)) return (null, $"{label}: value must be a whole number.");
                    if (cfg.Min is { } min && d < min) return (null, $"{label}: value must be >= {min}.");
                    if (cfg.Max is { } max && d > max) return (null, $"{label}: value must be <= {max}.");
                    if (def.DataType == FieldDataTypes.Decimal)
                    {
                        var scale = (decimal.GetBits(d)[3] >> 16) & 0x7F;
                        var normalized = d / 1.0000000000000000000000000000m;
                        scale = (decimal.GetBits(normalized)[3] >> 16) & 0x7F;
                        if (cfg.Scale is { } s && scale > s) return (null, $"{label}: at most {s} decimal places are allowed.");
                        var integerDigits = Math.Truncate(Math.Abs(d)).ToString(CultureInfo.InvariantCulture).TrimStart('0').Length;
                        if (cfg.Precision is { } prec && integerDigits + Math.Min(scale, cfg.Scale ?? scale) > prec)
                            return (null, $"{label}: precision exceeds {prec} digits.");
                    }
                    return (new ParsedFieldValue { Number = d, Display = d.ToString(CultureInfo.InvariantCulture) }, null);
                }
                case FieldDataTypes.Boolean:
                    if (raw.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        return (new ParsedFieldValue { Bool = raw.GetBoolean(), Display = raw.GetBoolean() ? "true" : "false" }, null);
                    if (raw.ValueKind == JsonValueKind.String && bool.TryParse(raw.GetString(), out var b))
                        return (new ParsedFieldValue { Bool = b, Display = b ? "true" : "false" }, null);
                    return (null, $"{label}: value must be true or false.");
                case FieldDataTypes.Date:
                case FieldDataTypes.DateTime:
                {
                    if (raw.ValueKind != JsonValueKind.String || !DateTime.TryParse(raw.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                        return (null, $"{label}: value must be an ISO-8601 {(def.DataType == FieldDataTypes.Date ? "date" : "date-time")}.");
                    if (def.DataType == FieldDataTypes.Date) dt = dt.Date;
                    if (cfg.MinDate is { } mn && dt < mn) return (null, $"{label}: date must be on or after {mn:yyyy-MM-dd}.");
                    if (cfg.MaxDate is { } mx && dt > mx) return (null, $"{label}: date must be on or before {mx:yyyy-MM-dd}.");
                    return (new ParsedFieldValue { Date = dt, Display = def.DataType == FieldDataTypes.Date ? dt.ToString("yyyy-MM-dd") : dt.ToString("O") }, null);
                }
                case FieldDataTypes.MultiSelect:
                {
                    if (raw.ValueKind != JsonValueKind.Array) return (null, $"{label}: value must be a list of option codes.");
                    var vals = raw.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "").ToList();
                    if (vals.Any(string.IsNullOrEmpty)) return (null, $"{label}: options must be strings.");
                    if (vals.Distinct().Count() != vals.Count) return (null, $"{label}: duplicate options.");
                    var invalid = vals.Where(v => activeOptions is null || !activeOptions.Contains(v)).ToList();
                    if (invalid.Count > 0) return (null, $"{label}: invalid or inactive option(s): {string.Join(", ", invalid)}.");
                    if (cfg.MaxSelections is { } ms && vals.Count > ms) return (null, $"{label}: at most {ms} selections.");
                    var ordered = activeOptions is null ? vals : activeOptions.Where(vals.Contains).ToList();
                    return (new ParsedFieldValue { Multi = ordered, Display = string.Join(",", ordered) }, null);
                }
                default:
                {
                    if (raw.ValueKind != JsonValueKind.String) return (null, $"{label}: value must be text.");
                    var s = raw.GetString()!.Trim();
                    if (s.Length == 0) return (null, null);
                    var maxLen = cfg.MaxLength ?? (def.DataType == FieldDataTypes.LongText ? 4000 : 400);
                    if (s.Length > maxLen) return (null, $"{label}: maximum length is {maxLen}.");
                    if (cfg.MinLength is { } minLen && s.Length < minLen) return (null, $"{label}: minimum length is {minLen}.");
                    switch (def.DataType)
                    {
                        case FieldDataTypes.SingleSelect:
                            if (activeOptions is null || !activeOptions.Contains(s)) return (null, $"{label}: '{s}' is not a valid active option.");
                            break;
                        case FieldDataTypes.Email:
                            if (!Email.IsMatch(s)) return (null, $"{label}: invalid email address.");
                            break;
                        case FieldDataTypes.Phone:
                            var compact = s.Replace(" ", "").Replace("-", "");
                            var ok = cfg.PhoneRegion == "JO" ? JordanMobile.IsMatch(compact) : E164.IsMatch(compact);
                            if (!ok) return (null, $"{label}: invalid phone number for policy {cfg.PhoneRegion ?? "E164"}.");
                            s = compact;
                            break;
                        case FieldDataTypes.Country:
                            s = s.ToUpperInvariant();
                            if (!Iso2.IsMatch(s) || knownCodes is not null && !knownCodes.Contains(s)) return (null, $"{label}: unknown ISO country code '{s}'.");
                            break;
                        case FieldDataTypes.Currency:
                            s = s.ToUpperInvariant();
                            if (!Iso3.IsMatch(s) || knownCodes is not null && !knownCodes.Contains(s)) return (null, $"{label}: unknown ISO currency code '{s}'.");
                            break;
                    }
                    if (!string.IsNullOrEmpty(cfg.Regex) && !Regex.IsMatch(s, cfg.Regex, RegexOptions.None, TimeSpan.FromMilliseconds(200)))
                        return (null, $"{label}: value does not match the required format.");
                    return (new ParsedFieldValue { Text = s, Display = s }, null);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return (null, $"{label}: validation timed out.");
        }
    }

    /// <summary>Returns true when a definition change would make stored values of the old type invalid.</summary>
    public static bool IsCompatibleTypeChange(string from, string to)
    {
        if (from == to) return true;
        if (from == FieldDataTypes.Integer && to == FieldDataTypes.Decimal) return true;
        if (from == FieldDataTypes.ShortText && to == FieldDataTypes.LongText) return true;
        if (from == FieldDataTypes.Date && to == FieldDataTypes.DateTime) return true;
        return false;
    }

    public static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var last = value.Length <= 4 ? value[^Math.Min(1, value.Length)..] : value[^4..];
        return new string('*', Math.Max(4, value.Length - last.Length)) + last;
    }
}
