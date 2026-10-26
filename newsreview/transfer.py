"""Bounded ZIP backup/restore; imported SQL, triggers and views are never installed."""

from contextlib import closing
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import secrets
import sqlite3
import tempfile
import zipfile

from . import __version__
from .defaults import SETTINGS, SOURCE_UPDATES
from .network import valid_url
from .store import SCHEMA_VERSION, initialize_topics
from . import validation

MAX_UPLOAD = 256 * 1024 * 1024
MAX_DATABASE = 1024 * 1024 * 1024
TABLES = ('sources', 'settings', 'stories', 'runs')
FORMAT = 'newsreview-backup'


def _readonly(path):
    connection = sqlite3.connect(Path(path).resolve().as_uri() + '?mode=ro', uri=True)
    connection.row_factory = sqlite3.Row
    connection.execute('PRAGMA trusted_schema=OFF')
    return connection


def _digest(path):
    with Path(path).open('rb') as handle:
        return hashlib.file_digest(handle, 'sha256').hexdigest()


def create_archive(store, output):
    """Snapshot committed WAL data; no authentication/environment secrets are included."""
    store.settings()  # Report a missing runtime database before opening the snapshot.
    with tempfile.TemporaryDirectory(prefix='newsreview-export-') as directory:
        snapshot = Path(directory) / 'newsreview.db'
        with closing(_readonly(store.path)) as source, closing(sqlite3.connect(snapshot)) as destination:
            source.backup(destination)
            destination.execute('PRAGMA journal_mode=DELETE')
            version = destination.execute('PRAGMA user_version').fetchone()[0]
            totals = {table: destination.execute(f'SELECT COUNT(*) FROM {table}').fetchone()[0] for table in TABLES}
        if snapshot.stat().st_size > MAX_DATABASE:
            raise ValueError('The database exceeds the 1 GiB ZIP restore limit; use the offline SQLite backup command instead')
        manifest = {'format': FORMAT, 'format_version': 1, 'application_version': __version__,
                    'schema_version': version, 'exported_at': datetime.now(timezone.utc).isoformat(),
                    'sha256': _digest(snapshot), 'totals': totals}
        with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            archive.write(snapshot, 'newsreview.db')
            archive.writestr('manifest.json', json.dumps(manifest, indent=2))
    if Path(output).stat().st_size > MAX_UPLOAD:
        raise ValueError('The compressed backup exceeds the 256 MiB ZIP upload limit; use the offline SQLite backup command instead')
    try:
        os.chmod(output, 0o600)
    except OSError:
        pass
    return manifest


def _version(value):
    if not isinstance(value, str) or not value or any(not p.isdigit() for p in value.split('.')):
        raise ValueError('Backup has an invalid application version')
    return tuple(int(p) for p in value.split('.'))


def _check_date(value, *, required=False):
    if value is None and not required:
        return
    if not isinstance(value, str) or len(value) > 80:
        raise ValueError('Backup contains an invalid timestamp')
    try:
        parsed = datetime.fromisoformat(value)
        if parsed.tzinfo is None:
            raise ValueError('Missing timezone')
    except ValueError as exc:
        raise ValueError('Backup contains an invalid timestamp') from exc


def _check_text(value, limit, name, *, nullable=False):
    if value is None and nullable:
        return
    if not isinstance(value, str) or len(value) > limit:
        raise ValueError(f'Backup contains invalid {name}')


