"""Single-file runtime logging with TaskList's truncate-in-place size policy."""

import logging
from pathlib import Path

LOG_FORMAT = '%(asctime)s %(levelname)s %(name)s: %(message)s'


class SingleFileHandler(logging.Handler):
    def __init__(self, path, max_bytes=10 * 1024 * 1024):
        super().__init__()
        self.path = Path(path)
        self.max_bytes = max_bytes
        self.stream = None
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.stream = self.path.open('a+b')

    def emit(self, record):
        # Handler.handle serializes writes with its standard logging lock.
        try:
            entry = (self.format(record) + '\n').encode('utf-8', errors='backslashreplace')
            if self.stream is None:
                return
            self.stream.seek(0, 2)
            if self.stream.tell() + len(entry) > self.max_bytes:
                self.stream.seek(0)
                self.stream.truncate()
            self.stream.write(entry)
            self.stream.flush()
        except (OSError, ValueError):
            # File failures must not stop the service or its console logging.
            self.close()

    def close(self):
        self.acquire()
        try:
            if self.stream is not None:
                try:
                    self.stream.close()
                except OSError:
                    pass
                self.stream = None
        finally:
            self.release()
            super().close()


def configure_logging():
    formatter = logging.Formatter(LOG_FORMAT)
    logging.basicConfig(level=logging.INFO, format=LOG_FORMAT, handlers=[logging.StreamHandler()])
    try:
        handler = SingleFileHandler(Path('logs') / 'console.log')
    except OSError as exc:
        logging.warning('File logging unavailable; continuing with console output: %s', exc)
        return
    handler.setFormatter(formatter)
    logging.getLogger().addHandler(handler)
