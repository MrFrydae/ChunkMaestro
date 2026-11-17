"""Synthetic phase-edge denials through producer and final accounting."""
import unittest

import test_cell_ci as cell
import test_test_cell_ci as fixtures


class PhaseEdgeTests(unittest.TestCase):
    def test_canonical_messages_at_or_overlapping_each_phase_edge_refuse_credit(self):
        edges = (
            ('12:00:00.000', '12:00:00+00:00', '12:01:00+00:00'),
            ('12:01:00.000', '12:00:00+00:00', '12:01:00+00:00'),
            ('12:00:00.000', '12:00:00.000500+00:00', '12:01:00+00:00'),
            ('12:00:59.999', '12:00:00+00:00', '12:00:59.999500+00:00'),
        )
        for message in ('start', 'completed'):
            for timestamp, start, end in edges:
                with self.subTest(message=message, timestamp=timestamp, start=start, end=end):
                    fixture = fixtures.Cells()
                    try:
                        fixture.setUp()
                        self.assertEqual(0, fixture.validate()[0])
                        folder = fixture.folders[0]
                        wire = folder / 'mutation/logs/Runner 0-log.txt'
                        raw = wire.read_bytes()
                        original = raw.splitlines()[0 if message == 'start' else -1]
                        observed = b'12:00:01.000' if message == 'start' else b'12:00:06.000'
                        added = original.replace(observed, timestamp.encode())
                        wire.write_bytes(added + b'\n' + raw)
                        phase = folder / 'mutation/logs/log-20261117.txt'
                        phase.write_bytes(phase.read_bytes().replace(b'12:00:00+00:00', start.encode())
                                          .replace(b'12:01:00+00:00', end.encode()))
                        with self.assertRaisesRegex(ValueError, 'phase boundary'):
                            cell.initial_case_evidence(folder, fixture.plan, 0, str(fixture.root))
                        fixture.reseal(lambda directory, value: None)
                        with self.assertRaisesRegex(ValueError, 'phase boundary'):
                            fixture.validate()
                    finally:
                        fixture.doCleanups()


if __name__ == '__main__':
    unittest.main()
