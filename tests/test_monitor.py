import importlib.util
import pathlib
import tempfile
import unittest

path = pathlib.Path(__file__).resolve().parents[1] / "Scripts/monitor.py"
spec = importlib.util.spec_from_file_location("monitor", path)
monitor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(monitor)


class MonitorTests(unittest.TestCase):
    def test_outage_threshold_and_recovery(self):
        prior = {"down": False, "failures": 0}
        for count in (1, 2, 3):
            prior = monitor.assess(prior, False, 3)
            self.assertEqual(prior, {"down": count == 3, "failures": count})
        self.assertEqual(monitor.assess(prior, False, 3), prior)
        self.assertEqual(monitor.assess(prior, True, 3), {"down": False, "failures": 0})

    def test_urls_and_state(self):
        self.assertEqual(monitor.health_url("https://test.example/jellyfin/health"), "https://test.example/jellyfin/health")
        with self.assertRaises(ValueError):
            monitor.discord_url("https://discord.com.evil.example/api/webhooks/123/abcdefghijk")
        with tempfile.TemporaryDirectory() as folder:
            target = pathlib.Path(folder) / "state.json"
            self.assertEqual(monitor.load_state(target), {"down": False, "failures": 0})
            monitor.save_state(target, {"down": True, "failures": 3})
            self.assertEqual(monitor.load_state(target), {"down": True, "failures": 3})
            target.write_text("x" * 1025)
            with self.assertRaises(ValueError):
                monitor.load_state(target)


if __name__ == "__main__":
    unittest.main()
