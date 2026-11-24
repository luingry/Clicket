using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Clicket.Core;

public enum ScriptParamType { Text, Integer, Number, Toggle, Choice }

/// <summary>A parameter a script exposes in its <c>param()</c> block, shown as a field in the step editor.</summary>
public sealed class ScriptParam
{
    public string Name { get; set; } = "";
    public ScriptParamType Type { get; set; }
    public string Default { get; set; } = "";
    public string? Label { get; set; }
    public List<string> Choices { get; } = new List<string>();
}

/// <summary>
/// Reads the setup a PowerShell script exposes, the way an AutoHotkey script exposes a GUI: its <c>param()</c> block.
/// <code>
/// param(
///     [Parameter(HelpMessage = "Times to repeat")] [int] $Count = 3,
///     [ValidateSet('Fast', 'Slow')] [string] $Speed = 'Fast',
///     [switch] $Beep
/// )
/// </code>
/// Supported: text, whole numbers, numbers, switch/bool, and a ValidateSet list. Anything unparseable is a text field,
/// and a script without param() simply has no fields. Values become named arguments when the step runs.
/// </summary>
public static class ScriptParams
{
    private static readonly Regex ParamStart = new Regex(@"(?im)^\s*param\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex Variable = new Regex(@"\$([A-Za-z_][A-Za-z0-9_]*)\s*(?:=\s*(.+))?$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Attribute = new Regex(@"\[([^\[\]]*(?:\[[^\[\]]*\][^\[\]]*)*)\]", RegexOptions.CultureInvariant);
    private static readonly Regex Help = new Regex(@"HelpMessage\s*=\s*(['""])(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SetItems = new Regex(@"(['""])(.*?)\1", RegexOptions.CultureInvariant);

    public static List<ScriptParam> Parse(string? script)
    {
        var result = new List<ScriptParam>();
        if (string.IsNullOrWhiteSpace(script)) return result;
        var text = StripComments(script!);
        var match = ParamStart.Match(text);
        if (!match.Success) return result;
        var body = Balanced(text, match.Index + match.Length - 1);
        if (body == null) return result;

        foreach (var piece in SplitTopLevel(body))
        {
            var param = ParseOne(piece.Trim());
            if (param != null && !result.Exists(p => string.Equals(p.Name, param.Name, StringComparison.OrdinalIgnoreCase))) result.Add(param);
        }
        return result;
    }

    /// <summary>The PowerShell command that runs the script file with the given values (names already validated by Parse).</summary>
    public static string BuildInvocation(string scriptPath, IReadOnlyList<ScriptParam> parameters, IDictionary<string, string> values)
    {
        var command = new StringBuilder("& ").Append(Quote(scriptPath));
        foreach (var p in parameters)
        {
            // Untouched parameters are left out, so the script's own default applies, expressions such as $env:USERPROFILE included.
            if (!values.TryGetValue(p.Name, out var value)) continue;
            switch (p.Type)
            {
                case ScriptParamType.Toggle:
                    command.Append(" -").Append(p.Name).Append(IsTrue(value) ? ":$true" : ":$false");
                    break;
                case ScriptParamType.Integer:
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) command.Append(" -").Append(p.Name).Append(' ').Append(whole.ToString(CultureInfo.InvariantCulture));
                    break;
                case ScriptParamType.Number:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) command.Append(" -").Append(p.Name).Append(' ').Append(number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    command.Append(" -").Append(p.Name).Append(' ').Append(Quote(value));
                    break;
            }
        }
        command.Append("; exit $LASTEXITCODE");
        return command.ToString();
    }

    public static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "$true", StringComparison.OrdinalIgnoreCase) || value == "1";

    /// <summary>A PowerShell single-quoted literal.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static ScriptParam? ParseOne(string piece)
    {
        if (piece.Length == 0) return null;
        var attributes = new List<string>();
        var rest = piece;
        while (true)
        {
            rest = rest.TrimStart();
            if (!rest.StartsWith("[", StringComparison.Ordinal)) break;
            var close = MatchingBracket(rest, 0);
            if (close < 0) return null;
            attributes.Add(rest.Substring(1, close - 1).Trim());
            rest = rest.Substring(close + 1);
        }
        var variable = Variable.Match(rest.Trim());
        if (!variable.Success) return null;

        var param = new ScriptParam { Name = variable.Groups[1].Value, Type = ScriptParamType.Text };
        foreach (var attribute in attributes)
        {
            var lower = attribute.ToLowerInvariant();
            if (lower == "int" || lower == "int32" || lower == "int64" || lower == "long" || lower == "int16" || lower == "byte") param.Type = ScriptParamType.Integer;
            else if (lower == "double" || lower == "float" || lower == "single" || lower == "decimal") param.Type = ScriptParamType.Number;
            else if (lower == "switch" || lower == "bool" || lower == "boolean") param.Type = ScriptParamType.Toggle;
            else if (lower.StartsWith("validateset", StringComparison.Ordinal))
            {
                foreach (Match item in SetItems.Matches(attribute)) param.Choices.Add(item.Groups[2].Value);
            }
            else if (lower.StartsWith("parameter", StringComparison.Ordinal))
            {
                var help = Help.Match(attribute);
                if (help.Success) param.Label = help.Groups[2].Value;
            }
        }
        if (param.Choices.Count > 0) param.Type = ScriptParamType.Choice;

        var value = variable.Groups[2].Success ? variable.Groups[2].Value.Trim() : "";
        if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[value.Length - 1] == value[0]) value = value.Substring(1, value.Length - 2);
        if (param.Type == ScriptParamType.Toggle) value = IsTrue(value) ? "true" : "false";
        if (param.Type == ScriptParamType.Choice && !param.Choices.Contains(value)) value = param.Choices[0];
        param.Default = value;
        return param;
    }

    /// <summary>Content between the bracket at <paramref name="open"/> and its match, skipping strings; null when unbalanced.</summary>
    private static string? Balanced(string text, int open)
    {
        var depth = 0;
        char quote = '\0';
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '\'' || c == '"') quote = c;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return text.Substring(open + 1, i - open - 1);
        }
        return null;
    }

    private static int MatchingBracket(string text, int open)
    {
        var depth = 0;
        char quote = '\0';
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '\'' || c == '"') quote = c;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return i;
        }
        return -1;
    }

    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        char quote = '\0';
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '\'' || c == '"') quote = c;
            else if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return body.Substring(start, i - start);
                start = i + 1;
            }
        }
        yield return body.Substring(start);
    }

    /// <summary>Removes # line comments and &lt;# #&gt; blocks outside strings, keeping line structure.</summary>
    private static string StripComments(string script)
    {
        var output = new StringBuilder(script.Length);
        char quote = '\0';
        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            if (quote != '\0')
            {
                output.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '<' && i + 1 < script.Length && script[i + 1] == '#')
            {
                var end = script.IndexOf("#>", i + 2, StringComparison.Ordinal);
                i = end < 0 ? script.Length : end + 1;
                continue;
            }
            if (c == '#')
            {
                while (i < script.Length && script[i] != '\n') i++;
                output.Append('\n');
                continue;
            }
            if (c == '\'' || c == '"') quote = c;
            output.Append(c);
        }
        return output.ToString();
    }
}
