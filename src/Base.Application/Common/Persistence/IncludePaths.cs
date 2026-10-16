using System.Linq.Expressions;

namespace Base.Application.Common.Persistence;

/// <summary>Turns typed include expressions (<c>x => x.Customer.Address</c>) into EF include paths (<c>Customer.Address</c>).</summary>
public static class IncludePaths
{
    public static string[] From<T>(IEnumerable<Expression<Func<T, object?>>> includes)
    {
        ArgumentNullException.ThrowIfNull(includes);
        return includes.Select(From).ToArray();
    }

    public static string From<T>(Expression<Func<T, object?>> include)
    {
        ArgumentNullException.ThrowIfNull(include);
        var parts = new Stack<string>();
        var expression = include.Body;

        while (true)
        {
            switch (expression)
            {
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary:
                    expression = unary.Operand;
                    continue;
                case MemberExpression member:
                    parts.Push(member.Member.Name);
                    expression = member.Expression!;
                    continue;
                case ParameterExpression:
                    if (parts.Count == 0)
                    {
                        break;
                    }

                    return string.Join('.', parts);
            }

            throw new ArgumentException(
                $"Include '{include}' must be a member chain such as x => x.Customer.Address. Use the string overload for collection navigations.",
                nameof(include));
        }
    }
}
