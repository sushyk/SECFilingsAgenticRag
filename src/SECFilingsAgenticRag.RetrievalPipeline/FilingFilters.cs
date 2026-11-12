using System.Linq.Expressions;
using SECFilingsAgenticRag.Common.VectorStore;

namespace SECFilingsAgenticRag.RetrievalPipeline;

// MEVD filters are LINQ expressions over the record; nothing built in turns the HTTP and tool filter parameters into one.
public static class FilingFilters
{
    public static Expression<Func<FilingChunkRecord, bool>>? Build(
        IReadOnlyCollection<string>? tickers, IReadOnlyCollection<string>? formTypes, IReadOnlyCollection<int>? fiscalYears,
        bool excludeInstructionLike)
    {
        // Captured arrays, not lists: connectors translate Contains over an array into SQL "= ANY(...)".
        string[] tickerValues = Normalize(tickers), formTypeValues = Normalize(formTypes);
        int[] years = [.. fiscalYears?.Distinct() ?? []];

        List<Expression<Func<FilingChunkRecord, bool>>> parts = [];
        if (tickerValues.Length > 0)
            parts.Add(r => tickerValues.Contains(r.Ticker));
        if (formTypeValues.Length > 0)
            parts.Add(r => formTypeValues.Contains(r.FormType));
        if (years.Length > 0)
            parts.Add(r => years.Contains(r.FiscalYear));
        if (excludeInstructionLike)
            parts.Add(r => !r.ContainsInstructionLikeText);

        return parts.Count == 0 ? null : parts.Aggregate(And);
    }

    private static string[] Normalize(IReadOnlyCollection<string>? values) =>
        [.. values?.Select(value => value.Trim().ToUpperInvariant()).Distinct() ?? []];

    // Rebinding the parameter instead of Expression.Invoke, which filter translators reject.
    private static Expression<Func<FilingChunkRecord, bool>> And(
        Expression<Func<FilingChunkRecord, bool>> left, Expression<Func<FilingChunkRecord, bool>> right) =>
        Expression.Lambda<Func<FilingChunkRecord, bool>>(
            Expression.AndAlso(left.Body, new ParameterReplacer(right.Parameters[0], left.Parameters[0]).Visit(right.Body)),
            left.Parameters);

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
