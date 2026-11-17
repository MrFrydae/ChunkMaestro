"""Read-only, bounded Stryker 5/VSTest diagnostic initial-case evidence.

Caller supplies trusted, source-bound expected hashes/command/case plan. This is
an additional initial-run predicate, never discovery, kill or qualification credit.
Only the observed single-runner, single-assembly, protocol-v7 xUnit shape is
supported. No deduplication, report-ID inference or warning heuristics.
"""
from datetime import datetime, timedelta
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import uuid
import xml.etree.ElementTree as ET

COMMIT = '6e77a3451bac4793e9c839c3ff4055c1b30ca3af'
ARCHIVE_SHA256 = '80702658bac0989e9416e43239ee9d0ca971cea79628bed2b7b1917edd07b269'
SUPPORTED_SDK = '10.0.404'
MAX_LOG_BYTES = 32 * 1024 * 1024
EXECUTOR = 'executor://xunit/VsTestRunner3/netcore/'
SOURCE_FILES = (
    'src/Stryker.CLI/Stryker.CLI/Logging/ApplicationLogging.cs',
    'src/Stryker.CLI/Stryker.CLI/Logging/LoggingInitializer.cs',
    'src/Stryker.TestRunner.VsTest/VsTestContextInformation.cs',
    'src/Stryker.TestRunner.VsTest/VsTestRunner.cs',
    'src/Stryker.TestRunner.VsTest/VsTestRunnerPool.cs',
    'src/Stryker.TestRunner.VsTest/RunEventHandler.cs',
    'src/Stryker.Core/Stryker.Core/Initialisation/InitialisationProcess.cs',
    'src/Stryker.Core/Stryker.Core/Initialisation/InitialTestProcess.cs',
)
SOURCE_HASHES = {'src/Stryker.CLI/Stryker.CLI/Logging/ApplicationLogging.cs': '825bee4bd891d3d09f54979910f94a6bdf9724dd3476711a57696c6258353574',
 'src/Stryker.CLI/Stryker.CLI/Logging/LoggingInitializer.cs': 'ed9e4e5b75d5ea85b11e0304eb257a1d0eec1595731e6721d53e52bfbcde7bab',
 'src/Stryker.Core/Stryker.Core/Initialisation/InitialTestProcess.cs': 'f706431389c9bb80e32290162e8326629494d2e9a335667c44f031903565ca5f',
 'src/Stryker.Core/Stryker.Core/Initialisation/InitialisationProcess.cs': 'a7f275167629458f86e60ebc71ce33781fac5b329fc931f11f6077a623a984ee',
 'src/Stryker.TestRunner.VsTest/RunEventHandler.cs': '70ff8dda34f6b5ca044abb707abcc7190ddbac06974bfc1fdba7e63dc3fc7b48',
 'src/Stryker.TestRunner.VsTest/VsTestContextInformation.cs': '0d704339d71d06c57d635d0e9e3a1bcba9189298cbb4d1666db95a83e1799fb9',
 'src/Stryker.TestRunner.VsTest/VsTestRunner.cs': '174ae64ae04b33149da7651639549f1ed02da4c37f49137d3679c4bb90d07d2e',
 'src/Stryker.TestRunner.VsTest/VsTestRunnerPool.cs': 'dcf3aeb86a68abf8983534d1b24c6c2315fa6e698ca326f5367a6fc43e30599f'}
WIRE = re.compile(r'^TpTrace Verbose: 0 : (\d+), \d+, (\d{4}/\d\d/\d\d), (\d\d:\d\d:\d\d\.\d{3}), (\d+), vstest.console.dll, (.*)$')
SEND = 'TestRequestSender.StartTestRun: Sending test run with message: '
RECEIVE = 'TestRequestSender.OnExecutionMessageReceived: Received message: '


