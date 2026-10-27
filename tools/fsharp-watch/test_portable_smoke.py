"""Keep Windows liveness assertions separate from process termination APIs."""
import ctypes
import unittest
from unittest.mock import Mock, patch

import portable_smoke


class WindowsLifetimeTests(unittest.TestCase):
    def test_restart_waits_without_posix_signals(self):
        watch = object.__new__(portable_smoke.WindowsWatch)
        watch.job = Mock()
        watch.job.alive.side_effect = [True, False]
        previous = {"pid": 41, "count": 10}
        current = {"pid": 42, "count": 1}
        with patch.object(portable_smoke.os, "kill", side_effect=AssertionError("POSIX signal on Windows")):
            with patch.object(portable_smoke.time, "sleep"):
                portable_smoke.assert_edit(watch, previous, current, False)
        self.assertEqual(2, watch.job.alive.call_count)
        watch.job.alive.assert_called_with(41)

    def test_hot_reload_requires_same_process_and_counter_progress(self):
        watch = object.__new__(portable_smoke.WindowsWatch)
        watch.job = Mock()
        previous = {"pid": 41, "count": 10}
        with self.assertRaisesRegex(AssertionError, "state did not survive"):
            portable_smoke.assert_edit(watch, previous, {"pid": 41, "count": 1}, True)
        with self.assertRaisesRegex(AssertionError, "restarted"):
            portable_smoke.assert_edit(watch, previous, {"pid": 42, "count": 11}, True)
        watch.job.alive.assert_not_called()

    def test_windows_liveness_uses_wait_only_and_closes_handle(self):
        job = object.__new__(portable_smoke.WindowsJob)
        job.kernel = Mock(spec=["OpenProcess", "WaitForSingleObject", "CloseHandle"])
        job.kernel.OpenProcess.return_value = 123
        job.kernel.WaitForSingleObject.return_value = 258
        self.assertTrue(job.alive(41))
        job.kernel.OpenProcess.assert_called_once_with(0x100000, False, 41)
        job.kernel.WaitForSingleObject.assert_called_once_with(123, 0)
        job.kernel.CloseHandle.assert_called_once_with(123)

    def test_windows_exited_process_is_not_alive(self):
        job = object.__new__(portable_smoke.WindowsJob)
        job.kernel = Mock(spec=["OpenProcess", "WaitForSingleObject", "CloseHandle"])
        job.kernel.OpenProcess.return_value = 123
        job.kernel.WaitForSingleObject.return_value = 0
        self.assertFalse(job.alive(41))
        job.kernel.CloseHandle.assert_called_once_with(123)

    def test_windows_missing_process_is_not_alive(self):
        job = object.__new__(portable_smoke.WindowsJob)
        job.kernel = Mock(spec=["OpenProcess", "WaitForSingleObject", "CloseHandle"])
        job.kernel.OpenProcess.return_value = None
        with patch.object(ctypes, "get_last_error", return_value=87, create=True):
            self.assertFalse(job.alive(41))
        job.kernel.WaitForSingleObject.assert_not_called()


if __name__ == "__main__":
    unittest.main()