def _validate_rows(database):
    settings = {r['key']: json.loads(r['value']) for r in database.execute('SELECT * FROM settings')}
    if set(settings) - {*SETTINGS, 'topics', 'sources_seeded', 'history_cleared_at', 'data_revision', *SOURCE_UPDATES} or not set(SETTINGS) <= set(settings):
        raise ValueError('Backup settings are incomplete or unsupported')
    validation.settings({key: settings[key] for key in SETTINGS}, SETTINGS)
    if 'topics' in settings:
        validation.topic_names(settings['topics'])
    if any(marker in settings and settings[marker] is not True for marker in SOURCE_UPDATES):
        raise ValueError('Backup has an invalid source-addition marker')
    if settings.get('sources_seeded') is not True:
        raise ValueError('Backup is missing its source initialization marker')
    if 'history_cleared_at' in settings:
        _check_date(settings['history_cleared_at'], required=True)
    for row in database.execute('SELECT * FROM sources'):
        value = dict(row)
        if value['enabled'] not in (0, 1) or value['removed'] not in (0, 1):
            raise ValueError('Backup contains invalid source flags')
        validation.source({**value, 'enabled': bool(value['enabled'])})
        for key in ('last_checked', 'last_success', 'first_checked', 'newest_published', 'last_new_story'):
            _check_date(value[key])
        for key in ('last_error', 'collection_note'):
            _check_text(value[key], 2000, 'source diagnostics', nullable=True)
        if type(value['consecutive_failures']) is not int or value['consecutive_failures'] < 0:
            raise ValueError('Backup contains invalid failure counts')
    for row in database.execute('SELECT * FROM stories'):
        value = dict(row)
        for key, limit in [('headline', 500), ('url', 2048), ('excerpt', 16000), ('article_text', 100000), ('summary', 2400), ('why_it_matters', 1000)]:
            _check_text(value[key], limit, key)
        valid_url(value['url'])
        tags = json.loads(value['tags'])
        if not isinstance(tags, list) or len(tags) > 100 or any(not isinstance(t, str) or len(t) > 60 for t in tags):
            raise ValueError('Backup contains invalid story tags')
        if value['status'] not in ('pending', 'reviewed', 'filtered', 'duplicate') or value['bookmarked'] not in (0, 1):
            raise ValueError('Backup contains invalid story state')
        if type(value['score']) is not int or not 0 <= value['score'] <= 100:
            raise ValueError('Backup contains invalid relevance scores')
        _check_date(value['discovered_at'], required=True)
        _check_date(value['published_at'])
        _check_date(value['reviewed_at'])
        _check_date(value.get('read_at'))
        datetime.strptime(value['day'], '%Y-%m-%d')
    for row in database.execute('SELECT * FROM runs'):
        if row['status'] not in ('running', 'complete', 'partial', 'failed', 'interrupted') or row['kind'] not in ('collect', 'review'):
            raise ValueError('Backup contains invalid run history')
        _check_date(row['started_at'], required=True)
        _check_date(row['finished_at'])
        details = json.loads(row['details'])
        if not isinstance(details, dict) or not isinstance(details.get('errors', []), list) or any(not isinstance(error, str) for error in details.get('errors', [])):
            raise ValueError('Backup contains invalid run details')