def digest(data):
    return hashlib.sha256(data).hexdigest()


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def passed_statistics(value, count):
    return (isinstance(value, dict) and set(value) == {'ExecutedTests', 'Stats'} and
            type(value['ExecutedTests']) is int and value['ExecutedTests'] == count and
            isinstance(value['Stats'], dict) and set(value['Stats']) == {'Passed'} and
            type(value['Stats']['Passed']) is int and value['Stats']['Passed'] == count)


def load_json(data):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, 'duplicate JSON field')
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=pairs, parse_constant=lambda _: (_ for _ in ()).throw(ValueError('nonfinite JSON')))


def read_bound(path, expected):
    data = Path(path).read_bytes()
    require(digest(data) == expected, 'artifact hash mismatch')
    return data


def read_diagnostic(path, expected=None):
    with Path(path).open('rb') as stream:
        info = os.fstat(stream.fileno())
        require(stat.S_ISREG(info.st_mode), 'diagnostic is not a regular file')
        require(info.st_size <= MAX_LOG_BYTES, 'initial diagnostic exceeds 32 MiB bound')
        data = stream.read(info.st_size + 1)
        require(len(data) == info.st_size, 'diagnostic changed while reading')
    if expected is not None:
        require(digest(data) == expected, 'artifact hash mismatch')
    return data


def phase_window(data):
    require(len(data) <= MAX_LOG_BYTES, 'initial phase log exceeds 32 MiB bound')
    lines = data.decode('utf-8', errors='strict').splitlines()
    starts = [line for line in lines if '[INF] Number of tests found:' in line and 'Initial test run started.' in line]
    ends = [line for line in lines if '[DBG] Initial test run output:' in line]
    require(len(starts) == len(ends) == 1, 'ambiguous or missing initial phase')
    def stamp(line):
        return datetime.fromisoformat(line.split('  [', 1)[0])
    start, end = stamp(starts[0]), stamp(ends[0])
    require(start < end, 'invalid phase order')
    window = [line for line in lines if line[:4].isdigit() and start <= stamp(line) <= end]
    require(sum('testing assembly' in line for line in window) == 1, 'multiple or absent assemblies')
    require(not any('Retrying' in line for line in window), 'retried initial session')
    return start, end


