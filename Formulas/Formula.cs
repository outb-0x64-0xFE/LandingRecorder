using System.Globalization;

namespace LandingRecorder.Formulas;

public sealed class FormulaException(string message) : Exception(message);

/// <summary>
/// A compiled arithmetic expression over variables x1..xN, e.g. <c>-x1 * 180 / pi</c> or <c>sqrt(x1^2 + x2^2)</c>.
/// Parsed once with the shunting-yard algorithm into reverse Polish notation, then evaluated with a small
/// value stack (no allocation per evaluation, so it's cheap to run per point on every graph redraw).
///
/// Supported: numbers (invariant culture, '.' decimal separator, optional exponent like 1e-3), variables
/// x1..xN (case-insensitive), constants pi and e, binary + - * / ^ (power, right-associative), unary minus/plus,
/// parentheses, and functions sqrt abs sin cos tan log (natural) log10 log2 — functions require parentheses.
/// Precedence, low to high: + -, * /, unary minus, ^ (so -x^2 is -(x^2) and 2^-1 is 0.5).
/// Not thread-safe: evaluation reuses one stack buffer per instance.
/// </summary>
public sealed class Formula
{
    private enum OpCode
    {
        PushConst,
        PushVar,
        Add,
        Sub,
        Mul,
        Div,
        Pow,
        Neg,
        Sqrt,
        Abs,
        Sin,
        Cos,
        Tan,
        Log,
        Log10,
        Log2,
    }

    private readonly record struct Instruction(OpCode Op, double Value = 0, int VarIndex = 0);

