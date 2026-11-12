using System.Text.RegularExpressions;

namespace ButchCrawler.App;

public sealed class CollectionSearchQueryException(string message) : Exception(message);

public sealed record CollectionSearchPlan(
    IReadOnlyList<string> Terms,
    IReadOnlyList<int> PositiveTermIndexes,
    string SqlPredicate,
    bool UsesBooleanLogic);

/// <summary>Parses the small, user-facing Boolean language used by collection search.</summary>
public static class CollectionSearchQuery
{
    private enum Kind { Term, And, Or, Not, Left, Right, End }
    private sealed record Token(Kind Kind, string Text = "", bool Quoted = false);
    private abstract record Node;
    private sealed record TermNode(string Value) : Node;
    private sealed record AndNode(Node Left, Node Right) : Node;
    private sealed record OrNode(Node Left, Node Right) : Node;
    private sealed record NotNode(Node Child) : Node;

    public static CollectionSearchPlan Parse(string query)
    {
        query ??= string.Empty;
        var tokens = Tokenize(query);
        var usesBoolean = tokens.Any(t => t.Kind is Kind.And or Kind.Or or Kind.Not or Kind.Left or Kind.Right || t.Quoted);
        if (!usesBoolean)
        {
            var terms = SqlAiRepository.SearchTerms(query);
            return new CollectionSearchPlan(terms, Enumerable.Range(0, terms.Length).ToArray(),
                terms.Length == 0 ? "1=1" : string.Join(" OR ", Enumerable.Range(0, terms.Length).Select(MatchSql)), false);
        }

        var parser = new Parser(tokens);
        var root = parser.Parse();
        var termsList = new List<string>();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var positive = new HashSet<int>();

        string Compile(Node node, bool negated = false) => node switch
        {
            TermNode term => CompileTerm(term.Value, negated),
            AndNode and => $"({Compile(and.Left, negated)} AND {Compile(and.Right, negated)})",
            OrNode or => $"({Compile(or.Left, negated)} OR {Compile(or.Right, negated)})",
            NotNode not => $"NOT ({Compile(not.Child, !negated)})",
            _ => throw new InvalidOperationException("Unknown search expression.")
        };

        string CompileTerm(string value, bool negated)
        {
            if (!indexes.TryGetValue(value, out var index))
            {
                if (termsList.Count == 12)
                    throw new CollectionSearchQueryException("Use no more than 12 search words or quoted phrases at one time.");
                index = termsList.Count; indexes[value] = index; termsList.Add(value);
            }
            if (!negated) positive.Add(index);
            return MatchSql(index);
        }

        var predicate = Compile(root);
        return new CollectionSearchPlan(termsList, positive.Order().ToArray(), predicate, true);
    }

    private static string MatchSql(int index) => $"(CHARINDEX(@t{index},Body)>0 OR CHARINDEX(@t{index},Source)>0)";

    private static IReadOnlyList<Token> Tokenize(string query)
    {
        var tokens = new List<Token>();
        for (var i = 0; i < query.Length;)
        {
            if (char.IsWhiteSpace(query[i])) { i++; continue; }
            if (query[i] == '(') { tokens.Add(new(Kind.Left)); i++; continue; }
            if (query[i] == ')') { tokens.Add(new(Kind.Right)); i++; continue; }
            if (query[i] == '"')
            {
                var end = query.IndexOf('"', i + 1);
                if (end < 0) throw new CollectionSearchQueryException("A quoted phrase is missing its closing quotation mark.");
                var phrase = Regex.Replace(query[(i + 1)..end], @"\s+", " ").Trim();
                if (phrase.Length < 2) throw new CollectionSearchQueryException("Put at least two characters inside quotation marks.");
                tokens.Add(new(Kind.Term, phrase, true)); i = end + 1; continue;
            }

            var start = i;
            while (i < query.Length && !char.IsWhiteSpace(query[i]) && query[i] is not ('(' or ')' or '"')) i++;
            var raw = query[start..i];
            if (raw.Equals("AND", StringComparison.OrdinalIgnoreCase)) tokens.Add(new(Kind.And));
            else if (raw.Equals("OR", StringComparison.OrdinalIgnoreCase)) tokens.Add(new(Kind.Or));
            else if (raw.Equals("NOT", StringComparison.OrdinalIgnoreCase)) tokens.Add(new(Kind.Not));
            else
            {
                var match = Regex.Match(raw, @"[\p{L}\p{N}][\p{L}\p{N}'’\-]*");
                if (match.Success && match.Value.Length >= 2) tokens.Add(new(Kind.Term, match.Value));
            }
        }
        tokens.Add(new(Kind.End));
        return tokens;
    }

    private sealed class Parser(IReadOnlyList<Token> tokens)
    {
        private int _position;
        private Token Current => tokens[_position];

        public Node Parse()
        {
            if (Current.Kind == Kind.End)
                throw new CollectionSearchQueryException("Enter a name, place, date, phrase, or other useful search words.");
            var node = ParseOr();
            if (Current.Kind != Kind.End)
                throw new CollectionSearchQueryException(Current.Kind == Kind.Right
                    ? "A closing parenthesis does not have a matching opening parenthesis."
                    : "Check the Boolean words and parentheses in this search.");
            return node;
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (true)
            {
                if (Take(Kind.Or)) { left = new OrNode(left, ParseAnd()); continue; }
                // Adjacent ordinary words retain the original novice-friendly OR behavior.
                if (Current.Kind is Kind.Term or Kind.Left) { left = new OrNode(left, ParseAnd()); continue; }
                return left;
            }
        }

        private Node ParseAnd()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Take(Kind.And)) { left = new AndNode(left, ParseUnary()); continue; }
                // "Lincoln NOT Nebraska" is friendly shorthand for "Lincoln AND NOT Nebraska".
                if (Current.Kind == Kind.Not) { left = new AndNode(left, ParseUnary()); continue; }
                return left;
            }
        }

        private Node ParseUnary() => Take(Kind.Not) ? new NotNode(ParseUnary()) : ParsePrimary();

        private Node ParsePrimary()
        {
            if (Current.Kind == Kind.Term) { var value = Current.Text; _position++; return new TermNode(value); }
            if (Take(Kind.Left))
            {
                if (Current.Kind == Kind.Right) throw new CollectionSearchQueryException("Parentheses must contain search words.");
                var node = ParseOr();
                if (!Take(Kind.Right)) throw new CollectionSearchQueryException("An opening parenthesis is missing its closing parenthesis.");
                return node;
            }
            if (Current.Kind is Kind.And or Kind.Or)
                throw new CollectionSearchQueryException($"{Current.Kind.ToString().ToUpperInvariant()} needs search words on both sides.");
            if (Current.Kind == Kind.Right)
                throw new CollectionSearchQueryException("A closing parenthesis does not have a matching opening parenthesis.");
            throw new CollectionSearchQueryException("The search ends with an unfinished Boolean instruction.");
        }

        private bool Take(Kind kind)
        {
            if (Current.Kind != kind) return false;
            _position++; return true;
        }
    }
}
