using System.Globalization;
using System.Text;

namespace Zladovce.Application.Common;

/// <summary>A CSV download: file name and UTF-8 bytes (with BOM).</summary>
public sealed record CsvFile(string FileName, byte[] Content)
{
    public const string ContentType = "text/csv; charset=utf-8";
}

/// <summary>
/// CSV for Serbian Excel: UTF-8 with BOM (Excel detects the encoding, so č, ć, š, ž, đ are shown correctly), <c>;</c> as the
/// separator and a decimal comma (the Serbian list separator and number format; no thousands separator), CRLF line ends,
/// RFC 4180 quoting (a cell with <c>;</c>, <c>"</c>, CR or LF, or leading/trailing spaces is quoted, quotes doubled).
/// Formula injection: a text cell starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or CR gets a leading apostrophe, so
/// spreadsheet programs show it as text instead of evaluating it. Numbers are written by <see cref="Number(decimal)"/> and are
/// never prefixed.
/// </summary>
public sealed class CsvWriter
{
    public const char Separator = ';';

    private static readonly NumberFormatInfo SerbianNumbers = new() { NumberDecimalSeparator = ",", NegativeSign = "-" };

    private readonly StringBuilder _builder = new();
    private bool _lineStarted;

    public CsvWriter Text(string? value)
    {
        var cell = value ?? string.Empty;
        if (cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            cell = "'" + cell;
        }

        return Raw(Quote(cell));
    }

    public CsvWriter Number(decimal value) => Raw(value.ToString("0.00", SerbianNumbers));

    public CsvWriter Number(int value) => Raw(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>A date as <c>dd.MM.yyyy</c>.</summary>
    public CsvWriter Date(DateOnly value) => Raw(value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));

    public CsvWriter Row(params string?[] texts)
    {
        foreach (var text in texts)
        {
            Text(text);
        }

        return EndRow();
    }

    public CsvWriter EndRow()
    {
        _builder.Append("\r\n");
        _lineStarted = false;
        return this;
    }

    public byte[] ToBytes()
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(_builder.ToString())];
    }

    public override string ToString() => _builder.ToString();

    private CsvWriter Raw(string cell)
    {
        if (_lineStarted)
        {
            _builder.Append(Separator);
        }

        _builder.Append(cell);
        _lineStarted = true;
        return this;
    }

    internal static string Quote(string cell)
    {
        var needsQuotes = cell.IndexOfAny([Separator, '"', '\r', '\n']) >= 0
            || (cell.Length > 0 && (char.IsWhiteSpace(cell[0]) || char.IsWhiteSpace(cell[^1])));
        return needsQuotes ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;
    }
}
