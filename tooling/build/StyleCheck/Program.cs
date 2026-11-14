using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ChunkMaestro.StyleCheck;

internal static class Program
{
    private static readonly string[] SourceDirectories = ["src", "tests", "tooling/build"];

    private static int Main(string[] arguments)
    {
        string root = Path.GetFullPath(arguments.FirstOrDefault() ?? ".");
        int violations = 0;
        foreach (string directory in SourceDirectories)
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
                foreach (SyntaxNode node in syntax.DescendantNodesAndSelf())
                {
                    if (node is NamespaceDeclarationSyntax)
                    {
                        Report(tree, node.GetFirstToken(), relative, "file-scoped namespaces are required.");
                        violations++;
                    }

                    if (UsesInferredType(node))
                    {
                        Report(tree, node.GetFirstToken(), relative, "explicit C# types are required.");
                        violations++;
                    }

                    IEnumerable<StatementSyntax> statements = node is CompilationUnitSyntax unit
                        ? unit.Members.OfType<GlobalStatementSyntax>().Select(statement => statement.Statement)
                        : node.ChildNodes().OfType<StatementSyntax>();
                    StatementSyntax? previous = null;
                    foreach (StatementSyntax statement in statements)
                    {
                        if (previous is not null && Line(tree, previous.GetLastToken()) == Line(tree, statement.GetFirstToken()))
                        {
                            Report(tree, statement.GetFirstToken(), relative, "use one C# statement per line.");
                            violations++;
                        }

                        previous = statement;
                    }
                }

                foreach (SyntaxToken opening in syntax.DescendantTokens().Where(IsCodeOpeningBrace))
                {
                    SyntaxToken closing = opening.Parent!.ChildTokens().LastOrDefault(token => token.IsKind(SyntaxKind.CloseBraceToken));
                    if (closing.RawKind == 0 || closing.IsMissing)
                    {
                        Console.Error.WriteLine($"{relative}: malformed brace-delimited C# construct.");
                        violations++;
                        continue;
                    }

                    int line = tree.GetLineSpan(opening.Span).StartLinePosition.Line;
                    if (line == Line(tree, closing) || line == Line(tree, opening.GetPreviousToken()) ||
                        line == Line(tree, opening.GetNextToken()) || Line(tree, closing.GetPreviousToken()) == Line(tree, closing))
                    {
                        Report(tree, opening, relative, "use multiline C# constructs with Allman braces.");
                        violations++;
                    }
                }
            }
        }

        return violations == 0 ? 0 : 1;
    }

    private static bool UsesInferredType(SyntaxNode node)
    {
        TypeSyntax? type = node switch
        {
            VariableDeclarationSyntax declaration => declaration.Type,
            ForEachStatementSyntax statement => statement.Type,
            DeclarationExpressionSyntax expression => expression.Type,
            _ => null
        };
        return node is VarPatternSyntax || (type is not null &&
            type.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(identifier => identifier.IsVar));
    }

    private static int Line(SyntaxTree tree, SyntaxToken token)
    {
        return token.RawKind == 0 ? -1 : tree.GetLineSpan(token.Span).StartLinePosition.Line;
    }

    private static void Report(SyntaxTree tree, SyntaxToken token, string relative, string message)
    {
        Console.Error.WriteLine($"{relative}:{Line(tree, token) + 1}: {message}");
    }

    private static bool IsCodeOpeningBrace(SyntaxToken token)
    {
        return !token.IsMissing && token.Parent is not InterpolationSyntax && token.IsKind(SyntaxKind.OpenBraceToken);
    }
}
