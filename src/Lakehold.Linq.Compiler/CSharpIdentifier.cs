using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Lakehold.Linq.Compiler;

internal static class CSharpIdentifier
{
    public static string Create(string value, string fallback)
    {
        var builder = new StringBuilder(value.Length + 1);
        foreach (var rune in value.EnumerateRunes())
        {
            var text = rune.ToString();
            var category = Rune.GetUnicodeCategory(rune);
            if (builder.Length == 0 && category == UnicodeCategory.DecimalDigitNumber)
            {
                builder.Append('_').Append(text);
                continue;
            }

            var valid = builder.Length == 0
                ? category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                    or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber
                    || text == "_"
                : category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                    or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber
                    or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation
                    or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.Format;

            builder.Append(valid ? text : "_");
        }

        if (builder.Length == 0)
        {
            builder.Append(fallback);
        }

        var identifier = builder.ToString();
        return SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : string.Concat("@", identifier);
    }

    public static string Pascal(string value, string fallback)
    {
        var identifier = Create(value, fallback).TrimStart('@');
        var parts = identifier.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return fallback;
        }

        var builder = new StringBuilder(identifier.Length);
        foreach (var part in parts)
        {
            builder.Append(char.ToUpperInvariant(part[0]));
            builder.Append(part.AsSpan(1));
        }

        return Create(builder.ToString(), fallback);
    }
}