def inspect_wire(data, start, end, cases, assembly, test_filter=None):
    """Return observed results; timestamps are local console/file logger time.

    Millisecond-rounded wire timestamps touching a phase edge are ambiguous.
    Only canonical console receive records count; host/send copies are never
    inputs. Duplicate canonical results, even identical, fail closed.
    """
    require(len(data) <= MAX_LOG_BYTES, 'initial wire log exceeds 32 MiB bound')
    planned = {(case['fqn'], case['display']) for case in cases}
    require(len(planned) == len(cases) and len(planned) > 0, 'empty/duplicate planned case set')
    require(all(all(isinstance(v, str) and v for v in pair) for pair in planned), 'invalid planned identity')
    seen = {}
    ids = set()
    active_ids = {}
    opened = False
    completed = False
    pid = None
    clock = -1
    records = []
    for number, line in enumerate(data.decode('utf-8', errors='strict').splitlines(), 1):
        match = WIRE.fullmatch(line)
        if not match:
            require(not (SEND in line or RECEIVE in line), 'unsupported wire prefix')
            continue
        process, day, at, ticks, text = match.groups()
        stamp = datetime.strptime(day + ' ' + at, '%Y/%m/%d %H:%M:%S.%f').replace(tzinfo=start.tzinfo)
        if not (text.startswith(SEND) or text.startswith(RECEIVE)):
            continue
        # Wire clocks lose sub-millisecond precision. Refuse any canonical
        # message whose timestamp bucket touches a phase edge before filtering.
        bucket_end = stamp + timedelta(milliseconds=1)
        require(not any(stamp <= edge <= bucket_end for edge in (start, end)),
                'ambiguous canonical message at phase boundary')
        if not start < stamp < end:
            continue
        require(int(ticks) >= clock, 'nonmonotonic wire clock')
        clock = int(ticks)
        if pid is None:
            pid = process
        require(pid == process, 'concurrent processes')
        prefix = SEND if text.startswith(SEND) else RECEIVE
        message = load_json(text[len(prefix):])
        require(type(message['Version']) is int and message['Version'] == 7, 'unsupported protocol version')
        kind, payload = message['MessageType'], message['Payload']
        records.append({'line': number, 'timestamp': stamp.isoformat(), 'clock': ticks, 'type': kind})
        if prefix == SEND:
            require(not opened and not completed and kind == 'TestExecution.StartWithSources', 'ambiguous/retried/unsupported session')
            require(payload['AdapterSourceMap'] == {'_none_': [assembly]}, 'unbound assembly')
            context = payload['TestExecutionContext']
            require(context['TestCaseFilter'] == test_filter and context['KeepAlive'] is False, 'unsupported selection/session')
            settings = ET.fromstring(payload['RunSettings'])
            require(settings.find('.//TestSessionTimeout') is None and settings.find('.//InProcDataCollectors') is None, 'coverage/mutation/timeout session')
            require(settings.findtext('.//MaxCpuCount') == '1' and settings.findtext('.//DisableParallelization') == 'true', 'parallel session')
            session_start = stamp
            opened = True
            continue
        require(opened and not completed, 'unbound or post-completion event')
        if kind == 'TestSession.Message':
            require(payload['MessageLevel'] == 0, 'runtime diagnostic error/warning')
            continue
        if kind == 'TestExecution.StatsChange':
            chunk = payload
        elif kind == 'TestExecution.Completed':
            args = payload['TestRunCompleteArgs']
            require(args['IsCanceled'] is False and args['IsAborted'] is False and args['Error'] is None, 'canceled/aborted/runtime error')
            require(passed_statistics(args['TestRunStatistics'], len(planned)), 'nonpassing completion statistics')
            require(payload['ExecutorUris'] == [EXECUTOR], 'unsupported executor')
            chunk = payload['LastRunTests']
            completed = True
        else:
            raise ValueError('unsupported execution message')
        require(isinstance(chunk, dict), 'missing result chunk')
        require(isinstance(chunk['NewTestResults'], list), 'missing results')
        for result in chunk['NewTestResults']:
            case = result['TestCase']
            identity = (case['FullyQualifiedName'], case['DisplayName'])
            require(identity in planned and identity not in seen, 'extra/duplicate case result')
            identifier = str(uuid.UUID(case['Id']))
            require(identity not in active_ids or active_ids[identity] == identifier, 'active/result ID conflict')
            require(identifier not in ids and uuid.UUID(identifier).int != 0, 'duplicate/empty wire ID')
            require(case['Source'] == assembly and case['ExecutorUri'] == EXECUTOR, 'unbound result source/adapter')
            require(result['DisplayName'] == case['DisplayName'], 'conflicting display identity')
            require(type(result['Outcome']) is int and result['Outcome'] == 1, 'case outcome is not Passed')
            require(result['ErrorMessage'] is None and result['ErrorStackTrace'] is None, 'result error')
            first = datetime.fromisoformat(result['StartTime'])
            last = datetime.fromisoformat(result['EndTime'])
            require(first.tzinfo is not None and last.tzinfo is not None and session_start <= first <= last <= stamp, 'invalid result timestamps')
            ids.add(identifier)
            seen[identity] = {'fqn': identity[0], 'display': identity[1], 'wire_id': identifier, 'outcome': 'Passed', 'start': result['StartTime'], 'end': result['EndTime']}
        require(passed_statistics(chunk['TestRunStatistics'], len(seen)), 'inconsistent chunk statistics')
        require(isinstance(chunk['ActiveTests'], list), 'missing active cases')
        for active in chunk['ActiveTests']:
            identity = (active['FullyQualifiedName'], active['DisplayName'])
            require(identity in planned and identity not in seen, 'unbound/already completed active case')
            identifier = str(uuid.UUID(active['Id']))
            require(active['Source'] == assembly and active['ExecutorUri'] == EXECUTOR, 'unbound active source/adapter')
            require(identity not in active_ids or active_ids[identity] == identifier, 'conflicting active ID')
            active_ids[identity] = identifier
        if completed:
            require(chunk['ActiveTests'] == [], 'unfinished active tests')
    require(opened and completed and set(seen) == planned, 'missing/incomplete initial case session')
    return {'cases': list(seen.values()), 'events': records}


