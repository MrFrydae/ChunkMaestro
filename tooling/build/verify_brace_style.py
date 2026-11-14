"""Real syntax-policy fixtures, including empty blocks, properties and interpolation."""
from pathlib import Path
import subprocess
import tempfile

import build


def main():
    checker = build.ROOT / 'tooling/build/StyleCheck/bin/Release/net10.0/StyleCheck.dll'
    with tempfile.TemporaryDirectory(prefix='chunkmaestro-brace-policy-') as temporary:
        root = Path(temporary)
        source = root / 'src/Fixture.cs'
        source.parent.mkdir()
        source.write_bytes(b'''internal sealed class Fixture { public string Name { get; set; } = "value"; public void Run() { } }
''')
        try:
            build.dotnet(checker, root)
        except subprocess.CalledProcessError:
            pass
        else:
            raise AssertionError('Single-line braces were accepted.')
        assert b'public void Run() { }' in source.read_bytes(), 'Read-only checker modified source.'
        source.write_bytes(b'''internal sealed class Fixture
{
    // Braces in comments {} and interpolation aren't code blocks.
    public string Render(int value) => $"{{literal}} {value}";
}
''')
        build.dotnet(checker, root)
        print('PASS: single-line blocks/properties rejected; source unchanged; interpolation/comments accepted.')


if __name__ == '__main__':
    main()
