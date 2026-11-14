using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ChunkMaestro.StyleCheck;

internal static class Program
{
    private static int Main(string[] arguments)
    {
        bool fix = arguments.Contains("--fix", StringComparer.Ordinal);
        string root = Path.GetFullPath(arguments.FirstOrDefault(argument => argument != "--fix") ?? ".");
        int violations = 0;
        foreach (string directory in new[]
{
"src", "tests", "tooling/build"
}
)
        {
            string path = Path.Combine(root, directory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                {
                    continue;
                }

                SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
                SyntaxNode syntax = tree.GetRoot();
                SyntaxToken[] braces = syntax.DescendantTokens().Where(IsBrace).ToArray();
                HashSet<SyntaxToken> changes = [];
                foreach (SyntaxToken opening in braces.Where(token => token.IsKind(SyntaxKind.OpenBraceToken)))
                {
                    SyntaxToken closing = opening.Parent!.ChildTokens().Last(token => token.IsKind(SyntaxKind.CloseBraceToken));
                    int line = tree.GetLineSpan(opening.Span).StartLinePosition.Line;
                    if (line == tree.GetLineSpan(closing.Span).StartLinePosition.Line)
                    {
                        Console.Error.WriteLine($"{relative}:{line + 1}: brace-delimited C# constructs must be multiline.");
                        violations++;
                        changes.Add(opening);
                        changes.Add(closing);
                    }
                }

                if (fix)
                {
                    SyntaxNode rewritten = syntax.ReplaceTokens(changes, (original, rewritten) =>
                        original.IsKind(SyntaxKind.OpenBraceToken)
                            ? rewritten.WithLeadingTrivia(original.LeadingTrivia.Insert(0, SyntaxFactory.LineFeed))
                                .WithTrailingTrivia(original.TrailingTrivia.Add(SyntaxFactory.LineFeed))
                            : rewritten.WithLeadingTrivia(original.LeadingTrivia.Insert(0, SyntaxFactory.LineFeed)));
                    Dictionary<SyntaxToken, SyntaxToken> join = [];
                    foreach (SyntaxToken token in rewritten.DescendantTokens())
                    {
                        SyntaxToken previous = token.GetPreviousToken();
                        if (previous.IsKind(SyntaxKind.CloseBraceToken) &&
                            token.IsKind(SyntaxKind.EqualsToken) && previous.Parent is AccessorListSyntax &&
                            previous.TrailingTrivia.Concat(token.LeadingTrivia).All(trivia =>
                                trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
                        {
                            join[previous] = previous.WithTrailingTrivia(SyntaxFactory.Space);
                            join[token] = token.WithLeadingTrivia();
                        }
                    }

                    rewritten = rewritten.ReplaceTokens(join.Keys, (original, _) => join[original]);
                    File.WriteAllText(file, rewritten.ToFullString());
                }
            }
        }

        return fix || violations == 0 ? 0 : 1;
    }

    private static bool IsBrace(SyntaxToken token)
    {
        return !token.IsMissing && token.Parent is not InterpolationSyntax &&
            (token.IsKind(SyntaxKind.OpenBraceToken) || token.IsKind(SyntaxKind.CloseBraceToken));
    }
}
