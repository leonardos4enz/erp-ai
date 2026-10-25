using System.Globalization;
using Erp.Api.Metadata;

namespace Erp.Api.Printing;

/// <summary>Spec 027 (H4 CA5): formato de los valores impresos (es-MX). Puro, para probarlo sin base de datos.</summary>
public static class PrintFormat
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Formato que se usa si la plantilla no indica uno: según el tipo del campo (o del resultado de la fórmula).</summary>
    public static string Auto(FieldDef? f, object? value)
    {
        var type = f?.Type == FieldTypes.Formula ? f.ResultType : f?.Type;
        return type switch
        {
            FieldTypes.Decimal => PrintFormats.Number,
            FieldTypes.Integer => PrintFormats.Integer,
            FieldTypes.Date => PrintFormats.Date,
            FieldTypes.DateTime => PrintFormats.DateTime,
            _ => value switch
            {
                decimal => PrintFormats.Number,
                DateTime d => d.TimeOfDay == TimeSpan.Zero ? PrintFormats.Date : PrintFormats.DateTime,
                _ => PrintFormats.Text,
            },
        };
    }

    public static string Format(object? value, FieldDef? field, string? format, int? decimals, string? currency)
    {
        value = Expressions.ExprEvaluator.Normalize(value);
        if (value is null) return "";
        if (value is bool b) return b ? "Sí" : "No";
        var fmt = format ?? Auto(field, value);
        var d = Math.Clamp(decimals ?? field?.Scale ?? 2, 0, 6);
        switch (fmt)
        {
            case PrintFormats.Number when AsDecimal(value) is { } n: return n.ToString("N" + d, Culture);
            case PrintFormats.Integer when AsDecimal(value) is { } n: return decimal.Round(n, 0, MidpointRounding.AwayFromZero).ToString("N0", Culture);
            case PrintFormats.Percent when AsDecimal(value) is { } n: return n.ToString("N" + Math.Clamp(decimals ?? 2, 0, 6), Culture) + " %";
            case PrintFormats.Money when AsDecimal(value) is { } n:
            {
                var text = (n < 0 ? "-$" : "$") + Math.Abs(n).ToString("N" + Math.Clamp(decimals ?? 2, 0, 6), Culture);
                return string.IsNullOrWhiteSpace(currency) ? text : $"{text} {currency.Trim().ToUpperInvariant()}";
            }
            case PrintFormats.Date when value is DateTime dt: return dt.ToString("dd/MM/yyyy", Culture);
            case PrintFormats.DateLong when value is DateTime dt: return dt.ToString("d 'de' MMMM 'de' yyyy", Culture);
            case PrintFormats.DateTime when value is DateTime dt: return dt.ToString("dd/MM/yyyy HH:mm", Culture);
        }
        return value switch
        {
            decimal n => n.ToString(field?.Scale is { } s ? "N" + s : "G29", Culture),
            DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "dd/MM/yyyy" : "dd/MM/yyyy HH:mm", Culture),
            _ => Convert.ToString(value, Culture) ?? "",
        };
    }

    private static decimal? AsDecimal(object v) => v switch
    {
        decimal n => n,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };
}
