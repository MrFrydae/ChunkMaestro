"""Synthetic profile controls; these do not establish Windows runtime evidence."""
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import runtime_trace as trace


class RuntimeTraceTests(unittest.TestCase):
    def test_profile_environment_drops_inherited_hooks_and_credentials(self):
        source = {'PATH': 'pinned', 'GITHUB_TOKEN': 'sentinel', 'DOTNET_STARTUP_HOOKS': 'sentinel',
                  'DOTNET_EventPipeOutputPath': 'untrusted', 'DOTNET_EventPipeConfig': 'untrusted'}
        env = trace.trace_environment(source, Path('/owned/traces'))
        self.assertNotIn('GITHUB_TOKEN', env)
        self.assertNotIn('DOTNET_STARTUP_HOOKS', env)
        self.assertEqual(env['DOTNET_EventPipeOutputPath'], str(Path('/owned/traces') / 'process-{pid}.nettrace'))
        self.assertEqual(env['DOTNET_EventPipeConfig'], trace.EVENTPIPE['DOTNET_EventPipeConfig'])
        self.assertEqual(env['DOTNET_EventPipeCircularMB'], '20')

    def test_other_platform_refuses_before_process_execution(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(trace.platform, 'system', return_value='Darwin'), \
                patch.object(trace.subprocess, 'Popen') as launch, patch('sys.stdout', new_callable=io.StringIO):
            output = Path(folder) / 'profile'
            self.assertEqual(trace.run(output, Path(folder)), 1)
            launch.assert_not_called()
            result = json.loads((output / 'result.json').read_bytes())
            self.assertEqual(result['status'], 'UNRESOLVED')
            self.assertNotIn('test_count', result)


if __name__ == '__main__':
    unittest.main()
