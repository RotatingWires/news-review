"""Create a consistent SQLite backup, including while the application is running."""

import argparse
from contextlib import closing
from pathlib import Path
import sqlite3


def main():
    parser = argparse.ArgumentParser(description="Back up Tech News Feed's SQLite database")
    parser.add_argument('--database', default='data/tech-news.db')
    parser.add_argument('--output', required=True, help='New backup file; existing files are never overwritten')
    args = parser.parse_args()
    source = Path(args.database).resolve()
    destination = Path(args.output).resolve()
    if not source.is_file():
        parser.error('Source database does not exist')
    destination.parent.mkdir(parents=True, exist_ok=True)
    try:
        with destination.open('xb'):
            pass
    except FileExistsError:
        parser.error('Output already exists; choose a new filename')
    try:
        with closing(sqlite3.connect(source.as_uri() + '?mode=ro', uri=True)) as src:
            with closing(sqlite3.connect(destination)) as dst:
                src.backup(dst)
                result = dst.execute('PRAGMA integrity_check').fetchone()[0]
                if result != 'ok':
                    raise RuntimeError(f'Backup integrity check failed: {result}')
    except Exception:
        # Only the new, exclusively-created destination belongs to this operation.
        destination.unlink(missing_ok=True)
        raise
    print(f'Backup created: {destination}')


if __name__ == '__main__':
    main()
