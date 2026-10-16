"""One-time, non-destructive import of the database from the initial app name."""

from contextlib import closing
import logging
from pathlib import Path
import sqlite3
import tempfile
import os


def database_path(directory):
    directory = Path(directory)
    current = directory / 'newsreview.db'
    legacy = directory / 'tech-news.db'
    if current.exists() or not legacy.exists():
        return current
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(prefix='migration-', suffix='.db', dir=directory, delete=False) as handle:
            temporary = Path(handle.name)
        with closing(sqlite3.connect(legacy.resolve().as_uri() + '?mode=ro', uri=True)) as source:
            with closing(sqlite3.connect(temporary)) as destination:
                source.backup(destination)
                if destination.execute('PRAGMA integrity_check').fetchone()[0] != 'ok':
                    raise ValueError('The previous database failed its integrity check')
        os.replace(temporary, current)
        logging.info('Imported the previous database into %s; the original is retained as a recovery copy', current)
    finally:
        if temporary:
            temporary.unlink(missing_ok=True)
    return current