    private static readonly Dictionary<string, OpCode> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqrt"] = OpCode.Sqrt,
        ["abs"] = OpCode.Abs,
        ["sin"] = OpCode.Sin,
        ["cos"] = OpCode.Cos,
        ["tan"] = OpCode.Tan,
        ["log"] = OpCode.Log,
        ["log10"] = OpCode.Log10,
        ["log2"] = OpCode.Log2,
    };

    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI,
        ["e"] = Math.E,
    };

    private readonly Instruction[] _program;
    private readonly double[] _stack;

    private Formula(string expression, int variableCount, Instruction[] program, int maxStackDepth)
    {
        Expression = expression;
        VariableCount = variableCount;
        _program = program;
        _stack = new double[Math.Max(1, maxStackDepth)];
    }

    public string Expression { get; }

    public int VariableCount { get; }

    /// <summary>Label of the 0-based variable index, e.g. 0 → "x1".</summary>
    public static string VariableLabel(int index) => $"x{index + 1}";

    /// <summary>Compiles <paramref name="expression"/>; variables x1..x<paramref name="variableCount"/> are allowed.</summary>
    /// <exception cref="FormulaException">The expression is malformed or references an unknown name.</exception>
    public static Formula Compile(string expression, int variableCount)
    {
        var program = new Compiler(expression, variableCount).Compile();
        var maxDepth = ValidateStack(program, expression);
        return new Formula(expression, variableCount, program, maxDepth);
    }

    public static bool TryCompile(string expression, int variableCount, out Formula? formula, out string? error)
    {
        try
        {
            formula = Compile(expression, variableCount);
            error = null;
            return true;
        }
        catch (FormulaException ex)
        {
            formula = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Evaluates with <paramref name="variables"/>[0] as x1, [1] as x2, ... Domain errors yield NaN/±Infinity.</summary>
    public double Evaluate(ReadOnlySpan<double> variables)
    {
        if (variables.Length < VariableCount)
        {
            throw new ArgumentException($"Expected {VariableCount} variables, got {variables.Length}.", nameof(variables));
        }

        var stack = _stack;
        var top = -1;
        foreach (var instruction in _program)
        {
            switch (instruction.Op)
            {
                case OpCode.PushConst: stack[++top] = instruction.Value; break;
                case OpCode.PushVar: stack[++top] = variables[instruction.VarIndex]; break;
                case OpCode.Add: top--; stack[top] += stack[top + 1]; break;
                case OpCode.Sub: top--; stack[top] -= stack[top + 1]; break;
                case OpCode.Mul: top--; stack[top] *= stack[top + 1]; break;
                case OpCode.Div: top--; stack[top] /= stack[top + 1]; break;
                case OpCode.Pow: top--; stack[top] = Math.Pow(stack[top], stack[top + 1]); break;
                case OpCode.Neg: stack[top] = -stack[top]; break;
                case OpCode.Sqrt: stack[top] = Math.Sqrt(stack[top]); break;
                case OpCode.Abs: stack[top] = Math.Abs(stack[top]); break;
                case OpCode.Sin: stack[top] = Math.Sin(stack[top]); break;
                case OpCode.Cos: stack[top] = Math.Cos(stack[top]); break;
                case OpCode.Tan: stack[top] = Math.Tan(stack[top]); break;
                case OpCode.Log: stack[top] = Math.Log(stack[top]); break;
                case OpCode.Log10: stack[top] = Math.Log10(stack[top]); break;
                case OpCode.Log2: stack[top] = Math.Log2(stack[top]); break;
            }
        }

        return stack[0];
    }

    /// <summary>Simulates the program's stack effect: catches malformed programs and sizes the evaluation stack.</summary>
    private static int ValidateStack(Instruction[] program, string expression)
    {
        var depth = 0;
        var maxDepth = 0;
        foreach (var instruction in program)
        {
            var (pops, pushes) = instruction.Op switch
            {
                OpCode.PushConst or OpCode.PushVar => (0, 1),
                OpCode.Add or OpCode.Sub or OpCode.Mul or OpCode.Div or OpCode.Pow => (2, 1),
                _ => (1, 1),
            };

            if (depth < pops)
            {
                throw new FormulaException($"Malformed expression \"{expression}\".");
            }

            depth += pushes - pops;
            maxDepth = Math.Max(maxDepth, depth);
        }

        if (depth != 1)
        {
            throw new FormulaException($"Malformed expression \"{expression}\".");
        }

        return maxDepth;
    }

    /// <summary>Tokenizer + shunting-yard conversion to RPN.</summary>
    private sealed class Compiler(string expression, int variableCount)
    {
        private enum StackKind { Operator, Function, OpenParen }

        private readonly record struct StackEntry(StackKind Kind, OpCode Op, int Precedence, bool RightAssociative);

        private readonly List<Instruction> _output = [];
        private readonly Stack<StackEntry> _stack = new();
        private int _pos;

        public Instruction[] Compile()
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                throw new FormulaException("Formula is empty.");
            }

            // True when the next token must be an operand (number, variable, function, '(' or unary sign).
            var expectOperand = true;

            while (true)
            {
                SkipWhitespace();
                if (_pos >= expression.Length)
                {
                    break;
                }

                var ch = expression[_pos];
                var start = _pos;

                if (expectOperand)
                {
                    if (char.IsDigit(ch) || ch == '.')
                    {
                        _output.Add(new Instruction(OpCode.PushConst, ReadNumber()));
                        expectOperand = false;
                    }
                    else if (char.IsLetter(ch) || ch == '_')
                    {
                        var name = ReadIdentifier();
                        if (Functions.TryGetValue(name, out var function))
                        {
                            SkipWhitespace();
                            if (_pos >= expression.Length || expression[_pos] != '(')
                            {
                                throw Error($"Expected '(' after function '{name}'", start);
                            }

                            _stack.Push(new StackEntry(StackKind.Function, function, 0, false));
                            // The '(' itself is consumed on the next loop iteration as an ordinary paren.
                        }
                        else if (Constants.TryGetValue(name, out var constant))
                        {
                            _output.Add(new Instruction(OpCode.PushConst, constant));
                            expectOperand = false;
                        }
                        else if (TryParseVariable(name, out var index))
                        {
                            _output.Add(new Instruction(OpCode.PushVar, VarIndex: index));
                            expectOperand = false;
                        }
                        else
                        {
                            throw Error(variableCount == 0
                                ? $"Unknown name '{name}' (no variables defined)"
                                : $"Unknown name '{name}' (variables are x1..x{variableCount})", start);
                        }
                    }
                    else if (ch == '(')
                    {
                        _pos++;
                        _stack.Push(new StackEntry(StackKind.OpenParen, default, 0, false));
                    }
                    else if (ch == '-')
                    {
                        _pos++;
                        // Prefix operator: pushed without popping anything (nothing to its left binds to it).
                        _stack.Push(new StackEntry(StackKind.Operator, OpCode.Neg, 3, true));
                    }
                    else if (ch == '+')
                    {
                        _pos++; // Unary plus is a no-op.
                    }
                    else
                    {
                        throw Error($"Unexpected '{ch}'", start);
                    }
                }
                else
                {
                    _pos++;
                    switch (ch)
                    {
                        case '+': PushBinary(OpCode.Add, 1, false); break;
                        case '-': PushBinary(OpCode.Sub, 1, false); break;
                        case '*': PushBinary(OpCode.Mul, 2, false); break;
                        case '/': PushBinary(OpCode.Div, 2, false); break;
                        case '^': PushBinary(OpCode.Pow, 4, true); break;
                        case ')':
                            CloseParen(start);
                            continue; // Still not expecting an operand after ')'.
                        default:
                            throw Error($"Expected an operator but found '{ch}'", start);
                    }

                    expectOperand = true;
                }
            }

            if (expectOperand)
            {
                throw new FormulaException("Formula ends unexpectedly (missing operand).");
            }

            while (_stack.Count > 0)
            {
                var entry = _stack.Pop();
                if (entry.Kind == StackKind.OpenParen)
                {
                    throw new FormulaException("Missing ')'.");
                }

                _output.Add(new Instruction(entry.Op));
            }

            return [.. _output];
        }

        private void PushBinary(OpCode op, int precedence, bool rightAssociative)
        {
            while (_stack.TryPeek(out var top) && top.Kind == StackKind.Operator
                   && (top.Precedence > precedence || (top.Precedence == precedence && !rightAssociative)))
            {
                _output.Add(new Instruction(_stack.Pop().Op));
            }

            _stack.Push(new StackEntry(StackKind.Operator, op, precedence, rightAssociative));
        }

        private void CloseParen(int position)
        {
            while (true)
            {
                if (_stack.Count == 0)
                {
                    throw Error("Unmatched ')'", position);
                }

                var entry = _stack.Pop();
                if (entry.Kind == StackKind.OpenParen)
                {
                    break;
                }

                _output.Add(new Instruction(entry.Op));
            }

            if (_stack.TryPeek(out var function) && function.Kind == StackKind.Function)
            {
                _output.Add(new Instruction(_stack.Pop().Op));
            }
        }

        private bool TryParseVariable(string name, out int index)
        {
            index = -1;
            if (name.Length < 2 || (name[0] != 'x' && name[0] != 'X')
                || !int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number < 1 || number > variableCount)
            {
                return false;
            }

            index = number - 1;
            return true;
        }

        private double ReadNumber()
        {
            var start = _pos;
            while (_pos < expression.Length && (char.IsDigit(expression[_pos]) || expression[_pos] == '.'))
            {
                _pos++;
            }

            // Optional exponent: e/E, optional sign, at least one digit.
            if (_pos < expression.Length && (expression[_pos] == 'e' || expression[_pos] == 'E'))
            {
                var exponentStart = _pos++;
                if (_pos < expression.Length && (expression[_pos] == '+' || expression[_pos] == '-'))
                {
                    _pos++;
                }

                if (_pos < expression.Length && char.IsDigit(expression[_pos]))
                {
                    while (_pos < expression.Length && char.IsDigit(expression[_pos]))
                    {
                        _pos++;
                    }
                }
                else
                {
                    _pos = exponentStart; // Not an exponent after all; leave 'e' for the next token.
                }
            }

            var text = expression[start.._pos];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw Error($"Invalid number '{text}'", start);
            }

            return value;
        }

        private string ReadIdentifier()
        {
            var start = _pos;
            while (_pos < expression.Length && (char.IsLetterOrDigit(expression[_pos]) || expression[_pos] == '_'))
            {
                _pos++;
            }

            return expression[start.._pos];
        }

        private void SkipWhitespace()
        {
            while (_pos < expression.Length && char.IsWhiteSpace(expression[_pos]))
            {
                _pos++;
            }
        }

        private static FormulaException Error(string message, int position) =>
            new($"{message} at position {position + 1}.");
    }
}
