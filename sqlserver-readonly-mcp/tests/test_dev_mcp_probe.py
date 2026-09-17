import sys
import time
import unittest
from dev_mcp_probe import Probe, ProbeFailure


class ProbeTests(unittest.TestCase):
    def test_startup_exit_is_immediate(self):
        probe = Probe([sys.executable, "-c", "import sys; sys.stderr.write('configuration failed'); sys.exit(2)"])
        try:
            started = time.monotonic()
            with self.assertRaises(ProbeFailure) as caught:
                probe.call("initialize", {}, timeout=20)
            self.assertEqual(caught.exception.summary["kind"], "process_exited")
            self.assertLess(time.monotonic()-started, 3)
            self.assertNotIn("configuration failed", str(caught.exception))
        finally:
            probe.close()

    def test_notifications_do_not_reset_deadline(self):
        code = "import time; \nwhile True: print('{\"jsonrpc\":\"2.0\",\"method\":\"notice\"}',flush=True); time.sleep(.01)"
        probe = Probe([sys.executable, "-c", code])
        try:
            started = time.monotonic()
            with self.assertRaises(ProbeFailure) as caught:
                probe.call("initialize", {}, timeout=.3)
            self.assertEqual(caught.exception.summary["kind"], "client_deadline")
            self.assertLess(time.monotonic()-started, 2)
        finally:
            probe.close()

if __name__ == "__main__":
    unittest.main()
