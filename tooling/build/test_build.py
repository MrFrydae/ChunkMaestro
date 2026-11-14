"""Regression checks for fail-closed build tooling; no product-feature assertions."""
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import build
import qa_evidence


class BuildCommands(unittest.TestCase):
    def test_sync_discovers_projects_and_rejects_stale_solution(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for relative in ('src/A/A.csproj', 'tests/B/B.csproj', 'src/A/obj/Noise.csproj'):
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.touch()
            with patch.object(build, 'ROOT', root), patch.object(build, 'SOLUTION', root / 'Test.slnx'):
                build.sync()
                solution = (root / 'Test.slnx').read_text()
                self.assertIn('src/A/A.csproj', solution)
                self.assertIn('tests/B/B.csproj', solution)
                self.assertNotIn('Noise', solution)
                build.sync(check=True)
                (root / 'src/C.csproj').touch()
                with self.assertRaisesRegex(RuntimeError, 'Solution differs'):
                    build.sync(check=True)

    def test_discovery_uses_same_release_configuration_as_execution(self):
        project = Path('ReleaseOnlyTests.csproj')
        with patch.object(build, 'projects', return_value=[project]), \
                patch.object(build, 'dotnet', return_value='false') as dotnet:
            build.test(allow_absent=True)
            dotnet.assert_called_once_with('msbuild', project, '-property:Configuration=Release',
                                           '-getProperty:IsTestProject', capture=True)

    def test_format_includes_owned_generated_source_without_sdk_intermediates(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for relative in ('src/Fixture/Owned.g.cs', 'src/Fixture/obj/Sdk.g.cs',
                             'tests/Fixture/bin/Generated.cs'):
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.touch()
            with patch.object(build, 'ROOT', root), patch.object(build, 'dotnet') as dotnet:
                build.format_check()
                arguments = dotnet.call_args.args
                self.assertIn('--include-generated', arguments)
                self.assertIn('src/Fixture/Owned.g.cs', arguments)
                self.assertNotIn('src/Fixture/obj/Sdk.g.cs', arguments)
                self.assertNotIn('tests/Fixture/bin/Generated.cs', arguments)

    def test_no_tests_is_error_for_explicit_test_command(self):
        with patch.object(build, 'projects', return_value=[]):
            with self.assertRaisesRegex(RuntimeError, 'no .NET test projects'):
                build.test()

    def test_missing_lock_fails_before_dotnet(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(build, 'projects', return_value=[Path(directory) / 'A.csproj']), \
                    patch.object(build, 'dotnet') as dotnet:
                with self.assertRaisesRegex(RuntimeError, 'Missing lock'):
                    build.restore()
                dotnet.assert_not_called()

    def test_stale_trx_cannot_supply_test_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / '.artifacts/tests/0'
            output.mkdir(parents=True)
            (output / 'stale.trx').write_text('<TestRun />')
            with patch.object(build, 'ROOT', root), \
                    patch.object(build, 'projects', return_value=[root / 'Suite.csproj']), \
                    patch.object(build, 'dotnet', side_effect=['true', None]):
                with self.assertRaisesRegex(RuntimeError, 'No TRX produced'):
                    build.test()
            self.assertFalse((output / 'stale.trx').exists())

    def test_zero_executed_tests_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def invoke(*args, **kwargs):
                if args[0] == 'msbuild':
                    return 'true'
                output = root / '.artifacts/tests/0'
                output.mkdir(parents=True)
                (output / 'empty.trx').write_text(
                    '<TestRun><ResultSummary><Counters executed="0" />'
                    '</ResultSummary></TestRun>')

            with patch.object(build, 'ROOT', root), \
                    patch.object(build, 'projects', return_value=[root / 'Suite.csproj']), \
                    patch.object(build, 'dotnet', side_effect=invoke):
                with self.assertRaisesRegex(RuntimeError, 'Zero tests executed'):
                    build.test()

    def test_wrong_sdk_rejected(self):
        with patch.object(build, 'dotnet', return_value='11.0.100'):
            with self.assertRaisesRegex(RuntimeError, 'Expected .NET SDK 10'):
                build.sdk()

    def test_qa_missing_stale_and_self_review_rejected(self):
        head = 'a' * 40
        for report in ({}, {'head': head, 'implementers': ['author']},
                       {'head': head, 'implementers': ['author'],
                        'reviews': [{'reviewer': 'AUTHOR', 'role': 'functional'}]}):
            with self.subTest(report=report), self.assertRaises(ValueError):
                qa_evidence.validate(report, head)


if __name__ == '__main__':
    unittest.main()
