using System.Globalization;

namespace SpotCont.Services;

public static class MathExpressionEvaluator
{
    public static bool TryEvaluateQuery(string query, out string result)
    {
        result = string.Empty;

        var expression = ExtractExpression(query);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        if (!TryEvaluate(expression, out var value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        result = Format(value);
        return true;
    }

    public static string Format(double value)
    {
        if (Math.Abs(value % 1) < 0.0000001)
        {
            return value.ToString("0", CultureInfo.InvariantCulture);
        }

        return value.ToString("0.###############", CultureInfo.InvariantCulture);
    }

    private static string ExtractExpression(string query)
    {
        var text = (query ?? string.Empty).Trim();
        if (text.StartsWith("calc ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..].Trim();
        }
        else if (text.StartsWith('='))
        {
            text = text[1..].Trim();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var looksLikeMath =
            text.Contains("sqrt", StringComparison.OrdinalIgnoreCase) ||
            text.IndexOfAny(new[] { '+', '-', '*', '/', '%', '(', ')' }) >= 0;

        if (!looksLikeMath)
        {
            return string.Empty;
        }

        return text.Replace(',', '.');
    }

    private static bool TryEvaluate(string expression, out double value)
    {
        try
        {
            var parser = new Parser(expression);
            value = parser.Parse();
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private sealed class Parser
    {
        private readonly string _expression;
        private int _index;

        public Parser(string expression)
        {
            _expression = expression;
        }

        public double Parse()
        {
            var value = ParseAdditive();
            SkipWhitespace();

            if (_index != _expression.Length)
            {
                throw new FormatException("Unexpected trailing characters.");
            }

            return value;
        }

        private double ParseAdditive()
        {
            var value = ParseMultiplicative();

            while (true)
            {
                SkipWhitespace();
                if (Match('+'))
                {
                    value += ParseMultiplicative();
                    continue;
                }

                if (Match('-'))
                {
                    value -= ParseMultiplicative();
                    continue;
                }

                return value;
            }
        }

        private double ParseMultiplicative()
        {
            var value = ParseUnary();

            while (true)
            {
                SkipWhitespace();
                if (Match('*'))
                {
                    value *= ParseUnary();
                    continue;
                }

                if (Match('/'))
                {
                    value /= ParseUnary();
                    continue;
                }

                if (Match('%'))
                {
                    value %= ParseUnary();
                    continue;
                }

                return value;
            }
        }

        private double ParseUnary()
        {
            SkipWhitespace();

            if (Match('+'))
            {
                return ParseUnary();
            }

            if (Match('-'))
            {
                return -ParseUnary();
            }

            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWhitespace();

            if (Match('('))
            {
                var innerValue = ParseAdditive();
                Expect(')');
                return innerValue;
            }

            if (char.IsLetter(Current))
            {
                var identifier = ParseIdentifier();
                Expect('(');
                var argument = ParseAdditive();
                Expect(')');

                return identifier switch
                {
                    "sqrt" => Math.Sqrt(argument),
                    _ => throw new NotSupportedException($"Unsupported function '{identifier}'.")
                };
            }

            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipWhitespace();
            var startIndex = _index;

            while (_index < _expression.Length &&
                   (char.IsDigit(_expression[_index]) || _expression[_index] == '.'))
            {
                _index++;
            }

            if (startIndex == _index)
            {
                throw new FormatException("Expected a number.");
            }

            return double.Parse(_expression[startIndex.._index], CultureInfo.InvariantCulture);
        }

        private string ParseIdentifier()
        {
            var startIndex = _index;

            while (_index < _expression.Length && char.IsLetter(_expression[_index]))
            {
                _index++;
            }

            return _expression[startIndex.._index].ToLowerInvariant();
        }

        private void Expect(char character)
        {
            SkipWhitespace();
            if (!Match(character))
            {
                throw new FormatException($"Expected '{character}'.");
            }
        }

        private bool Match(char character)
        {
            if (Current != character)
            {
                return false;
            }

            _index++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_index < _expression.Length && char.IsWhiteSpace(_expression[_index]))
            {
                _index++;
            }
        }

        private char Current => _index < _expression.Length ? _expression[_index] : '\0';
    }
}
