using LandingRecorder.Formulas;
using Xunit;

namespace LandingRecorder.Tests;

public class FormulaTests
{
    [Theory]
    [InlineData("1+2*3", 7)]
    [InlineData("(1+2)*3", 9)]
    [InlineData("10-4-3", 3)]          // left-associative
    [InlineData("100/10/5", 2)]        // left-associative
    [InlineData("2^3^2", 512)]         // right-associative power
    [InlineData("-2^2", -4)]           // power binds tighter than unary minus
    [InlineData("2^-1", 0.5)]
    [InlineData("2*-3", -6)]
    [InlineData("+3 - -2", 5)]
    [InlineData("abs(-3)", 3)]
    [InlineData("sin(pi/2)", 1)]
    [InlineData("cos(0)", 1)]
    [InlineData("tan(0)", 0)]
    [InlineData("log(e)", 1)]          // natural logarithm
    [InlineData("log10(1000)", 3)]
    [InlineData("log2(8)", 3)]
    [InlineData("sqrt(16)", 4)]
    [InlineData("1.5e2 + 2", 152)]
    [InlineData("  1 +\t2 ", 3)]
    public void EvaluatesConstantExpressions(string expression, double expected)
    {
        Assert.Equal(expected, Formula.Compile(expression, 0).Evaluate([]), precision: 9);
    }

    [Theory]
    [InlineData("-x1", new[] { 5.0 }, -5)]
    [InlineData("--x1", new[] { 5.0 }, 5)]
    [InlineData("X1 + x2", new[] { 1.0, 2.0 }, 3)]               // case-insensitive
    [InlineData("x1 - x2 * x3", new[] { 1.0, 2.0, 3.0 }, -5)]
    [InlineData("sqrt(x1^2 + x2^2)", new[] { 3.0, 4.0 }, 5)]
    [InlineData("-(x1+1)*2", new[] { 3.0 }, -8)]
    [InlineData("x1*180/pi", new[] { Math.PI }, 180)]
    [InlineData("abs(sin(x1))*-1", new[] { Math.PI / 2 }, -1)]
    public void EvaluatesVariables(string expression, double[] variables, double expected)
    {
        Assert.Equal(expected, Formula.Compile(expression, variables.Length).Evaluate(variables), precision: 9);
    }

    [Fact]
    public void DomainErrorsYieldNonFiniteResults()
    {
        Assert.True(double.IsNaN(Formula.Compile("sqrt(-1)", 0).Evaluate([])));
        Assert.True(double.IsInfinity(Formula.Compile("1/x1", 1).Evaluate([0.0])));
    }

    [Fact]
    public void FormulaIsReusableAcrossEvaluations()
    {
        var formula = Formula.Compile("x1 * 2 + x2", 2);
        Assert.Equal(4, formula.Evaluate([1.0, 2.0]));
        Assert.Equal(13, formula.Evaluate([5.0, 3.0]));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("x2", 1)]        // beyond declared variable count
    [InlineData("x0", 1)]
    [InlineData("foo", 1)]
    [InlineData("1+", 0)]
    [InlineData("(1+2", 0)]
    [InlineData("1+2)", 0)]
    [InlineData("()", 0)]
    [InlineData("*2", 0)]
    [InlineData("2 3", 0)]
    [InlineData("1 $ 2", 0)]
    [InlineData("1,5", 0)]       // decimal separator is always '.'
    [InlineData("sqrt 4", 0)]    // functions require parentheses
    public void RejectsInvalidExpressions(string expression, int variableCount)
    {
        Assert.Throws<FormulaException>(() => Formula.Compile(expression, variableCount));
        Assert.False(Formula.TryCompile(expression, variableCount, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void VariableLabelsAreOneBased()
    {
        Assert.Equal("x1", Formula.VariableLabel(0));
        Assert.Equal("x10", Formula.VariableLabel(9));
    }
}
