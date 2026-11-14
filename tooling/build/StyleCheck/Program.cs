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
                foreach (SyntaxToken opening in tree.GetRoot().DescendantTokens().Where(IsCodeOpeningBrace))
                {
                    SyntaxToken closing = opening.Parent!.ChildTokens().LastOrDefault(token => token.IsKind(SyntaxKind.CloseBraceToken));
                    if (closing.RawKind == 0 || closing.IsMissing)
                    {
                        Console.Error.WriteLine($"{relative}: malformed brace-delimited C# construct.");
                        violations++;
                        continue;
                    }

                    int line = tree.GetLineSpan(opening.Span).StartLinePosition.Line;
                    if (line == tree.GetLineSpan(closing.Span).StartLinePosition.Line)
                    {
                        Console.Error.WriteLine($"{relative}:{line + 1}: brace-delimited C# constructs must be multiline.");
                        violations++;
                    }
                }
            }
        }

        return violations == 0 ? 0 : 1;
    }

    private static bool IsCodeOpeningBrace(SyntaxToken token)
    {
        return !token.IsMissing && token.Parent is not InterpolationSyntax && token.IsKind(SyntaxKind.OpenBraceToken);
    }
}