def extract_and_validate(store, upload, directory):
    """Only write the two expected members to controlled paths, with size bounds."""
    database_path = Path(directory) / 'restore.db'
    try:
        with zipfile.ZipFile(upload) as archive:
            members = archive.infolist()
            if len(members) != 2 or {m.filename for m in members} != {'manifest.json', 'newsreview.db'}:
                raise ValueError('Choose a NewsReview export ZIP containing only manifest.json and newsreview.db')
            for member in members:
                limit = 64000 if member.filename == 'manifest.json' else MAX_DATABASE
                if member.file_size > limit or member.flag_bits & 1 or member.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                    raise ValueError('Backup member is encrypted, unsupported, or exceeds its size limit')
            manifest = json.loads(archive.read('manifest.json'))
            if not isinstance(manifest, dict) or manifest.get('format') != FORMAT or manifest.get('format_version') != 1 or manifest.get('schema_version') not in (2, SCHEMA_VERSION):
                raise ValueError('This is not a supported NewsReview backup format')
            if _version(manifest.get('application_version')) > _version(__version__):
                raise ValueError('This backup is from a newer NewsReview version. Update the application first.')
            with archive.open('newsreview.db') as source, database_path.open('wb') as destination:
                total = 0
                while chunk := source.read(65536):
                    total += len(chunk)
                    if total > MAX_DATABASE:
                        raise ValueError('Uncompressed backup database exceeds 1 GiB')
                    destination.write(chunk)
        if _digest(database_path) != manifest.get('sha256'):
            raise ValueError('Backup database checksum does not match its manifest')
        with closing(_readonly(database_path)) as imported, store.connect() as live:
            objects = imported.execute("SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'").fetchall()
            if any(r['type'] not in ('table', 'index') for r in objects) or {r['name'] for r in objects if r['type'] == 'table'} != set(TABLES):
                raise ValueError('Backup has unexpected tables, triggers, or views')
            if any(r['sql'] and 'VIRTUAL TABLE' in r['sql'].upper() for r in objects):
                raise ValueError('Virtual tables are not accepted in backups')
            if imported.execute('PRAGMA user_version').fetchone()[0] != manifest['schema_version'] or imported.execute('PRAGMA integrity_check').fetchone()[0] != 'ok':
                raise ValueError('Backup database schema/integrity check failed')
            if imported.execute('PRAGMA foreign_key_check').fetchone():
                raise ValueError('Backup contains invalid source/duplicate relationships')
            for table in TABLES:
                expected = [tuple(r) for r in live.execute(f'PRAGMA table_info({table})')]
                if table == 'stories' and manifest['schema_version'] == 2:
                    expected = [column for column in expected if column[1] != 'read_at']
                actual = [tuple(r) for r in imported.execute(f'PRAGMA table_info({table})')]
                if expected != actual:
                    raise ValueError(f'Backup table layout does not match: {table}')
            _validate_rows(imported)
            totals = {table: imported.execute(f'SELECT COUNT(*) FROM {table}').fetchone()[0] for table in TABLES}
            if manifest.get('totals') != totals:
                raise ValueError('Backup totals do not match the database')
        return database_path, totals
    except (zipfile.BadZipFile, sqlite3.DatabaseError, json.JSONDecodeError, UnicodeError, KeyError, TypeError, OverflowError) as exc:
        raise ValueError('Invalid or damaged NewsReview backup ZIP') from exc


def restore_archive(store, upload):
    """Caller owns the collector lock. Restore rows transactionally, keeping live schema."""
    with tempfile.TemporaryDirectory(prefix='newsreview-import-') as directory:
        imported_path, totals = extract_and_validate(store, upload, directory)
        recovery_dir = store.path.parent / 'backups'
        recovery_dir.mkdir(mode=0o700, exist_ok=True)
        name = 'before-restore-' + datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S') + '-' + secrets.token_hex(4) + '.zip'
        with store.connect() as live, closing(_readonly(imported_path)) as imported:
            live.execute('BEGIN IMMEDIATE')
            # Other writers wait; a separate WAL reader snapshots the committed,
            # pre-transaction dataset. Failure here leaves the live archive intact.
            create_archive(store, recovery_dir / name)
            live.execute('PRAGMA defer_foreign_keys=ON')
            live.execute('UPDATE stories SET duplicate_of=NULL')
            for table in ('stories', 'runs', 'sources', 'settings'):
                live.execute(f'DELETE FROM {table}')
            for table in TABLES:
                columns = [r['name'] for r in live.execute(f'PRAGMA table_info({table})')]
                placeholders = ','.join('?' for _ in columns)
                names = ','.join('"' + column + '"' for column in columns)
                imported_columns = {row['name'] for row in imported.execute(f'PRAGMA table_info({table})')}
                selected = ','.join('"' + column + '"' if column in imported_columns else 'NULL AS "read_at"' for column in columns)
                live.executemany(f'INSERT INTO {table}({names}) VALUES ({placeholders})',
                                 (tuple(row) for row in imported.execute(f'SELECT {selected} FROM {table}')))
            initialize_topics(live)
            live.execute("INSERT INTO settings(key,value) VALUES ('data_revision',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (json.dumps(secrets.token_hex(16)),))
            live.execute("UPDATE runs SET status='interrupted',finished_at=? WHERE status='running'", (datetime.now(timezone.utc).isoformat(),))
            if live.execute('PRAGMA foreign_key_check').fetchone():
                raise ValueError('Restored relationships are invalid; changes were rolled back')
        return {'restored': True, 'totals': totals, 'recoveryBackup': name}
