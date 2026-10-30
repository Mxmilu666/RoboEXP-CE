namespace RoboExp.App.Properties;

/// <summary>Original PropertyFrmNew.CheckExpression (advisory syntax check of If/While/Calculate/Return expressions).</summary>
public static class ExpressionChecker
{
    public enum ErrorType { None, Other, WrongWord, WrongInitial, WrongLast, WrongParenthesis, Dislocation }

    /// <summary>Result: error kind plus the text range the original selects (TxtExprFocus).</summary>
    public readonly record struct Result(ErrorType Error, int Start, int Length)
    {
        public bool IsValid => Error == ErrorType.None;
    }

    private enum Sign { Operand, Number, AndOr, Not, Relation, Arithmetic, LeftParen, RightParen, Assignment, Self }

    public static string MessageKey(ErrorType e) => e switch
    {
        ErrorType.WrongWord => "Xwb:ExpressionFrm_WrongWord",
        ErrorType.WrongInitial => "Xwb:ExpressionFrm_WrongInitial",
        ErrorType.WrongLast => "Xwb:ExpressionFrm_WrongLast",
        ErrorType.WrongParenthesis => "Xwb:ExpressionFrm_WrongParenthesis",
        ErrorType.Dislocation => "Xwb:ExpressionFrm_Dislocation",
        _ => "Xwb:ExpressionFrm_WrongExpression",
    };

    private static bool LetterOrDigit(char c) => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsNumber(string s)
    {
        if (s.Length == 0) return false;
        var body = s[0] == '-' ? s[1..] : s;
        return body.All(c => c is >= '0' and <= '9');
    }

    public static Result Check(string? expression)
    {
        if (expression == null || expression.Trim().Length == 0) return new(ErrorType.None, 0, 0);
        var trimmed = expression.Trim();
        var first = trimmed[0];
        if (!LetterOrDigit(first) && first is not ('!' or '(' or '_' or '+' or '-')) return new(ErrorType.WrongInitial, 0, 1);
        var last = trimmed[^1];
        if (!LetterOrDigit(last) && last is not (')' or '_' or '+' or '-')) return new(ErrorType.WrongLast, trimmed.Length - 1, 1);

        var text = expression;
        var len = text.Length;
        char At(int i) => i < len ? text[i] : '\0'; // the original indexes past the end (exception); treat as no char
        var tokens = new List<(string Text, Sign Sign, int Pos)>();
        int open = 0, close = 0, p = 0;
        while (p < len)
        {
            var c = text[p];
            switch (c)
            {
                case '&' or '|':
                    if (At(p + 1) != c) return new(ErrorType.WrongWord, p, 1);
                    tokens.Add((text.Substring(p, 2), Sign.AndOr, p)); p += 2; continue;
                case '!':
                    if (At(p + 1) == '=') { tokens.Add(("!=", Sign.Relation, p)); p += 2; }
                    else { tokens.Add(("!", Sign.Not, p)); p++; }
                    continue;
                case '<' or '>':
                    if (At(p + 1) == '=') { tokens.Add((text.Substring(p, 2), Sign.Relation, p)); p += 2; }
                    else { tokens.Add((c.ToString(), Sign.Relation, p)); p++; }
                    continue;
                case '=':
                    if (At(p + 1) == '=') { tokens.Add(("==", Sign.Relation, p)); p += 2; }
                    else { tokens.Add(("=", Sign.Assignment, p)); p++; }
                    continue;
                case '+' or '-':
                    if (At(p + 1) == c) { tokens.Add((text.Substring(p, 2), Sign.Self, p)); p += 2; }
                    else { tokens.Add((c.ToString(), Sign.Arithmetic, p)); p++; }
                    continue;
                case '%' or '*' or '/':
                    tokens.Add((c.ToString(), Sign.Arithmetic, p)); p++; continue;
                case '(':
                    tokens.Add(("(", Sign.LeftParen, p)); open++; p++; continue;
                case ')':
                    tokens.Add((")", Sign.RightParen, p)); close++; p++; continue;
                case ' ':
                    p++; continue;
            }
            if (!LetterOrDigit(c) && c != '_') return new(ErrorType.WrongWord, p, 1);
            var start = p++;
            while (p < len && (LetterOrDigit(text[p]) || text[p] == '_')) p++;
            var word = text[start..p];
            if (p >= len)
            {
                // Original: a word ending the text is always an operand and is not keyword-checked.
                tokens.Add((word, Sign.Operand, start));
                break;
            }
            if (IsNumber(word)) tokens.Add((word, Sign.Number, start));
            else
            {
                if (ValueCheck.IsKeyWord(word) || ValueCheck.IsSystemUsed(word)) return new(ErrorType.Other, start, word.Length);
                tokens.Add((word, Sign.Operand, start));
            }
        }

        if (tokens.Count == 0) return new(ErrorType.Other, 0, 0);
        if (tokens.Count == 1)
        {
            var t = tokens[0];
            return t.Sign is Sign.Operand or Sign.Number ? new(ErrorType.None, 0, 0) : new(ErrorType.Other, t.Pos, t.Text.Length);
        }
        if (tokens.Count == 2)
        {
            var (a, b) = (tokens[0].Sign, tokens[1].Sign);
            if (!(a == Sign.Operand && b == Sign.Self) && !(a == Sign.Self && b == Sign.Operand)) return new(ErrorType.Other, 0, 0);
        }
        if (open != close) return new(ErrorType.WrongParenthesis, open > close ? len : 0, 0);
        var f = tokens[0];
        if (f.Sign is not (Sign.Not or Sign.LeftParen or Sign.Operand or Sign.Number or Sign.Self)) return new(ErrorType.WrongInitial, f.Pos, f.Text.Length);
        var l = tokens[^1];
        if (l.Sign is not (Sign.RightParen or Sign.Self or Sign.Operand or Sign.Number)) return new(ErrorType.WrongLast, l.Pos, l.Text.Length);
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var next = tokens[i + 1].Sign;
            var ok = tokens[i].Sign switch
            {
                Sign.Operand => next is Sign.AndOr or Sign.RightParen or Sign.Assignment or Sign.Relation or Sign.Arithmetic or Sign.Self,
                Sign.Number => next is Sign.AndOr or Sign.RightParen or Sign.Relation or Sign.Arithmetic,
                Sign.AndOr or Sign.Not or Sign.LeftParen or Sign.Assignment => next is Sign.Not or Sign.LeftParen or Sign.Operand or Sign.Number or Sign.Self,
                Sign.Relation or Sign.Arithmetic => next is Sign.LeftParen or Sign.Self or Sign.Operand or Sign.Number,
                Sign.RightParen => next is Sign.AndOr or Sign.Relation or Sign.Arithmetic or Sign.Assignment or Sign.RightParen or Sign.Self,
                Sign.Self => next is Sign.AndOr or Sign.Operand or Sign.Relation or Sign.Arithmetic or Sign.LeftParen or Sign.RightParen,
                _ => false,
            };
            if (!ok) return new(ErrorType.Dislocation, tokens[i + 1].Pos, tokens[i + 1].Text.Length);
        }
        return new(ErrorType.None, 0, 0);
    }
}