def validate_initial_events(binding):
    """Strict read-only public API; failures are UNRESOLVED with credit=False.

    binding is trusted integration input, NOT self-authenticating evidence. The
    lead must bind its hashes and selected cases to the exact plan/source/tool
    provenance using existing gates. The collector integrates the phase/wire
    predicates under its existing source and artifact-transport contract.
    """
    try:
        require(binding['source_commit'] == COMMIT and binding['archive_sha256'] == ARCHIVE_SHA256, 'unsupported source provenance')
        require(binding['sdk_version'] == SUPPORTED_SDK, 'unsupported SDK')
        require(binding['source_hashes'] == SOURCE_HASHES, 'source differs from verified archive')
        for relative in SOURCE_FILES:
            read_bound(Path(binding['source_root']) / relative, binding['source_hashes'][relative])
        command = load_json(read_bound(binding['command_path'], binding['command_sha256']))
        require(command['args'] == binding['argv'] and command['cwd'] == binding['cwd'], 'command binding mismatch')
        require(command['timeout'] is False and type(command['exit_code']) is int and command['exit_code'] in (0, 2), 'command timeout/failure')
        require(binding['cancelled'] is False, 'external cancellation/unknown')
        argv = command['args']
        require(all(option in argv for option in ('--diag', '--log-to-file', '--config-file', '--output')), 'missing diagnostic options')
        read_diagnostic(command['log'], command['log_sha256'])
        read_bound(argv[0], binding['sdk_sha256'])
        read_bound(argv[1], binding['tool_sha256'])
        config_path = argv[argv.index('--config-file') + 1]
        config = load_json(read_bound(config_path, binding['config_sha256']))['stryker-config']
        require(type(config['concurrency']) is int and config['concurrency'] == 1 and isinstance(config['test-projects'], list) and len(config['test-projects']) == 1, 'unsupported concurrency/projects')
        test_filter = binding.get('test_case_filter')
        require(config.get('test-case-filter') == test_filter, 'unsupported configured selection')
        output = Path(argv[argv.index('--output') + 1])
        require(Path(binding['wire_path']) == output / 'logs/Runner 0-log.txt', 'unbound diagnostic path')
        require(Path(binding['phase_path']).parent == output / 'logs', 'unbound phase path')
        require(Path(binding['phase_path']).name.startswith('log-') and Path(binding['phase_path']).suffix == '.txt', 'unsupported phase log')
        phase = read_diagnostic(binding['phase_path'], binding['phase_sha256'])
        wire = read_diagnostic(binding['wire_path'], binding['wire_sha256'])
        start, end = phase_window(phase)
        result = inspect_wire(wire, start, end, binding['cases'], binding['assembly'], test_filter)
        return {'status': 'PASS', 'credit': False, 'initial_cases_passed': True,
                'phase_start': start.isoformat(), 'phase_end': end.isoformat(),
                'phase_sha256': binding['phase_sha256'],
                'phase_records': [line for line in phase.decode('utf-8').splitlines() if 'Initial test run started.' in line or 'Initial test run output:' in line],
                'wire_sha256': binding['wire_sha256'], 'command_sha256': binding['command_sha256'], **result}
    except (ValueError, KeyError, TypeError, AttributeError, OverflowError, RecursionError, OSError, UnicodeError, IndexError, ET.ParseError) as error:
        return {'status': 'UNRESOLVED', 'credit': False, 'initial_cases_passed': False,
                'reason': str(error)}
