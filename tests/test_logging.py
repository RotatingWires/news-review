"""Single-file logging uses real files, byte limits, and the normal logging API."""
import logging
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from newsreview.runtime_logging import SingleFileHandler


class LoggingTests(unittest.TestCase):
    def test_truncates_same_file_by_utf8_bytes_without_rotated_copies(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'logs' / 'console.log'
            handler = SingleFileHandler(path, max_bytes=32)
            try:
                logger = logging.Logger('fixture', level=logging.INFO)
                logger.addHandler(handler)
                logger.info('old')
                original_inode = path.stat().st_ino
                logger.info('€' * 10)
                self.assertEqual(path.read_bytes(), ('€' * 10 + '\n').encode())
                self.assertEqual(path.stat().st_ino, original_inode)
                logger.info('next')
                self.assertEqual(path.read_text(), 'next\n')
                self.assertEqual(list(path.parent.iterdir()), [path])
            finally:
                handler.close()

    def test_appends_across_restarts_and_records_exceptions(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'console.log'
            path.write_text('previous run\n')
            handler = SingleFileHandler(path)
            try:
                self.assertEqual(handler.max_bytes, 10 * 1024 * 1024)
                logger = logging.Logger('fixture', level=logging.INFO)
                logger.addHandler(handler)
                try:
                    raise ValueError('fixture failure')
                except ValueError:
                    logger.exception('Request failed')
                content = path.read_text()
                self.assertTrue(content.startswith('previous run\nRequest failed\n'))
                self.assertIn('Traceback', content)
                self.assertIn('ValueError: fixture failure', content)
            finally:
                handler.close()

    def test_file_write_failure_does_not_raise(self):
        with tempfile.TemporaryDirectory() as directory:
            handler = SingleFileHandler(Path(directory) / 'console.log')
            handler.stream.close()
            logger = logging.Logger('fixture', level=logging.INFO)
            logger.addHandler(handler)
            logger.info('Storage became unavailable')
            self.assertIsNone(handler.stream)
            logger.info('Service can continue')
            handler.close()

    def test_unavailable_log_directory_keeps_console_output(self):
        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / 'logs').write_text('A file blocks directory creation')
            environment = dict(os.environ)
            environment['PYTHONPATH'] = str(Path(__file__).resolve().parent.parent)
            result = subprocess.run([sys.executable, '-c',
                                     'from newsreview.runtime_logging import configure_logging; '
                                     'import logging; configure_logging(); logging.info("Service still running")'],
                                    cwd=directory, env=environment, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('File logging unavailable', result.stderr)
            self.assertIn('Service still running', result.stderr)
            self.assertNotIn('AttributeError', result.stderr)
